using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>
/// kamui-subs.cz – WordPress (Ultimate Member login). A project page lists "N. Díl" links to
/// "/download/&lt;id&gt;/" which return password-protected archives (password from the settings).
/// </summary>
public sealed partial class KamuiClient : SiteClient
{
    private const string Base = "https://kamui-subs.cz/";

    public KamuiClient(ILogger<KamuiClient> logger)
        : base(logger)
    {
    }

    public override string Name => "kamui-subs.cz";

    protected override (string User, string Password) Credentials => (Config.KamuiUsername, Config.KamuiPassword);

    protected override async Task<bool> LoginAsync(CancellationToken ct)
    {
        // The home page is served from a cache with a stale nonce → use the login page.
        var login = Base + "login/";
        var (html, _) = await GetHtmlAsync(login, ct).ConfigureAwait(false);
        var form = Parse(html).QuerySelectorAll("form").FirstOrDefault(f => f.QuerySelector("input[type=password]") is not null);
        if (form is null)
        {
            return false;
        }

        var fields = FormFields(form);
        foreach (var name in fields.Select(f => f.Key).ToList())
        {
            if (name.StartsWith("username-", StringComparison.Ordinal))
            {
                Set(fields, name, Credentials.User);
            }
            else if (name.StartsWith("user_password-", StringComparison.Ordinal))
            {
                Set(fields, name, Credentials.Password);
            }
        }

        Set(fields, "rememberme", "1");
        await PostFormAsync(login, fields, ct).ConfigureAwait(false);
        return Cookies.GetAllCookies().Any(c => c.Name.StartsWith("wordpress_logged_in", StringComparison.Ordinal));
    }

    public async Task<Payload?> DownloadAsync(string url, int episode, CancellationToken ct)
    {
        await EnsureLoginAsync(ct).ConfigureAwait(false);
        if (!IsOnHost(url, "kamui-subs.cz"))
        {
            return null;
        }

        var dl = url.Contains("/download/", StringComparison.Ordinal) ? url : await FindEpisodeAsync(url, episode, ct).ConfigureAwait(false);
        if (dl is null || !IsOnHost(dl, "kamui-subs.cz"))
        {
            return null;
        }

        var p = await DownloadGetAsync(dl, ct).ConfigureAwait(false);
        if (p is null && await EnsureLoginAsync(ct, force: true).ConfigureAwait(false))
        {
            p = await DownloadGetAsync(dl, ct).ConfigureAwait(false);
        }

        return p;
    }

    private async Task<string?> FindEpisodeAsync(string projectUrl, int episode, CancellationToken ct)
    {
        var (html, _) = await GetHtmlAsync(projectUrl, ct).ConfigureAwait(false);
        foreach (var a in Parse(html).QuerySelectorAll("a[href]"))
        {
            var m = EpisodeLink().Match(Regex.Replace(a.TextContent, @"\s+", " ").Trim());
            var href = a.GetAttribute("href")!;
            if (m.Success && href.Contains("/download/", StringComparison.Ordinal)
                && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == episode)
            {
                return new Uri(new Uri(projectUrl), href).ToString();
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(\d+)\.\s*Díl", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeLink();
}
