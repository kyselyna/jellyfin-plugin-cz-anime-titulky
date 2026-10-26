using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>
/// hns.sk – Hentai &amp; Senpai subs. Anime page "/anime/&lt;slug&gt;" lists episode pages
/// "/anime/episode/&lt;slug&gt;/&lt;id&gt;", each with a POST download form (action=download).
/// </summary>
public sealed partial class HnsClient : SiteClient
{
    private const string Base = "https://hns.sk";
    private readonly Util.JsonCache<Dictionary<int, string>> _episodes = new("hns-episodes");
    private readonly Util.JsonCache<Dictionary<string, string>> _list = new("hns-animelist");

    public HnsClient(ILogger<HnsClient> logger)
        : base(logger)
    {
    }

    public override string Name => "hns.sk";

    protected override (string User, string Password) Credentials => (Config.HnsEmail, Config.HnsPassword);

    protected override async Task<bool> LoginAsync(CancellationToken ct)
    {
        var (html, _) = await GetHtmlAsync(Base + "/site/login", ct).ConfigureAwait(false);
        var form = Parse(html).QuerySelectorAll("form").FirstOrDefault(f => f.QuerySelector("input[type=password]") is not null);
        if (form is null)
        {
            return false;
        }

        var fields = FormFields(form);
        foreach (var name in fields.Select(f => f.Key).ToList())
        {
            if (name.Contains("pass", StringComparison.OrdinalIgnoreCase))
            {
                Set(fields, name, Credentials.Password);
            }
            else if (Regex.IsMatch(name, "user|login|email|name", RegexOptions.IgnoreCase) && fields.First(f => f.Key == name).Value.Length == 0)
            {
                Set(fields, name, Credentials.User);
            }
        }

        var action = new Uri(new Uri(Base + "/site/login"), form.GetAttribute("action") ?? "/site/login").ToString();
        if (!IsOnHost(action, "hns.sk"))
        {
            return false; // never send credentials anywhere else
        }

        var (resp, uri) = await PostFormAsync(action, fields, ct).ConfigureAwait(false);
        return !uri.AbsolutePath.Contains("site/login", StringComparison.OrdinalIgnoreCase)
            && (resp.Contains("logout", StringComparison.OrdinalIgnoreCase) || resp.Contains("odhlás", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Payload?> DownloadAsync(string url, int episode, IEnumerable<string> titles, CancellationToken ct)
    {
        await EnsureLoginAsync(ct).ConfigureAwait(false);
        // hiyori links www.hns.sk, but the login cookie belongs to hns.sk
        url = url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase).Replace("://www.hns.sk", "://hns.sk", StringComparison.OrdinalIgnoreCase);
        if (!IsOnHost(url, "hns.sk"))
        {
            return null;
        }

        string? epUrl = null;
        if (url.Contains("/episode/", StringComparison.Ordinal))
        {
            epUrl = url;
        }
        else
        {
            var episodes = await EpisodesAsync(url, ct).ConfigureAwait(false);
            // hns renames anime pages (DanMachi II: hiyori still links the old slug, which redirects to the home page)
            // → find the anime in the A–Z list by its title.
            if (episodes.Count == 0 && await FindByTitleAsync(titles, ct).ConfigureAwait(false) is { } moved && moved != url)
            {
                Logger.LogInformation("hns.sk: {Old} moved to {New}", url, moved);
                episodes = await EpisodesAsync(moved, ct).ConfigureAwait(false);
            }

            epUrl = episodes.GetValueOrDefault(episode);
        }
        if (epUrl is null)
        {
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (html, _) = await GetHtmlAsync(epUrl, ct).ConfigureAwait(false);
            foreach (var form in Parse(html).QuerySelectorAll("form"))
            {
                if (!string.Equals(form.GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fields = FormFields(form);
                if (fields.FirstOrDefault(f => f.Key == "action").Value != "download")
                {
                    continue;
                }

                var name = fields.FirstOrDefault(f => f.Key == "name").Value ?? "subtitle.ass";
                var p = await DownloadPostAsync(epUrl, fields, name, ct).ConfigureAwait(false);
                if (p is not null)
                {
                    return p;
                }
            }

            if (!await EnsureLoginAsync(ct, force: true).ConfigureAwait(false))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>Episode number → episode page URL for an anime page.</summary>
    private async Task<Dictionary<int, string>> EpisodesAsync(string animeUrl, CancellationToken ct)
    {
        if (_episodes.TryGet(animeUrl, TimeSpan.FromDays(3), out var cached) && cached is { Count: > 0 })
        {
            return cached;
        }

        var map = new Dictionary<int, string>();
        var slug = animeUrl.TrimEnd('/').Split('/')[^1];
        var (html, _) = await GetHtmlAsync(animeUrl, ct).ConfigureAwait(false);
        var links = Parse(html).QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href")!)
            .Where(h => Regex.IsMatch(h, "^/anime/episode/" + Regex.Escape(slug) + @"/\d+$"))
            .Distinct();
        foreach (var href in links)
        {
            var (page, _) = await GetHtmlAsync(Base + href, ct).ConfigureAwait(false);
            // TextContent glues elements together without spaces ("- 19Zpět na anime").
            var text = Regex.Replace(Parse(page).Body?.TextContent ?? string.Empty, @"\s+", " ");
            var m = EpisodeNumber().Match(text);
            if (m.Success)
            {
                map[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)] = Base + href;
            }
        }

        _episodes.Set(animeUrl, map);
        return map;
    }

    /// <summary>Anime page URL from the A–Z list (/animelist) whose name equals one of the titles.</summary>
    private async Task<string?> FindByTitleAsync(IEnumerable<string> titles, CancellationToken ct)
    {
        var wanted = titles.Select(Normalize).Where(t => t.Length > 0).ToHashSet();
        if (wanted.Count == 0)
        {
            return null;
        }

        if (!_list.TryGet("animelist", TimeSpan.FromDays(1), out var list) || list is not { Count: > 0 })
        {
            var (html, _) = await GetHtmlAsync(Base + "/animelist", ct).ConfigureAwait(false);
            list = new Dictionary<string, string>();
            foreach (var a in Parse(html).QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href")!;
                if (AnimePage().IsMatch(href))
                {
                    list.TryAdd(Normalize(a.TextContent), Base + href);
                }
            }

            _list.Set("animelist", list);
        }

        return list.Where(kv => wanted.Contains(kv.Key)).Select(kv => kv.Value).FirstOrDefault();
    }

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", string.Empty);

    [GeneratedRegex(@"^/anime/(?!episode/)[^/]+$")]
    private static partial Regex AnimePage();

    [GeneratedRegex(@" - (\d+)\s*Zpět na anime")]
    private static partial Regex EpisodeNumber();
}
