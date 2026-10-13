using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.CzAnimeTitulky.Integrations;

/// <summary>One open issue from Jellyseerr / Overseerr.</summary>
public sealed record SeerrIssue(int Id, int Type, string MediaType, string? JellyfinId, int Season, int Episode, string Message);

/// <summary>Minimal Jellyseerr / Overseerr API client (issues: list, comment, resolve).</summary>
public sealed class SeerrClient
{
    /// <summary>Issue type "subtitles" (1 video, 2 audio, 3 subtitles, 4 other).</summary>
    public const int SubtitleIssue = 3;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    public static bool Configured => Config.SeerrEnabled && !string.IsNullOrWhiteSpace(Config.SeerrUrl) && !string.IsNullOrWhiteSpace(Config.SeerrApiKey);

    /// <summary>Open issues, newest first (first comment = the reporter's description).</summary>
    public async Task<List<SeerrIssue>> OpenIssuesAsync(CancellationToken ct)
    {
        var node = await SendAsync(HttpMethod.Get, "/api/v1/issue?take=50&skip=0&sort=added&filter=open", null, ct).ConfigureAwait(false);
        var list = new List<SeerrIssue>();
        foreach (var r in node?["results"]?.AsArray() ?? new JsonArray())
        {
            if (r is null)
            {
                continue;
            }

            var message = (string?)r["comments"]?.AsArray().FirstOrDefault()?["message"] ?? string.Empty;
            list.Add(new SeerrIssue(
                (int?)r["id"] ?? 0,
                (int?)r["issueType"] ?? 0,
                (string?)r["media"]?["mediaType"] ?? string.Empty,
                (string?)r["media"]?["jellyfinMediaId"],
                (int?)r["problemSeason"] ?? 0,
                (int?)r["problemEpisode"] ?? 0,
                message));
        }

        return list;
    }

    /// <summary>The issue list's first comment is not always included – fetch the issue itself.</summary>
    public async Task<string> FirstCommentAsync(int id, CancellationToken ct)
    {
        var node = await SendAsync(HttpMethod.Get, $"/api/v1/issue/{id}", null, ct).ConfigureAwait(false);
        return (string?)node?["comments"]?.AsArray().FirstOrDefault()?["message"] ?? string.Empty;
    }

    public Task CommentAsync(int id, string message, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/v1/issue/{id}/comment", new { message }, ct);

    public Task ResolveAsync(int id, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/v1/issue/{id}/resolved", null, ct);

    /// <summary>Connection test: number of open issues.</summary>
    public async Task<int> OpenCountAsync(CancellationToken ct)
    {
        var node = await SendAsync(HttpMethod.Get, "/api/v1/issue/count", null, ct).ConfigureAwait(false);
        return (int?)node?["open"] ?? 0;
    }

    private static async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var baseUrl = Config.SeerrUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Seerr: zadej adresu, např. http://192.168.1.20:5055");
        }

        using var req = new HttpRequestMessage(method, baseUrl + path);
        req.Headers.Add("X-Api-Key", Config.SeerrApiKey.Trim());
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Seerr: HTTP {(int)resp.StatusCode} {text[..Math.Min(200, text.Length)]}");
        }

        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
}
