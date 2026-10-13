using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CzAnimeTitulky.Configuration;

/// <summary>
/// Plugin settings (stored by Jellyfin in plugins/configurations/Jellyfin.Plugin.CzAnimeTitulky.xml).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // --- Site logins -------------------------------------------------------
    public string HiyoriUsername { get; set; } = string.Empty;

    public string HiyoriPassword { get; set; } = string.Empty;

    public string HnsEmail { get; set; } = string.Empty;

    public string HnsPassword { get; set; } = string.Empty;

    public string KamuiUsername { get; set; } = string.Empty;

    public string KamuiPassword { get; set; } = string.Empty;

    public string WosirUsername { get; set; } = string.Empty;

    public string WosirPassword { get; set; } = string.Empty;

    /// <summary>Password for kamui-subs.cz archives (published on their website; also tried for own-collection archives).</summary>
    public string ArchivePassword { get; set; } = string.Empty;

    // --- Language ------------------------------------------------------------

    /// <summary>When searching Czech and no Czech subtitle exists, offer Slovak.</summary>
    public bool SlovakFallback { get; set; } = true;

    /// <summary>Save as "video.cs.default.ass" so players pick the track automatically.</summary>
    public bool MarkAsDefault { get; set; } = true;

    // --- Timing --------------------------------------------------------------

    /// <summary>Align timing against the English subtitle track embedded in the video.</summary>
    public bool EnableReferenceSync { get; set; } = true;

    /// <summary>When no embedded reference exists, align timing against the audio (speech detection).</summary>
    public bool EnableAudioSync { get; set; } = true;

    /// <summary>Largest accepted global shift in seconds.</summary>
    public int MaxOffsetSeconds { get; set; } = 90;

    /// <summary>
    /// Automatic downloads only: save subtitles even if their timing could not be verified
    /// (no reference track and audio alignment not confident). Subtitles that provably
    /// do NOT match the embedded reference are never saved automatically.
    /// </summary>
    public bool SaveUnverified { get; set; } = true;

    // --- Own collection --------------------------------------------------------

    /// <summary>Folders with your own subtitles (one per line), as the Jellyfin server sees them.</summary>
    public string LocalFolders { get; set; } = string.Empty;

    /// <summary>Prefer your own collection over the fansub sites.</summary>
    public bool LocalFirst { get; set; }

    // --- AI translation ------------------------------------------------------

    /// <summary>Translate embedded subtitles (e.g. English) to Czech with AI when no fansub exists anywhere.</summary>
    public bool AiEnabled { get; set; }

    /// <summary>Also translate automatically (new episodes, daily task). Off = only on request in "Search subtitles".</summary>
    public bool AiAutomatic { get; set; }

    /// <summary>Lines per AI request (an episode has ~300–500); bigger = more context, fewer requests.</summary>
    public int AiChunkLines { get; set; } = 150;

    /// <summary>claude-cli (Claude Code, claude.ai subscription), anthropic-api, openai-api, local (Ollama / OpenAI-compatible server).</summary>
    public string AiBackend { get; set; } = "claude-cli";

    /// <summary>Model name/alias, empty = backend default (claude-cli: "sonnet").</summary>
    public string AiModel { get; set; } = string.Empty;

    /// <summary>claude-cli: long-lived token from "claude setup-token". APIs: the API key.</summary>
    public string AiToken { get; set; } = string.Empty;

    /// <summary>Local AI server, e.g. "http://192.168.1.50:11434" (Ollama) or "http://host:1234/v1" (LM Studio, llama.cpp, vLLM…).</summary>
    public string AiLocalUrl { get; set; } = string.Empty;

    /// <summary>Optional key of the local AI server – separate from AiToken so a cloud API key never goes to it.</summary>
    public string AiLocalKey { get; set; } = string.Empty;

    /// <summary>Path of the CLI (claude / codex), empty = from PATH.</summary>
    public string AiCliPath { get; set; } = string.Empty;

    /// <summary>Most episodes translated per day (subscription limits are shared with your normal use).</summary>
    public int AiDailyEpisodes { get; set; } = 20;

    /// <summary>API backends: stop translating when the estimated cost this month reaches this many USD (0 = no limit).</summary>
    public double AiMonthlyLimitUsd { get; set; } = 2;

    // --- Jellyseerr / Overseerr ---------------------------------------------

    /// <summary>Handle "subtitles" issues reported in Jellyseerr/Overseerr: fetch another version and answer the issue.</summary>
    public bool SeerrEnabled { get; set; }

    /// <summary>Address of Jellyseerr/Overseerr, e.g. "http://192.168.1.20:5055".</summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>API key (Seerr → Settings → General).</summary>
    public string SeerrApiKey { get; set; } = string.Empty;

    /// <summary>Mark the issue as resolved when new subtitles were saved for every reported episode.</summary>
    public bool SeerrResolve { get; set; } = true;

    // --- Behaviour -----------------------------------------------------------

    /// <summary>Regex of series names to ignore (e.g. Czech dub or hardsubs).</summary>
    public string ExcludedSeriesRegex { get; set; } = string.Empty;

    /// <summary>Skip automatic search when the video already has a cs/sk subtitle file next to it.</summary>
    public bool SkipIfSidecarExists { get; set; } = true;

    /// <summary>How long downloaded hiyori listings are cached (hours).</summary>
    public int CacheHours { get; set; } = 24;

    /// <summary>Most requests per site and day (0 = unlimited).</summary>
    public int DailyRequestLimit { get; set; } = 500;

    /// <summary>Days before a subtitle that did not fit an episode is tried again automatically.</summary>
    public int RetryRejectedDays { get; set; } = 14;

    /// <summary>Delay between requests to one site (ms) – be polite to fansub sites.</summary>
    public int RequestDelayMs { get; set; } = 700;
}
