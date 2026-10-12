using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>
/// wosir.cz (WOSUBS + Širase). hiyori links redirect to "/anime?id=N"; that page has a table
/// (div.subs tr.tsubs) with one row per episode and POST forms (form.form-test) for the TV and
/// the BD version. The "download all" zips are incomplete – always download per episode.
/// </summary>
public sealed partial class WosirClient : SiteClient
{
    private const string Base = "https://www.wosir.cz";

    public WosirClient(ILogger<WosirClient> logger)
        : base(logger)
    {
    }

    public override string Name => "wosir.cz";

    protected override (string User, string Password) Credentials => (Config.WosirUsername, Config.WosirPassword);

    protected override async Task<bool> LoginAsync(CancellationToken ct)
    {
        var (html, _) = await GetHtmlAsync(Base + "/prihlaseni", ct).ConfigureAwait(false);
        var form = Parse(html).QuerySelector("form");
        if (form is null)
        {
            return false;
        }

        var fields = FormFields(form);
        Set(fields, "prezdivka", Credentials.User);
        Set(fields, "heslo", Credentials.Password);
        Set(fields, "sub", string.Empty);
        var (resp, uri) = await PostFormAsync(Base + "/prihlaseni", fields, ct).ConfigureAwait(false);
        return !uri.AbsolutePath.Contains("prihlaseni", StringComparison.OrdinalIgnoreCase)
            && (resp.Contains("odhl", StringComparison.OrdinalIgnoreCase) || resp.Contains("logout", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="preferBd">True when the video is a Blu-ray release.</param>
    public async Task<Payload?> DownloadAsync(string url, int episode, bool preferBd, CancellationToken ct)
    {
        if (!IsOnHost(url, "wosir.cz"))
        {
            return null;
        }

        await EnsureLoginAsync(ct).ConfigureAwait(false);
        var (_, final) = await GetHtmlAsync(url, ct).ConfigureAwait(false);
        var m = AnimeId().Match(final.ToString());
        if (!m.Success)
        {
            return null;
        }

        var page = Base + "/anime?id=" + m.Groups[1].Value;
        var (html, _) = await GetHtmlAsync(page, ct).ConfigureAwait(false);
        var table = Parse(html).QuerySelector("div.subs");
        if (table is null)
        {
            return null;
        }

        foreach (var tr in table.QuerySelectorAll("tr.tsubs"))
        {
            var first = tr.QuerySelector("td")?.TextContent.Trim();
            if (!int.TryParse(first, out var ep) || ep != episode)
            {
                continue;
            }

            var forms = tr.QuerySelectorAll("form.form-test").Select(FormFields)
                .OrderBy(f => f.Any(x => x.Key == "dwl_bd") != preferBd)
                .ToList();
            foreach (var fields in forms)
            {
                var p = await DownloadPostAsync(page, fields, $"wosir-{episode:000}.ass", ct).ConfigureAwait(false);
                if (p is not null)
                {
                    return p;
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"anime\?id=(\d+)")]
    private static partial Regex AnimeId();
}
