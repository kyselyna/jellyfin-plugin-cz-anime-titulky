using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>
/// teamns.gensubs.cz – archive of the (ended) Team NShon'yaku group. hiyori only links the home page,
/// the archive has one zip per anime (finished: hotovo.php, unfinished: nedodelano.php), named by title.
/// The zip is found by comparing the archive titles with the AniList titles of the season.
/// </summary>
public sealed partial class TeamNsClient : SiteClient
{
    private const string Base = "https://teamns.gensubs.cz/";
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private Dictionary<string, string>? _index;
    private DateTime _indexAt;
    private (string Url, Payload Zip, DateTime At)? _lastZip;

    public TeamNsClient(ILogger<TeamNsClient> logger)
        : base(logger)
    {
    }

    public override string Name => "teamns.gensubs.cz";

    protected override (string User, string Password) Credentials => ("-", "-");

    protected override Task<bool> LoginAsync(CancellationToken ct) => Task.FromResult(true);

    /// <summary>Season zip for one of <paramref name="titles"/>, or null.</summary>
    public async Task<Payload?> DownloadAsync(IEnumerable<string> titles, CancellationToken ct)
    {
        var index = await IndexAsync(ct).ConfigureAwait(false);
        var url = Match(titles, index);
        if (url is null)
        {
            return null;
        }

        // A season is requested episode by episode – keep the last zip for a while.
        if (_lastZip is { } z && z.Url == url && z.At > DateTime.UtcNow.AddMinutes(-30))
        {
            return z.Zip;
        }

        var zip = await DownloadGetAsync(url, ct).ConfigureAwait(false);
        if (zip is not null)
        {
            _lastZip = (url, zip, DateTime.UtcNow);
        }

        return zip;
    }

    /// <summary>Normalized title → zip URL (the archive does not change; refreshed weekly).</summary>
    private async Task<Dictionary<string, string>> IndexAsync(CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_index is not null && _indexAt > DateTime.UtcNow.AddDays(-7))
            {
                return _index;
            }

            var map = new Dictionary<string, string>();
            foreach (var page in new[] { "hotovo.php", "nedodelano.php" })
            {
                var (html, _) = await GetHtmlAsync(Base + page, ct).ConfigureAwait(false);
                foreach (var div in Parse(html).QuerySelectorAll("[onclick]"))
                {
                    var m = ZipLink().Match(div.GetAttribute("onclick") ?? string.Empty);
                    var title = Normalize(div.TextContent);
                    if (m.Success && title.Length > 0 && title != "info")
                    {
                        map.TryAdd(title, Base + m.Groups[1].Value);
                    }
                }
            }

            Logger.LogInformation("teamns.gensubs.cz: {Count} archives", map.Count);
            _index = map;
            _indexAt = DateTime.UtcNow;
            return map;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Best archive for the titles: similarity ≥ 0.9 (the archive has typos and variants such as
    /// "Love Comedy" vs "Love Come") and the same season markers (numbers, "∬"), so that season 1
    /// never gets the "Part 2" archive.
    /// </summary>
    public static string? Match(IEnumerable<string> titles, IReadOnlyDictionary<string, string> index)
    {
        string? best = null;
        var bestScore = 0.9;
        foreach (var t in titles.Select(Normalize).Where(t => t.Length >= 3))
        {
            if (index.TryGetValue(t, out var exact))
            {
                return exact;
            }

            foreach (var (name, url) in index)
            {
                if (Markers(name) != Markers(t))
                {
                    continue;
                }

                var score = Similarity(t, name);
                if (score > bestScore)
                {
                    (best, bestScore) = (url, score);
                }
            }
        }

        return best;
    }

    private static string Markers(string normalized) =>
        string.Concat(Regex.Matches(normalized, @"\d+|[^\p{L}\p{N}]").Select(m => m.Value + "|"));

    /// <summary>1 − Levenshtein distance / longer length.</summary>
    private static double Similarity(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            d[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }

        return 1.0 - ((double)d[b.Length] / Math.Max(a.Length, b.Length));
    }

    /// <summary>Lower-case letters, digits and symbols (keeps "∬" = 2nd season); drops spaces and punctuation.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            var cat = char.GetUnicodeCategory(ch);
            if (char.IsLetterOrDigit(ch) || cat is UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"'(archiv-zip/(?:hotovo|nedodelano)/[^']+\.zip)'")]
    private static partial Regex ZipLink();
}
