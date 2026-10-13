using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>
/// ange.3mka.cz (3Mka, Joomla + jDownloads). The project page "/anime/preklady/&lt;slug&gt;" lists one
/// "/component/jdownloads/send/&lt;cat&gt;-&lt;slug&gt;/&lt;id&gt;-&lt;episode title&gt;" link per episode in episode order
/// (plus fonts and a pack of all episodes). Links carry the episode title, not the number, so the
/// number is checked against the downloaded file name. No login needed.
/// </summary>
public sealed partial class AngeClient : SiteClient
{
    private const string Base = "https://ange.3mka.cz";

    public AngeClient(ILogger<AngeClient> logger)
        : base(logger)
    {
    }

    public override string Name => "ange.3mka.cz";

    protected override (string User, string Password) Credentials => ("-", "-");

    protected override Task<bool> LoginAsync(CancellationToken ct) => Task.FromResult(true);

    public async Task<Payload?> DownloadAsync(string url, int episode, CancellationToken ct)
    {
        if (!IsOnHost(url, "ange.3mka.cz") || !Uri.TryCreate(url, UriKind.Absolute, out var page))
        {
            return null;
        }

        var slug = page.AbsolutePath.TrimEnd('/').Split('/')[^1];
        var (html, _) = await GetHtmlAsync(url, ct).ConfigureAwait(false);

        // The menu also lists the newest files of other projects – keep only this project's category.
        var files = new List<(int Id, string Href)>();
        foreach (var href in Parse(html).QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href")!).Distinct())
        {
            var m = SendLink().Match(href);
            if (m.Success && m.Groups["slug"].Value.Equals(slug, StringComparison.OrdinalIgnoreCase))
            {
                files.Add((int.Parse(m.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture), Base + m.Value));
            }
        }

        var episodes = files.Where(f => !NotEpisode().IsMatch(f.Href)).OrderBy(f => f.Id).Select(f => f.Href).ToList();
        var pack = files.Where(f => Pack().IsMatch(f.Href)).Select(f => f.Href).FirstOrDefault();

        // Position = episode number; one correction step when the list has extras or gaps.
        var index = episode - 1;
        for (var attempt = 0; attempt < 2 && index >= 0 && index < episodes.Count; attempt++)
        {
            var p = await DownloadGetAsync(episodes[index], ct).ConfigureAwait(false);
            if (p is null)
            {
                break;
            }

            var n = EpisodeNumber(p.FileName);
            if (n == episode || (n is null && episodes.Count == 1 && episode == 1))
            {
                return p;
            }

            if (n is null)
            {
                break;
            }

            index += episode - n.Value;
        }

        // The pack of all episodes – PayloadExtractor picks the episode by file name.
        return pack is null ? null : await DownloadGetAsync(pack, ct).ConfigureAwait(false);
    }

    internal static int? EpisodeNumber(string fileName)
    {
        var m = FileEpisode().Match(Path.GetFileNameWithoutExtension(fileName));
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"/component/jdownloads/send/\d+-(?<slug>[a-z0-9-]+)/(?<id>\d+)-[a-z0-9-]+", RegexOptions.IgnoreCase)]
    private static partial Regex SendLink();

    [GeneratedRegex(@"/\d+-(fonty?|balik[a-z0-9-]*|vse[a-z0-9-]*|komplet[a-z0-9-]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotEpisode();

    [GeneratedRegex(@"/\d+-(balik|vse|komplet)[a-z0-9-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Pack();

    // "Show - 01v2 [3Mka]", "Show - 12 [3Mka]", "Show S02E03"
    [GeneratedRegex(@"(?:\s-\s|E)(\d{1,3})(?:v\d)?(?=\s|\[|$|\.)", RegexOptions.IgnoreCase)]
    private static partial Regex FileEpisode();
}
