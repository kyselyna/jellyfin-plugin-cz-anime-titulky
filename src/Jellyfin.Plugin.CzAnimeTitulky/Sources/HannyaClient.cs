using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>
/// HannyaSubs (hannya-subs.blogspot.com). A project post links every episode as a public
/// "https://mega.nz/file/&lt;id&gt;#&lt;key&gt;" in episode order. MEGA files are AES-128-CTR encrypted with the
/// key from the link; the encrypted attributes carry the file name, which is used to confirm the
/// episode number before the file itself is downloaded. No login needed.
/// </summary>
public sealed partial class HannyaClient : SiteClient
{
    private const string MegaApi = "https://g.api.mega.co.nz/cs";

    public HannyaClient(ILogger<HannyaClient> logger)
        : base(logger)
    {
    }

    public override string Name => "hannya-subs";

    protected override (string User, string Password) Credentials => ("-", "-");

    protected override Task<bool> LoginAsync(CancellationToken ct) => Task.FromResult(true);

    public async Task<Payload?> DownloadAsync(string url, int episode, CancellationToken ct)
    {
        if (!IsOnHost(url, "hannya-subs.blogspot.com"))
        {
            return null;
        }

        var (html, _) = await GetHtmlAsync(url, ct).ConfigureAwait(false);
        var links = MegaLink().Matches(html).Select(m => (Id: m.Groups[1].Value, Key: m.Groups[2].Value)).Distinct().ToList();

        // Position = episode number; then one correction step based on the real file name.
        var index = episode - 1;
        for (var attempt = 0; attempt < 3 && index >= 0 && index < links.Count; attempt++)
        {
            var file = await InfoAsync(links[index].Id, links[index].Key, ct).ConfigureAwait(false);
            if (file is null)
            {
                break;
            }

            var n = AngeClient.EpisodeNumber(file.Value.Name);
            if (n == episode || (n is null && links.Count == 1 && episode == 1))
            {
                return await FetchAsync(file.Value, ct).ConfigureAwait(false);
            }

            if (n is null)
            {
                break;
            }

            index += episode - n.Value;
        }

        return null;
    }

    private readonly record struct MegaFile(string Url, long Size, string Name, byte[] Key, byte[] Iv);

    private async Task<MegaFile?> InfoAsync(string id, string linkKey, CancellationToken ct)
    {
        byte[] raw;
        try
        {
            raw = FromBase64Url(linkKey);
        }
        catch (FormatException)
        {
            return null;
        }

        if (raw.Length != 32)
        {
            return null;
        }

        var key = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            key[i] = (byte)(raw[i] ^ raw[i + 16]);
        }

        var iv = new byte[16];
        Array.Copy(raw, 16, iv, 0, 8);

        var body = JsonSerializer.Serialize(new[] { new { a = "g", g = 1, ssl = 1, p = id } });
        using var req = new HttpRequestMessage(HttpMethod.Post, MegaApi) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0
            || json.RootElement[0].ValueKind != JsonValueKind.Object)
        {
            return null; // negative number = MEGA error (file removed, quota…)
        }

        var o = json.RootElement[0];
        var size = o.TryGetProperty("s", out var s) ? s.GetInt64() : long.MaxValue;
        var dl = o.TryGetProperty("g", out var g) ? g.GetString() : null;
        var at = o.TryGetProperty("at", out var a) ? a.GetString() : null;
        if (dl is null || at is null || size > MaxDownloadBytes || !Uri.TryCreate(dl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var name = AttributeName(at, key);
        return name is null ? null : new MegaFile(dl, size, name, key, iv);
    }

    private async Task<Payload?> FetchAsync(MegaFile f, CancellationToken ct)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, f.Url), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return data.Length > MaxDownloadBytes ? null : new Payload(AesCtr(data, f.Key, f.Iv), f.Name);
    }

    /// <summary>Attributes = AES-CBC (zero IV, no padding) of "MEGA{json}".</summary>
    private static string? AttributeName(string at, byte[] key)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            var plain = aes.DecryptCbc(FromBase64Url(at), new byte[16], PaddingMode.None);
            var text = Encoding.UTF8.GetString(plain).TrimEnd('\0');
            if (!text.StartsWith("MEGA{", StringComparison.Ordinal))
            {
                return null;
            }

            using var json = JsonDocument.Parse(text[4..]);
            return json.RootElement.TryGetProperty("n", out var n) ? Path.GetFileName(n.GetString()) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    private static byte[] AesCtr(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var counter = (byte[])iv.Clone();
        var block = new byte[16];
        var output = new byte[data.Length];
        for (var off = 0; off < data.Length; off += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            for (var i = 0; i < 16 && off + i < data.Length; i++)
            {
                output[off + i] = (byte)(data[off + i] ^ block[i]);
            }

            for (var i = 15; i >= 0; i--)
            {
                if (++counter[i] != 0)
                {
                    break; // big-endian increment with carry
                }
            }
        }

        return output;
    }

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/').Replace(",", string.Empty, StringComparison.Ordinal);
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    [GeneratedRegex(@"https://mega\.nz/file/([A-Za-z0-9_-]{8})#([A-Za-z0-9_-]{43})")]
    private static partial Regex MegaLink();
}
