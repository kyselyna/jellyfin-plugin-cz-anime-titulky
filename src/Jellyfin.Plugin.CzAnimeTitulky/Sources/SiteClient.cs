using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>A downloaded file (subtitle or archive).</summary>
public sealed record Payload(byte[] Content, string FileName);

/// <summary>
/// One website session: its own cookie jar, polite throttling and lazy login.
/// </summary>
public abstract class SiteClient : IDisposable
{
    private static readonly HtmlParser Parser = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    /// <summary>Largest accepted download (subtitles are tiny; archives of a whole season stay well below).</summary>
    public const long MaxDownloadBytes = 20 * 1024 * 1024;

    private DateTime _last = DateTime.MinValue;
    private DateOnly _budgetDay;
    private int _budgetUsed;
    private bool _loggedIn;
    private string _loginKey = string.Empty;

    protected SiteClient(ILogger logger)
    {
        Logger = logger;
        Cookies = new CookieContainer();
        var handler = new SocketsHttpHandler
        {
            CookieContainer = Cookies,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            MaxResponseHeadersLength = 64,
        };
        // All sites are public: never connect into the local network, also not after a redirect.
        {
            handler.ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
                var ip = addresses.FirstOrDefault(a => !IsPrivate(a))
                    ?? throw new HttpRequestException($"Blocked non-public address for {ctx.DnsEndPoint.Host}");
                if (addresses.Any(IsPrivate))
                {
                    throw new HttpRequestException($"Blocked host {ctx.DnsEndPoint.Host} resolving to a private address");
                }

                var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
                    return new System.Net.Sockets.NetworkStream(socket, true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = MaxDownloadBytes };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("cs,en;q=0.8");
    }

    protected ILogger Logger { get; }

    /// <summary>Browser-like by default (some fansub sites serve bots differently); always names the plugin.</summary>
#pragma warning disable CA2214 // read in the constructor: overrides return constants
    protected virtual string UserAgent =>
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Jellyfin-CzAnimeTitulky";
#pragma warning restore CA2214

    /// <summary>Lower bound for the pause between two requests to this site (slow or strict sites).</summary>
    protected virtual int MinDelayMs => 0;

    protected CookieContainer Cookies { get; }

    public abstract string Name { get; }

    /// <summary>Credentials as currently configured; login is redone when they change.</summary>
    protected abstract (string User, string Password) Credentials { get; }

    protected static Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    public bool HasCredentials => !string.IsNullOrEmpty(Credentials.User) && !string.IsNullOrEmpty(Credentials.Password);

    /// <summary>Performs the login. Returns true on success.</summary>
    protected abstract Task<bool> LoginAsync(CancellationToken ct);

    public async Task<bool> EnsureLoginAsync(CancellationToken ct, bool force = false)
    {
        var key = Credentials.User + "\n" + Credentials.Password;
        if (_loggedIn && !force && key == _loginKey)
        {
            return true;
        }

        if (!HasCredentials)
        {
            return false;
        }

        try
        {
            _loggedIn = await LoginAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogWarning(ex, "{Site}: login failed", Name);
            _loggedIn = false;
        }

        _loginKey = key;
        if (!_loggedIn)
        {
            Logger.LogWarning("{Site}: login rejected – check the credentials in the plugin settings", Name);
        }

        return _loggedIn;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Daily request budget per site – a safety net so a bug or a huge library never hammers a fansub site.
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (today != _budgetDay)
            {
                (_budgetDay, _budgetUsed) = (today, 0);
            }

            var budget = Config.DailyRequestLimit;
            if (budget > 0 && ++_budgetUsed > budget)
            {
                if (_budgetUsed == budget + 1)
                {
                    Util.ActivityLog.Write($"LIMIT     {Name}: dnešní limit {budget} požadavků vyčerpán, pokračuje se zítra");
                }

                throw new HttpRequestException($"{Name}: daily request limit reached");
            }

            var wait = _last.AddMilliseconds(Math.Max(Config.RequestDelayMs, MinDelayMs)) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            _last = DateTime.UtcNow;
            return resp;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected async Task<(string Html, Uri FinalUri)> GetHtmlAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (html, resp.RequestMessage?.RequestUri ?? new Uri(url));
    }

    protected async Task<(string Html, Uri FinalUri)> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (html, resp.RequestMessage?.RequestUri ?? new Uri(url));
    }

    /// <summary>Downloads a file; returns null when the server answered with an HTML page instead.</summary>
    protected async Task<Payload?> DownloadAsync(HttpRequestMessage req, string fallbackName, CancellationToken ct)
    {
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            return null;
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length < 100 || LooksLikeHtml(bytes))
        {
            return null;
        }

        var cd = resp.Content.Headers.ContentDisposition;
        var name = cd?.FileNameStar ?? cd?.FileName ?? fallbackName;
        return new Payload(bytes, name.Trim('"', ' '));
    }

    protected Task<Payload?> DownloadGetAsync(string url, CancellationToken ct) =>
        DownloadAsync(new HttpRequestMessage(HttpMethod.Get, url), Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath)), ct);

    protected Task<Payload?> DownloadPostAsync(string url, IEnumerable<KeyValuePair<string, string>> form, string fallbackName, CancellationToken ct) =>
        DownloadAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) }, fallbackName, ct);

    protected static IDocument Parse(string html) => Parser.ParseDocument(html);

    /// <summary>All named inputs of a form as key/value pairs.</summary>
    protected static List<KeyValuePair<string, string>> FormFields(IElement form) =>
        form.QuerySelectorAll("input[name]")
            .Select(i => new KeyValuePair<string, string>(i.GetAttribute("name")!, i.GetAttribute("value") ?? string.Empty))
            .ToList();

    protected static List<KeyValuePair<string, string>> Set(List<KeyValuePair<string, string>> fields, string name, string value)
    {
        fields.RemoveAll(f => f.Key == name);
        fields.Add(new KeyValuePair<string, string>(name, value));
        return fields;
    }

    /// <summary>Loopback, private, link-local, CGNAT, unique-local and unspecified addresses.</summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6)
        {
            a = a.MapToIPv4();
        }

        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast)
        {
            return true;
        }

        var b = a.GetAddressBytes();
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return (b[0] & 0xFE) == 0xFC; // fc00::/7
        }

        return b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    /// <summary>True when <paramref name="url"/> is http(s) on <paramref name="host"/> or one of its subdomains.</summary>
    public static bool IsOnHost(string url, string host) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
        && (u.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeHtml(byte[] b)
    {
        var head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 300)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        return head.StartsWith('<') && Regex.IsMatch(head, "^<(!doctype|html|head|body)", RegexOptions.IgnoreCase);
    }
}
