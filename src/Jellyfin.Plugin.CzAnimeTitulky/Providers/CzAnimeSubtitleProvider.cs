using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.CzAnimeTitulky.Matching;
using Jellyfin.Plugin.CzAnimeTitulky.Sources;
using Jellyfin.Plugin.CzAnimeTitulky.Subtitles;
using Jellyfin.Plugin.CzAnimeTitulky.Sync;
using Jellyfin.Plugin.CzAnimeTitulky.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Providers;

/// <summary>
/// Jellyfin subtitle provider: finds Czech/Slovak fansub subtitles via hiyori.cz,
/// downloads them from the fansub sites, fixes their timing and hands them to Jellyfin.
/// </summary>
public sealed partial class CzAnimeSubtitleProvider : ISubtitleProvider
{
    private readonly ILibraryManager _library;
    private readonly HiyoriResolver _resolver;
    private readonly HiyoriClient _hiyori;
    private readonly HnsClient _hns;
    private readonly KamuiClient _kamui;
    private readonly WosirClient _wosir;
    private readonly DirectClient _direct;
    private readonly AngeClient _ange;
    private readonly HannyaClient _hannya;
    private readonly WaybackClient _wayback;
    private readonly TeamNsClient _teamNs;
    private readonly AniListClient _aniList;
    private readonly Translation.SubtitleTranslator _translator;
    private readonly MediaTools _tools;
    private readonly LocalClient _local;
    private readonly SyncService _sync;
    private readonly ILogger<CzAnimeSubtitleProvider> _logger;
    private const string NothingDownloaded = "nothing downloaded";
    private readonly JsonCache<string> _rejected = new("rejected");

    public CzAnimeSubtitleProvider(
        ILibraryManager library,
        HiyoriResolver resolver,
        HiyoriClient hiyori,
        HnsClient hns,
        KamuiClient kamui,
        WosirClient wosir,
        DirectClient direct,
        AngeClient ange,
        HannyaClient hannya,
        WaybackClient wayback,
        TeamNsClient teamNs,
        AniListClient aniList,
        Translation.SubtitleTranslator translator,
        MediaTools tools,
        LocalClient local,
        SyncService sync,
        ILogger<CzAnimeSubtitleProvider> logger)
    {
        _library = library;
        _resolver = resolver;
        _hiyori = hiyori;
        _hns = hns;
        _kamui = kamui;
        _wosir = wosir;
        _direct = direct;
        _ange = ange;
        _hannya = hannya;
        _wayback = wayback;
        _teamNs = teamNs;
        _aniList = aniList;
        _translator = translator;
        _tools = tools;
        _local = local;
        _sync = sync;
        _logger = logger;
    }

    public string Name => "CZ Anime Titulky";

