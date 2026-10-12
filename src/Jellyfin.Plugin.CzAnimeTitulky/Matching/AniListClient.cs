using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.CzAnimeTitulky.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Matching;

/// <summary>Minimal AniList entry.</summary>
public sealed class AniMedia
{
    public int Id { get; set; }

    public string? Format { get; set; }

    public int? Episodes { get; set; }

    public string? Romaji { get; set; }

    public string? English { get; set; }

    public List<string> Synonyms { get; set; } = new();

    public int? SequelId { get; set; }

    /// <summary>Sequel that is not a TV season (special, OVA, movie) – the chain may continue behind it.</summary>
    public int? BridgeId { get; set; }

    public IEnumerable<string> Titles() =>
        new[] { Romaji, English }.Concat(Synonyms.Take(3)).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct()!;
}

/// <summary>AniList GraphQL client (titles and the TV sequel chain = seasons).</summary>
public sealed class AniListClient
{
    private const string MediaQuery =
        "query($id:Int){Media(id:$id,type:ANIME){id format episodes title{romaji english} synonyms relations{edges{relationType node{id format type}}}}}";

    private const string SearchQuery =
        "query($s:String){Page(perPage:5){media(search:$s,type:ANIME){id}}}";

    private static readonly HashSet<string> SeasonFormats = new() { "TV", "TV_SHORT", "ONA" };
    private readonly ILogger<AniListClient> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonCache<AniMedia> _media = new("anilist-media-v2");
    private readonly JsonCache<int?> _search = new("anilist-search");

    public AniListClient(ILogger<AniListClient> logger)
    {
        _logger = logger;
    }

    public async Task<AniMedia?> GetAsync(int id, CancellationToken ct)
    {
        if (_media.TryGet(id.ToString(System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromDays(7), out var cached))
        {
            return cached;
        }

        var data = await QueryAsync(MediaQuery, new { id }, ct).ConfigureAwait(false);
        var m = data?["Media"];
        if (m is null)
        {
            return null;
        }

        var media = new AniMedia
        {
            Id = (int)m["id"]!,
            Format = (string?)m["format"],
            Episodes = (int?)m["episodes"],
            Romaji = (string?)m["title"]?["romaji"],
            English = (string?)m["title"]?["english"],
            Synonyms = m["synonyms"]?.AsArray().Select(x => (string?)x).Where(x => x is not null).ToList()! ?? new(),
        };
        foreach (var edge in m["relations"]?["edges"]?.AsArray() ?? new JsonArray())
        {
            var node = edge?["node"];
            if ((string?)edge?["relationType"] != "SEQUEL" || (string?)node?["type"] != "ANIME")
            {
                continue;
            }

            if (SeasonFormats.Contains((string?)node?["format"] ?? string.Empty))
            {
                media.SequelId ??= (int)node!["id"]!;
            }
            else
            {
                media.BridgeId ??= (int)node!["id"]!;
            }
        }

        _media.Set(media.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), media);
        return media;
    }

    /// <summary>The n-th season (1-based) of the TV sequel chain starting at <paramref name="firstId"/>.</summary>
    public async Task<AniMedia?> GetSeasonAsync(int firstId, int season, CancellationToken ct)
    {
        var seen = new HashSet<int>();
        int? cur = firstId;
        for (var n = 1; cur is not null && seen.Add(cur.Value) && n <= 20; n++)
        {
            var m = await GetAsync(cur.Value, ct).ConfigureAwait(false);
            if (m is null)
            {
                return null;
            }

            if (n == season)
            {
                return m;
            }

            cur = m.SequelId ?? await NextSeasonBehindBridgeAsync(m, seen, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Follows non-TV sequels (e.g. Dr. STONE S2 → "Ryuusui" special → S3) without counting them as seasons.
    /// </summary>
    private async Task<int?> NextSeasonBehindBridgeAsync(AniMedia m, HashSet<int> seen, CancellationToken ct)
    {
        var bridge = m.BridgeId;
        for (var hop = 0; bridge is not null && hop < 3 && seen.Add(bridge.Value); hop++)
        {
            var b = await GetAsync(bridge.Value, ct).ConfigureAwait(false);
            if (b is null)
            {
                return null;
            }

            if (b.SequelId is not null)
            {
                return b.SequelId;
            }

            bridge = b.BridgeId;
        }

        return null;
    }

    public async Task<int?> SearchAsync(string title, CancellationToken ct)
    {
        if (_search.TryGet(title, TimeSpan.FromDays(30), out var cached))
        {
            return cached;
        }

        var data = await QueryAsync(SearchQuery, new { s = title }, ct).ConfigureAwait(false);
        var id = (int?)data?["Page"]?["media"]?.AsArray().FirstOrDefault()?["id"];
        _search.Set(title, id);
        return id;
    }

    private async Task<JsonNode?> QueryAsync(string query, object variables, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var resp = await _http.PostAsJsonAsync("https://graphql.anilist.co", new { query, variables }, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(61);
                    _logger.LogInformation("AniList rate limit, waiting {Seconds} s", wait.TotalSeconds);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(800, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false);
                return json?["data"];
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "AniList request failed");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
