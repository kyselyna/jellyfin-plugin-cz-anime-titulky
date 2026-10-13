using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>
/// Pages of defunct fansub sites preserved on web.archive.org (gawsubs.com, ngfailsub.eu…). hiyori links
/// the archived project page; the snapshot links the files (often only some of them were archived).
/// Per episode: a file whose name carries the episode number, then the season pack (zip/rar), then the
/// plain "/download/N/" links in page order (name checked after the download). Raw files are fetched
/// with the "id_" form of the snapshot URL so the archive returns the original bytes.
/// </summary>
public sealed partial class WaybackClient : SiteClient
{
    private const int MaxTries = 4; // web.archive.org is slow and rate-limited – stay modest

    public WaybackClient(ILogger<WaybackClient> logger)
        : base(logger)
    {
    }

    public override string Name => "web.archive.org";

    protected override (string User, string Password) Credentials => ("-", "-");

    // The Wayback Machine blocks clients that ask too often ("suspected abusive bot traffic").
    protected override int MinDelayMs => 5000;

    // A browser name on a non-browser client looks like a bot in disguise to the archive – say who we are.
    protected override string UserAgent => "Jellyfin-CzAnimeTitulky (+https://github.com/kyselyna/jellyfin-plugin-cz-anime-titulky)";

    protected override Task<bool> LoginAsync(CancellationToken ct) => Task.FromResult(true);

    public async Task<Payload?> DownloadAsync(string url, int episode, CancellationToken ct)
    {
        if (!IsOnHost(url, "web.archive.org"))
        {
            return null;
        }

        var (html, final) = await GetHtmlAsync(url, ct).ConfigureAwait(false);
        ThrowIfBlocked(html);
        if (!IsOnHost(final.ToString(), "web.archive.org"))
        {
            return null;
        }

        var links = Parse(html).QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href")!)
            .Select(h => Uri.TryCreate(final, h, out var u) ? u.ToString() : null)
            .Where(h => h is not null && IsOnHost(h, "web.archive.org") && Snapshot().IsMatch(h))
            .Select(h => h!)
            .Distinct()
            .ToList();

        var subtitles = links.Where(h => SubtitleFile().IsMatch(Uri.UnescapeDataString(h))).ToList();
        var exact = subtitles.Where(h => EpisodeOf(h) == episode).ToList();
        var packs = links.Where(h => PackFile().IsMatch(Uri.UnescapeDataString(h))).ToList();
        var tries = 0;
        if (links.Count == 0)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(Parse(html).Body?.TextContent ?? html, @"\s+", " ").Trim();
            Util.ActivityLog.Write($"ARCHIV    {final}: stránka bez odkazů ({html.Length} znaků): {text[..Math.Min(160, text.Length)]}");
            throw new HttpRequestException("web.archive.org vrátil stránku bez odkazů (přetížení / limit dotazů) – zkusí se později");
        }

        Logger.LogDebug("{Page}: {Links} links, {Subs} subtitle files, {Exact} for episode {Ep}, {Packs} packs", final, links.Count, subtitles.Count, exact.Count, episode, packs.Count);

        foreach (var h in exact.Concat(packs))
        {
            if (tries++ >= MaxTries)
            {
                return null;
            }

            var p = await DownloadGetAsync(Raw(h), ct).ConfigureAwait(false);
            Logger.LogDebug("{Url}: {Result}", Raw(h), p is null ? "nothing" : p.FileName);
            if (p is not null)
            {
                return p;
            }

            // An HTML answer instead of the file may be the bot block – then retry later instead of rejecting.
            ThrowIfBlocked((await GetHtmlAsync(Raw(h), ct).ConfigureAwait(false)).Html);
        }

        // Unnamed download links (e.g. ngfailsub.eu/download/5097/) in page order; check the real name.
        var unnamed = links.Where(h => DownloadLink().IsMatch(h)).ToList();
        var index = episode - 1;
        while (tries++ < MaxTries && index >= 0 && index < unnamed.Count)
        {
            var p = await DownloadGetAsync(Raw(unnamed[index]), ct).ConfigureAwait(false);
            if (p is null)
            {
                return null;
            }

            var n = EpisodeNumber(p.FileName);
            if (n == episode || PackFile().IsMatch(p.FileName))
            {
                return p;
            }

            if (n is null)
            {
                return null;
            }

            index += episode - n.Value;
        }

        return null;
    }

    /// <summary>A temporary block is a failed download (retried later), not a rejected subtitle.</summary>
    private static void ThrowIfBlocked(string html)
    {
        if (html.Length < 5000 && html.Contains("abusive bot traffic", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("web.archive.org dočasně blokuje časté dotazy – zkusí se později");
        }
    }

    /// <summary>"…/web/20190917005433/http://…" → "…/web/20190917005433id_/http://…" (original bytes).</summary>
    private static string Raw(string snapshot) => Snapshot().Replace(snapshot, m => m.Groups[1].Value + "id_/", 1);

    private static int? EpisodeOf(string url) => EpisodeNumber(Path.GetFileName(Uri.UnescapeDataString(new Uri(url).AbsolutePath)));

    /// <summary>Last stand-alone 1–3 digit number in the file name ("Show II - 16 GAW-Subs", "Show-hen-05"); "14.5" is not 14.</summary>
    internal static int? EpisodeNumber(string fileName)
    {
        var ms = NameEpisode().Matches(Path.GetFileNameWithoutExtension(fileName));
        return ms.Count == 0 ? null : int.Parse(ms[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(/web/\d{8,14})(?:[a-z]{2}_)?/", RegexOptions.IgnoreCase)]
    private static partial Regex Snapshot();

    [GeneratedRegex(@"\.(ass|ssa|srt)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubtitleFile();

    [GeneratedRegex(@"\.(zip|rar)$", RegexOptions.IgnoreCase)]
    private static partial Regex PackFile();

    [GeneratedRegex(@"/download/\d+/?$", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadLink();

    [GeneratedRegex(@"(?:^|[\s_\-\[])(\d{1,3})(?:v\d)?(?=[\s_\-\]]|\.(?!\d)|$)")]
    private static partial Regex NameEpisode();
}
