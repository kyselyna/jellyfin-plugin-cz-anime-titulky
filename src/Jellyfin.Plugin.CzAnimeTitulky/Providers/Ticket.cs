using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.CzAnimeTitulky.Providers;

/// <summary>One downloadable subtitle found for an episode.</summary>
public sealed class Candidate
{
    /// <summary>Download URL (from hiyori).</summary>
    public string U { get; set; } = string.Empty;

    /// <summary>Site kind: hiyori, hns, kamui, wosir, direct.</summary>
    public string K { get; set; } = string.Empty;

    /// <summary>Episode number on the source site.</summary>
    public int E { get; set; }

    /// <summary>Language label on hiyori (CZ/SK).</summary>
    public string L { get; set; } = string.Empty;

    /// <summary>Fansub group.</summary>
    public string G { get; set; } = string.Empty;

    /// <summary>Release the subtitles are timed for.</summary>
    public string R { get; set; } = string.Empty;

    /// <summary>AniList id of the season (teamns archive lookup by title).</summary>
    public int A { get; set; }

    /// <summary>Season number (own collection: SxxEyy file names).</summary>
    public int S { get; set; }
}

/// <summary>
/// The opaque subtitle id handed to Jellyfin: the video, the request and the ordered candidates.
/// For automatic downloads it carries up to three alternatives that are tried in order.
/// </summary>
public sealed class Ticket
{
    public Guid V { get; set; }

    public bool A { get; set; }

    public List<Candidate> C { get; set; } = new();

    private static readonly Lazy<byte[]> Key = new(LoadKey);

    /// <summary>Signed id: payload "~" HMAC – ids arrive through the API and must not be forgeable.</summary>
    public string Encode()
    {
        var payload = B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)));
        return payload + "~" + B64(HMACSHA256.HashData(Key.Value, Encoding.ASCII.GetBytes(payload))[..16]);
    }

    public static Ticket Decode(string id)
    {
        var parts = id.Split('~');
        if (parts.Length != 2
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(parts[1]),
                Encoding.ASCII.GetBytes(B64(HMACSHA256.HashData(Key.Value, Encoding.ASCII.GetBytes(parts[0]))[..16]))))
        {
            throw new ArgumentException("Invalid subtitle id");
        }

        var s = parts[0].Replace('-', '+').Replace('.', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        return JsonSerializer.Deserialize<Ticket>(Encoding.UTF8.GetString(Convert.FromBase64String(s)))
            ?? throw new ArgumentException("Invalid subtitle id");
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '.');

    /// <summary>Random server secret, created once in the plugin data folder.</summary>
    private static byte[] LoadKey()
    {
        var folder = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "cz-anime-titulky");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "ticket.key");
        if (File.Exists(file) && File.ReadAllBytes(file) is { Length: 32 } existing)
        {
            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(file, key);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }
}
