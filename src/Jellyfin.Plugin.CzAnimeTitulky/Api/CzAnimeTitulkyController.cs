using Jellyfin.Plugin.CzAnimeTitulky.Sources;
using Jellyfin.Plugin.CzAnimeTitulky.Subtitles;
using Jellyfin.Plugin.CzAnimeTitulky.Sync;
using Jellyfin.Plugin.CzAnimeTitulky.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CzAnimeTitulky.Api;

/// <summary>Helper endpoints for the settings page and scripts.</summary>
[ApiController]
[Route("CzAnimeTitulky")]
[Authorize(Policy = "RequiresElevation")]
public class CzAnimeTitulkyController : ControllerBase
{
    private readonly SiteClient[] _sites;
    private readonly ILibraryManager _library;
    private readonly ISubtitleManager _subtitles;
    private readonly SyncService _sync;
    private readonly Translation.AiBackend _ai;
    private readonly Translation.ClaudeSetup _claude;
    private readonly LocalClient _local;
    private readonly Translation.SubtitleTranslator _translator;

    public CzAnimeTitulkyController(HiyoriClient hiyori, HnsClient hns, KamuiClient kamui, WosirClient wosir, ILibraryManager library, ISubtitleManager subtitles, SyncService sync, Translation.AiBackend ai, Translation.ClaudeSetup claude, LocalClient local, Translation.SubtitleTranslator translator)
    {
        _sites = new SiteClient[] { hiyori, hns, kamui, wosir };
        _library = library;
        _subtitles = subtitles;
        _sync = sync;
        _ai = ai;
        _claude = claude;
        _local = local;
        _translator = translator;
    }

    /// <summary>Logs in to every site with the saved credentials.</summary>
    [HttpPost("TestLogins")]
    public async Task<ActionResult<Dictionary<string, string>>> TestLogins(CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        foreach (var s in _sites)
        {
            result[s.Name] = !s.HasCredentials ? "nevyplněno"
                : await s.EnsureLoginAsync(ct, force: true).ConfigureAwait(false) ? "OK" : "CHYBA přihlášení";
        }

        return result;
    }

    /// <summary>Scans the own subtitle collection and lists what was found.</summary>
    [HttpPost("Local/Scan")]
    public async Task<ActionResult<object>> LocalScan(CancellationToken ct)
    {
        var list = await _local.ListAsync(true, ct).ConfigureAwait(false);
        return Ok(new { folders = LocalClient.Folders(), count = list.Count, names = list.Select(e => e.Name).Distinct().Take(500) });
    }

    /// <summary>Claude Code installation and login state.</summary>
    [HttpGet("Claude")]
    public Task<Translation.ClaudeStatus> ClaudeStatus(CancellationToken ct) => _claude.StatusAsync(ct);

    /// <summary>Installs Claude Code with the official installer.</summary>
    [HttpPost("Claude/Install")]
    public async Task<ActionResult<object>> ClaudeInstall(CancellationToken ct) => Ok(new { message = await _claude.InstallAsync(ct).ConfigureAwait(false) });

    /// <summary>Starts "claude setup-token"; returns the sign-in URL.</summary>
    [HttpPost("Claude/Login")]
    public Task<Translation.ClaudeStatus> ClaudeLogin(CancellationToken ct) => _claude.StartLoginAsync(ct);

    /// <summary>Finishes the login with the code from claude.ai.</summary>
    [HttpPost("Claude/Code")]
    public Task<Translation.ClaudeStatus> ClaudeCode([FromQuery] string code, CancellationToken ct) => _claude.SubmitCodeAsync(code ?? string.Empty, ct);

    [HttpDelete("Claude/Login")]
    public ActionResult ClaudeCancel()
    {
        _claude.CancelLogin();
        return NoContent();
    }

    /// <summary>Estimated AI spend this month.</summary>
    [HttpGet("AiUsage")]
    public ActionResult<object> AiUsage() => Ok(new
    {
        spent = Math.Round(_translator.SpentThisMonth(), 3),
        limit = Plugin.Instance?.Configuration.AiMonthlyLimitUsd ?? 2,
        model = Translation.AiBackend.Model(),
        backend = Plugin.Instance?.Configuration.AiBackend,
    });

    /// <summary>Translates two test lines with the configured AI backend.</summary>
    [HttpPost("TestAi")]
    public async Task<ActionResult<object>> TestAi(CancellationToken ct)
    {
        try
        {
            var reply = await _ai.AskAsync("Přelož do češtiny, odpověz jen překladem.", "Onii-chan, wait for me! / I'll protect everyone, no matter what.", ct).ConfigureAwait(false);
            return Ok(new { ok = true, text = reply.Trim() });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Ok(new { ok = false, text = ex.Message });
        }
    }

    /// <summary>The plugin's activity history (newest last).</summary>
    [HttpGet("Activity")]
    public ActionResult<IReadOnlyList<string>> Activity([FromQuery] int lines = 300) =>
        Ok(ActivityLog.Tail(Math.Clamp(lines, 1, 5000)));

    /// <summary>
    /// Uploads your own subtitle (ass/srt, or a zip/rar with it) for one episode or movie: the timing is
    /// aligned like for downloaded subtitles and the file is saved next to the video.
    /// Request body = the file. Subtitles that provably do not fit the video are refused (409) unless force=true.
    /// </summary>
    [HttpPost("Upload/{itemId}")]
    [RequestSizeLimit(SiteClient.MaxDownloadBytes)]
    public async Task<ActionResult<object>> Upload([FromRoute] Guid itemId, [FromQuery] bool force = false, [FromQuery] string? name = null, CancellationToken ct = default)
    {
        if (_library.GetItemById(itemId) is not Video video || string.IsNullOrEmpty(video.Path))
        {
            return NotFound();
        }

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
        if (ms.Length == 0 || ms.Length > SiteClient.MaxDownloadBytes)
        {
            return BadRequest("empty or too large");
        }

        SubtitleDocument? doc;
        try
        {
            doc = PayloadExtractor.Extract(new Payload(ms.ToArray(), name ?? "upload"), (video as Episode)?.IndexNumber ?? 1, Plugin.Instance?.Configuration.ArchivePassword ?? string.Empty);
        }
        catch (Exception ex) when (ex is InvalidDataException or SharpCompress.Common.SharpCompressException or InvalidOperationException)
        {
            return BadRequest("cannot read: " + ex.Message);
        }

        if (doc is null)
        {
            return BadRequest("not a subtitle");
        }

        var lang = LanguageDetector.Detect(doc.Text);
        var outcome = await _sync.SyncAsync(video.Path, doc, ct).ConfigureAwait(false);
        var label = video is Episode e ? $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00}" : video.Name;
        if (outcome.Mismatch && !force)
        {
            ActivityLog.Write($"NAHRÁNÍ ODMÍTNUTO {label}: {name} – časování {outcome}");
            return Conflict(new { saved = false, timing = outcome.ToString(), language = lang });
        }

        var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        await _subtitles.UploadSubtitle(video, new SubtitleResponse
        {
            Format = doc.Format,
            Language = cfg.MarkAsDefault ? lang + ".default" : lang,
            Stream = new MemoryStream(doc.ToUtf8()),
        }).ConfigureAwait(false);
        ActivityLog.Write($"NAHRÁNO   {label}: {lang.ToUpperInvariant()} {name}, časování: {outcome}");
        return Ok(new { saved = true, timing = outcome.ToString(), language = lang });
    }
}
