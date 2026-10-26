using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>One row of the hiyori.cz subtitle table ("#subs-panel").</summary>
public sealed record HiyoriRow(
    string Id,
    string Episode,
    string Title,
    string Lang,
    string Release,
    string Group,
    string Version,
    string? Url);

/// <summary>A hiyori.cz anime page: its AniList id and the subtitle rows.</summary>
public sealed record HiyoriAnime(string Path, int? AniListId, IReadOnlyList<HiyoriRow> Rows, DateTime Fetched, int? MalId = null);

/// <summary>
/// hiyori.cz – the Czech/Slovak fansub aggregator. Provides the list of subtitles per anime
/// and links to the fansub sites (or a direct download hosted by hiyori).
/// </summary>
public sealed partial class HiyoriClient : SiteClient
{
    public const string Base = "https://hiyori.cz";
    private readonly ConcurrentDictionary<string, HiyoriAnime> _pages = new();

    public HiyoriClient(ILogger<HiyoriClient> logger)
        : base(logger)
    {
    }

    public override string Name => "hiyori.cz";

    protected override (string User, string Password) Credentials => (Config.HiyoriUsername, Config.HiyoriPassword);

    protected override async Task<bool> LoginAsync(CancellationToken ct)
    {
        await GetHtmlAsync(Base + "/", ct).ConfigureAwait(false);
        var (html, uri) = await PostFormAsync(
            Base + "/account/login",
            new Dictionary<string, string>
            {
                ["username"] = Credentials.User,
                ["Password"] = Credentials.Password,
                ["remember_me"] = "true",
            },
            ct).ConfigureAwait(false);
        if (IsLoggedIn(html))
        {
            return true;
        }

        var (home, _) = await GetHtmlAsync(Base + "/", ct).ConfigureAwait(false);
        return IsLoggedIn(home) || home.Contains(Credentials.User, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full-text search; returns anime page paths like "/anime/123".</summary>
    public async Task<List<string>> SearchAsync(string title, CancellationToken ct)
    {
        await EnsureLoginAsync(ct).ConfigureAwait(false);
        var q = title.Length > 60 ? title[..60] : title;
        var form = new Dictionary<string, string> { ["nazev"] = q };
        var (html, _) = await PostFormAsync(Base + "/rozcestnik", form, ct).ConfigureAwait(false);
        // A logged-out search finds nothing, and the session expires after a few days of uptime.
        if (!IsLoggedIn(html) && await EnsureLoginAsync(ct, force: true).ConfigureAwait(false))
        {
            (html, _) = await PostFormAsync(Base + "/rozcestnik", form, ct).ConfigureAwait(false);
        }

        return Parse(html).QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href")!)
            .Where(h => AnimePath().IsMatch(h))
            .Distinct()
            .ToList();
    }

    /// <summary>Anime detail page (cached for <see cref="Configuration.PluginConfiguration.CacheHours"/>).</summary>
    public async Task<HiyoriAnime> GetAnimeAsync(string path, CancellationToken ct)
    {
        if (!AnimePath().IsMatch(path))
        {
            throw new ArgumentException("Not a hiyori anime path", nameof(path));
        }

        if (_pages.TryGetValue(path, out var cached) && cached.Fetched > DateTime.UtcNow.AddHours(-Math.Max(1, Config.CacheHours)))
        {
            return cached;
        }

        // The subtitle table is only shown to logged-in users.
        await EnsureLoginAsync(ct).ConfigureAwait(false);
        var (html, _) = await GetHtmlAsync(Base + path, ct).ConfigureAwait(false);
        if (Parse(html).GetElementById("subs-panel") is null && await EnsureLoginAsync(ct, force: true).ConfigureAwait(false))
        {
            (html, _) = await GetHtmlAsync(Base + path, ct).ConfigureAwait(false);
        }

        var m = AniListLink().Match(html);
        var mal = MalLink().Match(html);
        var rows = new List<HiyoriRow>();
        var panel = Parse(html).GetElementById("subs-panel");
        if (panel is not null)
        {
            foreach (var tr in panel.QuerySelectorAll("tr").Skip(1))
            {
                var td = tr.QuerySelectorAll("td").ToList();
                if (td.Count < 11)
                {
                    continue;
                }

                string T(int i) => Regex.Replace(td[i].TextContent, @"\s+", " ").Trim();
                rows.Add(new HiyoriRow(T(0), T(2), T(3), T(4).ToUpperInvariant(), T(5), T(6), T(8), td[10].QuerySelector("a[href]")?.GetAttribute("href")));
            }
        }

        var anime = new HiyoriAnime(path, m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null, rows, DateTime.UtcNow,
            mal.Success ? int.Parse(mal.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null);
        if (panel is not null || anime.AniListId is not null)
        {
            _pages[path] = anime; // a logged-out page (no table, no AniList link) is not cached
        }

        return anime;
    }

    /// <summary>Downloads a subtitle hosted directly on hiyori ("/anime/downloadsubtitles?id=N").</summary>
    public async Task<Payload?> DownloadAsync(string url, CancellationToken ct)
    {
        await EnsureLoginAsync(ct).ConfigureAwait(false);
        var full = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : Base + url;
        if (!IsOnHost(full, "hiyori.cz"))
        {
            return null;
        }

        var p = await DownloadGetAsync(full, ct).ConfigureAwait(false);
        if (p is null && await EnsureLoginAsync(ct, force: true).ConfigureAwait(false))
        {
            p = await DownloadGetAsync(full, ct).ConfigureAwait(false);
        }

        return p;
    }

    private static bool IsLoggedIn(string html) =>
        html.Contains("odhlás", StringComparison.OrdinalIgnoreCase) || html.Contains("logout", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^/anime/\d+$")]
    private static partial Regex AnimePath();

    [GeneratedRegex(@"anilist\.co/anime/(\d+)")]
    private static partial Regex AniListLink();

    [GeneratedRegex(@"myanimelist\.net/anime/(\d+)")]
    private static partial Regex MalLink();
}
