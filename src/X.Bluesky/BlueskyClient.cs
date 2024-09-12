﻿using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using X.Bluesky.Models;

namespace X.Bluesky;

[PublicAPI]
public interface IBlueskyClient
{
    /// <summary>
    /// Make post with link
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    Task Post(string text);

    /// <summary>
    /// Make post with link (page preview will be attached)
    /// </summary>
    /// <param name="text"></param>
    /// <param name="uri"></param>
    /// <returns></returns>
    Task Post(string text, Uri uri);
}

public class BlueskyClient : IBlueskyClient
{
    private readonly string _identifier;
    private readonly string _password;
    private readonly ILogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IReadOnlyCollection<string> _languages;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="httpClientFactory"></param>
    /// <param name="identifier">Bluesky identifier</param>
    /// <param name="password">Bluesky application password</param>
    /// <param name="languages">Post languages</param>
    /// <param name="logger"></param>
    public BlueskyClient(
        IHttpClientFactory httpClientFactory,
        string identifier,
        string password,
        IEnumerable<string> languages,
        ILogger<BlueskyClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _identifier = identifier;
        _password = password;
        _logger = logger;
        _languages = languages.ToImmutableList();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="httpClientFactory"></param>
    /// <param name="identifier">Bluesky identifier</param>
    /// <param name="password">Bluesky application password</param>
    public BlueskyClient(
        IHttpClientFactory httpClientFactory,
        string identifier,
        string password)
        : this(httpClientFactory, identifier, password, new[] { "en", "en-US" }, NullLogger<BlueskyClient>.Instance)
    {
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="identifier">Bluesky identifier</param>
    /// <param name="password">Bluesky application password</param>
    public BlueskyClient(string identifier, string password)
        : this(new HttpClientFactory(), identifier, password)
    {
    }

    private async Task CreatePost(string text, Uri? url)
    {
        var session = await Authorize(_identifier, _password);

        if (session == null)
        {
            throw new AuthenticationException();
        }

        // Fetch the current time in ISO 8601 format, with "Z" to denote UTC
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");


        var facets = await GetFacets(text);

        // Required fields for the post
        var post = new Post
        {
            Type = "app.bsky.feed.post",
            Text = text,
            CreatedAt = now,
            Langs = _languages.ToList(),
            Facets = facets 
        };

        if (url != null)
        {
            var embedCardBuilder = new EmbedCardBuilder(_httpClientFactory, _logger);

            post.Embed = new Embed
            {
                External = await embedCardBuilder.Create(url, session.AccessJwt),
                Type = "app.bsky.embed.external"
            };
        }

        var requestUri = "https://bsky.social/xrpc/com.atproto.repo.createRecord";

        var requestData = new CreatePostRequest
        {
            Repo = session.Did,
            Collection = "app.bsky.feed.post",
            Record = post,
        };

        var jsonRequest = JsonConvert.SerializeObject(requestData, Formatting.Indented, new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        });

        var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

        var httpClient = _httpClientFactory.CreateClient();

        // Add the Authorization header with the bearer token
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);

        var response = await httpClient.PostAsync(requestUri, content);

        // This throws an exception if the HTTP response status is an error code.
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<Facet>> GetFacets(string text)
    {
        const string pattern = "rb\"[$|\\W](@([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\.)+[a-zA-Z]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)\"";

        var matches = Regex.Matches(text, pattern);

        var facets = new List<Facet>();
        var facetBuilder = new FacetBuilder();
        
        foreach (Match match in matches)
        {
            if (match.Success)
            {
                var facetFeature = facetBuilder.Create(match.Value);

                if (facetFeature != null)
                {


                    facets.Add(new Facet
                    {
                        Index = new FacetIndex
                        {
                            ByteStart = 1,
                            ByteEnd = 1
                        },
                        Features = [facetFeature]
                    });
                }
            }
        }
        // {
        //     text: 'Go to this site',
        //     facets: [
        //     {
        //         index: {
        //             byteStart: 6,
        //             byteEnd: 15
        //         },
        //         features: [{
        //             $type: 'app.bsky.richtext.facet#link',
        //             uri: 'https://example.com'
        //         }]
        //     }
        //     ]
        // }

        return facets;
    }

    /// <summary>
    /// Create post
    /// </summary>
    /// <param name="text">Post text</param>
    /// <returns></returns>
    public Task Post(string text) => CreatePost(text, null);

    /// <summary>
    /// Create post with attached link
    /// </summary>
    /// <param name="text">Post text</param>
    /// <param name="uri">Link to webpage</param>
    /// <returns></returns>
    public Task Post(string text, Uri uri) => CreatePost(text, uri);

    /// <summary>
    /// Authorize in Bluesky
    /// </summary>
    /// <param name="identifier">Bluesky identifier</param>
    /// <param name="password">Bluesky application password</param>
    /// <returns>
    /// Instance of authorized session
    /// </returns>
    public async Task<Session?> Authorize(string identifier, string password)
    {
        var requestData = new
        {
            identifier = identifier,
            password = password
        };

        var json = JsonConvert.SerializeObject(requestData);

        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var httpClient = _httpClientFactory.CreateClient();

        var uri = "https://bsky.social/xrpc/com.atproto.server.createSession";
        var response = await httpClient.PostAsync(uri, content);

        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();

        return JsonConvert.DeserializeObject<Session>(jsonResponse);
    }
}