    public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Episode, VideoContentType.Movie };

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var want = WantedLanguage(request);
        if (want is null || string.IsNullOrEmpty(request.MediaPath))
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var cfg = Config;
        if (!string.IsNullOrWhiteSpace(cfg.ExcludedSeriesRegex) && !string.IsNullOrEmpty(request.SeriesName ?? request.Name)
            && Regex.IsMatch(request.SeriesName ?? request.Name, cfg.ExcludedSeriesRegex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var sidecar = cfg.SkipIfSidecarExists ? SidecarState(request.MediaPath) : Sidecar.None;
        if (request.IsAutomated && sidecar == Sidecar.Human)
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        if (_library.FindByPath(request.MediaPath, false) is not Video video)
        {
            ActivityLog.Write($"PŘESKOČENO {Path.GetFileName(request.MediaPath)}: video není v knihovně");
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var target = await _resolver.ResolveAsync(video, !request.IsAutomated, cancellationToken).ConfigureAwait(false);
        var local = await LocalCandidateAsync(video, cancellationToken).ConfigureAwait(false);
        var ranked = target is null ? new List<(HiyoriRow Row, Candidate Candidate)>() : Rank(target, want);
        if (local is not null)
        {
            var row = new HiyoriRow(string.Empty, local.E.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty, "CZ", "vlastní", local.G, string.Empty, local.U);
            if (cfg.LocalFirst || ranked.Count == 0)
            {
                ranked.Insert(0, (row, local));
            }
            else
            {
                ranked.Add((row, local));
            }
        }

        if (ranked.Count == 0)
        {
            ActivityLog.Write(target is null
                ? $"NENALEZENO {Describe(video)}: anime není na hiyori.cz (nebo chybí AniList id) ani ve vlastní sbírce"
                : $"NENALEZENO {Describe(video)}: díl {target.Episode} nemá {want} titulky na podporovaném webu (hiyori {target.Anime.Path}, CZ/SK řádků celkem {target.Anime.Rows.Count(r => r.Lang is "CZ" or "SK")}) ani ve vlastní sbírce");
            return AiOffer(video, want, request.IsAutomated, sidecar);
        }

        target ??= new Target(video, Describe(video), local!.E, new HiyoriAnime(string.Empty, null, Array.Empty<HiyoriRow>(), DateTime.UtcNow), local.A);

        _logger.LogInformation("{Name}: {Count} candidate(s), best {Group} [{Release}] {Lang}", target.DisplayName, ranked.Count, ranked[0].Row.Group, ranked[0].Row.Release, ranked[0].Row.Lang);
        ActivityLog.Write($"HLEDÁNÍ  {target.DisplayName} ({want}{(request.IsAutomated ? ", automaticky" : string.Empty)}): {ranked.Count}× – nejlepší {ranked[0].Row.Group} [{ranked[0].Row.Release}] {ranked[0].Row.Lang}");

        if (request.IsAutomated)
        {
            // Skip versions that already failed for this video recently (no daily re-downloads).
            var age = TimeSpan.FromDays(Math.Max(1, Config.RetryRejectedDays));
            string? why = null;
            // Temporary failures (site down, rate limit) are not recorded here and are simply tried again next time.
            ranked = ranked.Where(x => !_rejected.TryGet(RejectKey(video.Id, x.Candidate), age, out why)).ToList();
            if (ranked.Count == 0)
            {
                ActivityLog.Write($"PŘESKOČENO {target.DisplayName}: všechny titulky byly v posledních {age.TotalDays:0} dnech zamítnuty ({why}), automaticky se zkusí znovu později – ručně přes Hledat titulky");
                return Array.Empty<RemoteSubtitleInfo>();
            }

            // Jellyfin takes only the first result → one result carrying the top three alternatives.
            var top = ranked.Take(3).ToList();
            return new[] { Info(target, top[0], new Ticket { V = video.Id, A = true, C = top.Select(x => x.Candidate).ToList() }) };
        }

        return ranked.Select(x => Info(target, x, new Ticket { V = video.Id, A = false, C = new() { x.Candidate } })).ToList();
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var ticket = Ticket.Decode(id);
        var video = _library.GetItemById(ticket.V) as Video;
        var cfg = Config;
        var reasons = new List<string>();

        // Jellyfin writes the file only after we return – check first that the folder takes new files (a full disk
        // would otherwise lose the subtitles silently after downloading them from the site).
        if (video?.Path is not null && !CanWrite(Path.GetDirectoryName(video.Path)!, out var writeError))
        {
            ActivityLog.Write($"NEULOŽENO {Describe(video)}: do složky videa nelze zapisovat ({writeError}) – plný disk nebo chybí práva");
            throw new IOException($"CZ Anime Titulky: cannot write next to the video ({writeError})");
        }

        foreach (var c in ticket.C.Take(3))
        {
            // The id arrives through the API – accept only links the site client itself would produce.
            if (Kind(c.U) != c.K)
            {
                reasons.Add($"{c.G}: invalid link");
                continue;
            }

            if (c.K == "ai")
            {
                return await TranslateAsync(video, ticket, cancellationToken).ConfigureAwait(false);
            }

            SubtitleDocument? localDoc = null;
            if (c.K == "local")
            {
                var entry = await _local.GetAsync(c.U["local:".Length..], cancellationToken).ConfigureAwait(false);
                localDoc = entry is null ? null : _local.Load(entry, c.S, c.E);
                if (localDoc is null)
                {
                    reasons.Add($"{c.G}: díl ve vlastní sbírce nenalezen");
                    continue;
                }
            }

            Payload? payload = null;
            try
            {
                if (localDoc is null)
                {
                    payload = await DownloadAsync(c, video?.Path, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                reasons.Add($"{c.G}: download failed ({ex.Message})");
                continue;
            }

            var doc = localDoc ?? (payload is null ? null : SafeExtract(payload, c.E, cfg.ArchivePassword));
            if (doc is null)
            {
                reasons.Add($"{c.G}: nothing usable downloaded from {c.K}");
                if (ticket.A)
                {
                    _rejected.Set(RejectKey(ticket.V, c), NothingDownloaded);
                }

                continue;
            }

            var lang = LanguageDetector.Detect(doc.Text);
            if (ticket.A && lang == "sk" && c.L == "CZ" && !cfg.SlovakFallback)
            {
                reasons.Add($"{c.G}: Slovak although labelled Czech");
                continue;
            }

            var outcome = new SyncOutcome(SyncMethod.None, 0, 0, false);
            if (video?.Path is not null)
            {
                outcome = await _sync.SyncAsync(video.Path, doc, cancellationToken).ConfigureAwait(false);
            }

            if (ticket.A && (outcome.Mismatch || (outcome.Method == SyncMethod.None && !cfg.SaveUnverified)))
            {
                reasons.Add($"{c.G}: timing {outcome}");
                _rejected.Set(RejectKey(ticket.V, c), outcome.ToString());
                continue;
            }

            _logger.LogInformation("{Video}: saved {Lang} subtitles by {Group} [{Release}] from {Site}, timing {Outcome}", video?.Name, lang, c.G, c.R, c.K, outcome);
            ActivityLog.Write($"ULOŽENO  {Describe(video)}: {lang.ToUpperInvariant()} {c.G} [{c.R}] z {c.K}, časování: {outcome}");
            RemoveAiSidecars(video?.Path); // a real fansub replaces the AI translation
            VerifySavedLater(video, lang);
            return new SubtitleResponse
            {
                Format = doc.Format,
                Language = cfg.MarkAsDefault ? lang + ".default" : lang,
                Stream = new MemoryStream(doc.ToUtf8()),
            };
        }

        ActivityLog.Write($"NEULOŽENO {Describe(video)}: " + string.Join("; ", reasons));
        throw new InvalidOperationException("CZ Anime Titulky: no candidate usable – " + string.Join("; ", reasons));
    }

    private enum Sidecar
    {
        None,
        Ai,
        Human,
    }

    /// <summary>Offers an AI translation of an embedded subtitle track when nothing else exists.</summary>
    private IEnumerable<RemoteSubtitleInfo> AiOffer(Video video, string want, bool automated, Sidecar sidecar)
    {
        var cfg = Config;
        if (!cfg.AiEnabled || want != "CZ" || (automated && (!cfg.AiAutomatic || sidecar != Sidecar.None || !_translator.HasBudget())))
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var streams = video.GetMediaStreams()
            .Where(m => m.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !m.IsExternal && m.IsTextSubtitleStream)
            .Where(m => (m.Language ?? string.Empty).ToLowerInvariant() is not ("cze" or "ces" or "slo" or "slk"))
            .ToList();
        if (streams.Count == 0)
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var lang = streams.Any(m => m.Language is "eng" or "en") ? "eng" : streams[0].Language ?? "und";
        var c = new Candidate { U = "ai:" + lang, K = "ai", E = (video as MediaBrowser.Controller.Entities.TV.Episode)?.IndexNumber ?? 1, L = "CZ", G = "AI " + _translator.BackendName, R = lang };
        ActivityLog.Write($"HLEDÁNÍ  {Describe(video)} (CZ{(automated ? ", automaticky" : string.Empty)}): fansub nikde – nabízím AI překlad z {lang}");
        return new[]
        {
            new RemoteSubtitleInfo
            {
                Id = new Ticket { V = video.Id, A = automated, C = new() { c } }.Encode(),
                ProviderName = Name,
                Name = $"AI překlad ({_translator.BackendName}) z titulků ve videu ({lang})",
                Author = "AI",
                Comment = "strojový překlad – časování převzato z videa",
                ThreeLetterISOLanguageName = "cze",
                Format = "ass",
                AiTranslated = true,
                MachineTranslated = true,
                IsHashMatch = true,
            },
        };
    }

    private async Task<SubtitleResponse> TranslateAsync(Video? video, Ticket ticket, CancellationToken ct)
    {
        if (video?.Path is null)
        {
            throw new InvalidOperationException("CZ Anime Titulky: video not found");
        }

        var src = await _tools.ExtractTranslationSourceAsync(video.Path, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("CZ Anime Titulky: no embedded subtitle to translate");
        var title = video is MediaBrowser.Controller.Entities.TV.Episode e ? $"{e.SeriesName}, díl {e.IndexNumber}" : video.Name;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _translator.TranslateAsync(src.Doc, title, src.Language, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ActivityLog.Write($"NEULOŽENO {Describe(video)}: AI překlad selhal – {ex.Message}");
            throw;
        }

        ActivityLog.Write($"ULOŽENO  {Describe(video)}: CS AI překlad ({_translator.BackendName}) z {src.Language}, {src.Doc.Events.Count} řádků za {sw.Elapsed.TotalSeconds:0} s, časování z videa");
        var cfg = Config;
        return new SubtitleResponse
        {
            Format = "ass",
            Language = cfg.MarkAsDefault ? "cs.ai.default" : "cs.ai",
            Stream = new MemoryStream(src.Doc.ToUtf8()),
        };
    }

    private static void RemoveAiSidecars(string? mediaPath)
    {
        if (mediaPath is null)
        {
            return;
        }

        try
        {
            var stem = Path.GetFileNameWithoutExtension(mediaPath);
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(mediaPath)!, stem + ".cs.ai*"))
            {
                File.Delete(f);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Episode from the own collection (folders in the settings), matched by AniList titles.</summary>
    private async Task<Candidate?> LocalCandidateAsync(Video video, CancellationToken ct)
    {
        if (LocalClient.Folders().Count == 0)
        {
            return null;
        }

        var ids = await _resolver.ResolveIdsAsync(video, ct).ConfigureAwait(false);
        if (ids is null)
        {
            return null;
        }

        var media = await _aniList.GetAsync(ids.Value.AniListId, ct).ConfigureAwait(false);
        var titles = (media?.Titles() ?? Array.Empty<string>()).ToList();
        if (ids.Value.Season == 1 && video is MediaBrowser.Controller.Entities.TV.Episode e && e.SeriesName is not null)
        {
            titles.Add(e.SeriesName);
        }

        var entry = await _local.FindAsync(titles, ct).ConfigureAwait(false);
        if (entry is null || _local.Load(entry, ids.Value.Season, ids.Value.Episode) is null)
        {
            return null;
        }

        return new Candidate
        {
            U = "local:" + entry.Key,
            K = "local",
            E = ids.Value.Episode,
            S = ids.Value.Season,
            A = ids.Value.AniListId,
            L = "CZ",
            G = "Vlastní: " + entry.Name,
            R = "vlastní sbírka",
        };
    }

    private static string RejectKey(Guid video, Candidate c) => $"{video:N}|{c.K}|{c.U}|{c.E}";

    private static bool CanWrite(string dir, out string error)
    {
        var probe = Path.Combine(dir, $".czat-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, new byte[16]);
            File.Delete(probe);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            try
            {
                File.Delete(probe);
            }
            catch (Exception)
            {
                // nothing was created
            }

            return false;
        }
    }

    /// <summary>Jellyfin saves the stream after GetSubtitles returns and only logs a failure – report it in our history too.</summary>
    private static void VerifySavedLater(Video? video, string lang)
    {
        if (video?.Path is null)
        {
            return;
        }

        var since = DateTime.UtcNow.AddSeconds(-5);
        var name = Describe(video);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            try
            {
                var dir = Path.GetDirectoryName(video.Path)!;
                var stem = Path.GetFileNameWithoutExtension(video.Path) + "." + lang;
                if (!Directory.EnumerateFiles(dir, stem + "*").Any(f => File.GetLastWriteTimeUtc(f) >= since))
                {
                    ActivityLog.Write($"CHYBA     {name}: titulky se u videa neobjevily – Jellyfin je nedokázal zapsat (plný disk? viz log Jellyfinu)");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ActivityLog.Write($"CHYBA     {name}: kontrolu uložení nelze provést ({ex.Message})");
            }
        });
    }

    private static string Describe(Video? v) => v switch
    {
        MediaBrowser.Controller.Entities.TV.Episode e => $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00}",
        null => "?",
        _ => v.Name,
    };

    private static string? WantedLanguage(SubtitleSearchRequest r)
    {
        var l = (r.Language ?? string.Empty).ToLowerInvariant();
        var two = (r.TwoLetterISOLanguageName ?? string.Empty).ToLowerInvariant();
        if (l is "cze" or "ces" or "cs" || two == "cs")
        {
            return "CZ";
        }

        if (l is "slo" or "slk" or "sk" || two == "sk")
        {
            return "SK";
        }

        return null;
    }

    /// <summary>
    /// True when a cs/sk subtitle already lies next to the video – tagged by name (video.cs.ass), or
    /// untagged/unknown tag (video.ass, video.22edited.srt) but Czech/Slovak by its text.
    /// </summary>
    private static Sidecar SidecarState(string mediaPath)
    {
        var state = Sidecar.None;
        try
        {
            var dir = Path.GetDirectoryName(mediaPath)!;
            var stem = Path.GetFileNameWithoutExtension(mediaPath);
            foreach (var f in Directory.EnumerateFiles(dir, stem + ".*"))
            {
                var rest = Path.GetFileName(f)[stem.Length..].ToLowerInvariant();
                if (!Regex.IsMatch(rest, @"\.(ass|ssa|srt|vtt)$"))
                {
                    continue;
                }

                if (rest.StartsWith(".cs.ai", StringComparison.Ordinal))
                {
                    state = Sidecar.Ai;
                    continue;
                }

                if (SidecarLang().IsMatch(rest))
                {
                    return Sidecar.Human;
                }

                if (OtherLang().IsMatch(rest) || new FileInfo(f).Length > 5 * 1024 * 1024)
                {
                    continue;
                }

                var text = File.ReadAllText(f);
                if (Regex.Count(text, "[řůěčšžťďňľĺŕŘŮĚČŠŽŤĎŇĽ]") > 100) // letters French/German/Polish subtitles do not use together
                {
                    return Sidecar.Human;
                }
            }

            return state;
        }
        catch (IOException)
        {
            return state;
        }
    }

    private List<(HiyoriRow Row, Candidate Candidate)> Rank(Target t, string want)
    {
        var cfg = Config;
        var rows = t.Anime.Rows.Where(r => r.Lang is "CZ" or "SK" && Kind(r.Url) is not null).ToList();

        // Exact episode numbers; some hiyori entries continue the numbering of the previous season.
        var exact = rows.Select(r => (r, n: ExactEpisode(r.Episode))).Where(x => x.n is not null).ToList();
        var ep = t.Episode;
        if (exact.Count > 0 && !exact.Any(x => x.n == ep))
        {
            var min = exact.Min(x => x.n!.Value);
            if (min > 1 && exact.Any(x => x.n == ep + min - 1))
            {
                ep += min - 1;
            }
        }

        var hits = rows
            .Select(r => (Row: r, Exact: ExactEpisode(r.Episode) == ep, InRange: InRange(r.Episode, ep)))
            .Where(x => x.Exact || x.InRange)
            .ToList();

        var langs = want == "CZ" && cfg.SlovakFallback && !hits.Any(h => h.Row.Lang == "CZ") ? new[] { "SK" } : new[] { want };
        hits = hits.Where(h => langs.Contains(h.Row.Lang)).ToList();

        // How many episodes each group/release covers – prefer a consistent group across the season.
        var coverage = rows.GroupBy(r => (r.Group, r.Release, r.Lang))
            .ToDictionary(g => g.Key, g => g.Select(r => r.Episode).Distinct().Count());
        var file = Path.GetFileName(t.Video.Path ?? string.Empty);
        var ourGroup = OurGroup().Match(file) is { Success: true } m ? m.Groups[1].Value : null;
        var isBd = BdRelease().IsMatch(t.Video.Path ?? string.Empty);

        return hits
            .OrderByDescending(h => h.Exact)
            .ThenByDescending(h => h.Row.Lang == want)
            .ThenByDescending(h => Kind(h.Row.Url) != "direct")
            .ThenByDescending(h => ourGroup is not null && h.Row.Release.Contains(ourGroup, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(h => BdRelease().IsMatch(h.Row.Release) == isBd)
            .ThenByDescending(h => coverage.GetValueOrDefault((h.Row.Group, h.Row.Release, h.Row.Lang)))
            .ThenByDescending(h => Version(h.Row.Version))
            .Select(h => (h.Row, new Candidate { U = h.Row.Url!, K = Kind(h.Row.Url)!, E = ep, L = h.Row.Lang, G = h.Row.Group, R = h.Row.Release, A = t.AniListId }))
            .ToList();
    }

    private RemoteSubtitleInfo Info(Target t, (HiyoriRow Row, Candidate Candidate) x, Ticket ticket) => new()
    {
        Id = ticket.Encode(),
        ProviderName = Name,
        Name = $"{x.Row.Group} – {x.Row.Release} – díl {x.Row.Episode}",
        Author = x.Row.Group,
        Comment = $"{x.Row.Lang} · {x.Row.Title} · {x.Row.Version} · {x.Candidate.K}",
        ThreeLetterISOLanguageName = x.Row.Lang == "SK" ? "slo" : "cze",
        Format = "ass",
        IsHashMatch = true,
    };

    private async Task<Payload?> DownloadAsync(Candidate c, string? videoPath, CancellationToken ct) => c.K switch
    {
        "hiyori" => await _hiyori.DownloadAsync(c.U, ct).ConfigureAwait(false),
        "hns" => await _hns.DownloadAsync(c.U, c.E, ct).ConfigureAwait(false),
        "kamui" => await _kamui.DownloadAsync(c.U, c.E, ct).ConfigureAwait(false),
        "wosir" => await _wosir.DownloadAsync(c.U, c.E, BdRelease().IsMatch(videoPath ?? string.Empty), ct).ConfigureAwait(false),
        "direct" => await _direct.DownloadAsync(c.U, ct).ConfigureAwait(false),
        "ange" => await _ange.DownloadAsync(c.U, c.E, ct).ConfigureAwait(false),
        "hannya" => await _hannya.DownloadAsync(c.U, c.E, ct).ConfigureAwait(false),
        "wayback" => await _wayback.DownloadAsync(c.U, c.E, ct).ConfigureAwait(false),
        "teamns" => await _teamNs.DownloadAsync((await _aniList.GetAsync(c.A, ct).ConfigureAwait(false))?.Titles() ?? Array.Empty<string>(), ct).ConfigureAwait(false),
        _ => null,
    };

    private SubtitleDocument? SafeExtract(Payload p, int ep, string password)
    {
        try
        {
            return PayloadExtractor.Extract(p, ep, password);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cannot read downloaded file {File}", p.FileName);
            return null;
        }
    }

    /// <summary>Which site client handles a hiyori download link (null = unsupported, e.g. mega.nz).</summary>
    public static string? Kind(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        // First: an archived page may carry another site's name in its path.
        if (SiteClient.IsOnHost(url, "web.archive.org"))
        {
            return "wayback";
        }

        if (url.StartsWith("/anime/downloadsubtitles", StringComparison.OrdinalIgnoreCase) || url.Contains("hiyori.cz/anime/downloadsubtitles", StringComparison.OrdinalIgnoreCase))
        {
            return "hiyori";
        }

        if (url.Contains("hns.sk/anime", StringComparison.OrdinalIgnoreCase))
        {
            return "hns";
        }

        if (url.Contains("kamui-subs.cz", StringComparison.OrdinalIgnoreCase))
        {
            return "kamui";
        }

        if (url.Contains("wosir.cz", StringComparison.OrdinalIgnoreCase))
        {
            return "wosir";
        }

        if (url.StartsWith("ai:", StringComparison.Ordinal))
        {
            return "ai";
        }

        if (url.StartsWith("local:", StringComparison.Ordinal))
        {
            return "local";
        }

        if (SiteClient.IsOnHost(url, "ange.3mka.cz") && url.Contains("/anime/preklady/", StringComparison.OrdinalIgnoreCase))
        {
            return "ange";
        }

        if (SiteClient.IsOnHost(url, "hannya-subs.blogspot.com"))
        {
            return "hannya";
        }

        if (url.Contains("teamns.gensubs.cz", StringComparison.OrdinalIgnoreCase))
        {
            return "teamns";
        }

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && DirectFile().IsMatch(url))
        {
            return "direct";
        }

        return null;
    }

    private static int? ExactEpisode(string s) =>
        int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool InRange(string s, int ep)
    {
        var m = EpisodeRange().Match(s);
        return m.Success
            && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) <= ep
            && ep <= int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    private static double Version(string s)
    {
        var m = VersionNumber().Match(s);
        if (!m.Success)
        {
            return 0;
        }

        var parts = m.Groups[1].Value.Split('.');
        return double.TryParse(parts[0] + "." + string.Concat(parts.Skip(1)), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    [GeneratedRegex(@"\.(cs|cz|cze|ces|sk|slo|slk)(\.|$)")]
    private static partial Regex SidecarLang();

    [GeneratedRegex(@"\.(en|eng|ja|jpn|de|ger|deu|es|spa|fr|fre|fra|it|ita|pl|pol|ru|rus|hu|hun|pt|por|zh|chi|ko|kor)(\.|$)")]
    private static partial Regex OtherLang();

    [GeneratedRegex(@"^\[([^\]]+)\]")]
    private static partial Regex OurGroup();

    [GeneratedRegex(@"(?i)(\bBD|Blu-?ray|BDRip|\bBD\b)")]
    private static partial Regex BdRelease();

    [GeneratedRegex(@"\.(ass|ssa|srt|zip|rar)(\?|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectFile();

    [GeneratedRegex(@"^\s*(\d+)\s*[-–]\s*(\d+)\s*$")]
    private static partial Regex EpisodeRange();

    [GeneratedRegex(@"Verze:\s*([\d.]+)")]
    private static partial Regex VersionNumber();
}
