using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CzAnimeTitulky.Providers;
using Jellyfin.Plugin.CzAnimeTitulky.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Integrations;

/// <summary>
/// Optional: answers "subtitles" issues from Jellyseerr / Overseerr. For each reported episode the version the
/// plugin saved is excluded, the plugin-style sidecars are set aside, and a new automatic search runs (another
/// version, or the same one synced again). Success → comment (+ resolve); nothing better → the old subtitles
/// come back and the issue gets a comment. Every issue is handled once.
/// </summary>
public sealed class SeerrIssuesTask : IScheduledTask
{
    private const int MaxEpisodes = 30;
    private readonly ILibraryManager _library;
    private readonly ISubtitleManager _subtitles;
    private readonly IFileSystem _fileSystem;
    private readonly CzAnimeSubtitleProvider _provider;
    private readonly SeerrClient _seerr = new();
    private readonly JsonCache<string> _done = new("seerr-issues");
    private readonly ILogger<SeerrIssuesTask> _logger;

    public SeerrIssuesTask(ILibraryManager library, ISubtitleManager subtitles, IFileSystem fileSystem, IEnumerable<ISubtitleProvider> providers, ILogger<SeerrIssuesTask> logger)
    {
        _library = library;
        _subtitles = subtitles;
        _fileSystem = fileSystem;
        _provider = providers.OfType<CzAnimeSubtitleProvider>().First();
        _logger = logger;
    }

    public string Name => "Hlášení titulků z Jellyseerr";

    public string Key => "CzAnimeTitulkySeerrIssues";

    public string Description => "Když divák v Jellyseerr/Overseerr nahlásí problém s titulky, stáhne jinou verzi (nebo je znovu srovná) a odpoví v hlášení. Zapíná se v nastavení doplňku CZ Anime Titulky.";

    public string Category => "CZ Anime Titulky";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(15).Ticks } };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!SeerrClient.Configured)
        {
            return;
        }

        List<SeerrIssue> issues;
        try
        {
            issues = await _seerr.OpenIssuesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            ActivityLog.Write("SEERR     nedostupný: " + ex.Message);
            return;
        }

        var todo = issues.Where(i => i.Type == SeerrClient.SubtitleIssue && !_done.TryGet(i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromDays(3650), out _)).ToList();
        for (var n = 0; n < todo.Count; n++)
        {
            progress.Report(100.0 * n / todo.Count);
            await HandleAsync(todo[n], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(SeerrIssue issue, CancellationToken ct)
    {
        var id = issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var message = string.IsNullOrWhiteSpace(issue.Message) ? await _seerr.FirstCommentAsync(issue.Id, ct).ConfigureAwait(false) : issue.Message;
        var videos = FindVideos(issue);
        if (videos.Count == 0)
        {
            // Not something this server can find (other library, whole series reported…) – leave it to the admin.
            ActivityLog.Write($"SEERR     hlášení #{issue.Id}: díl v knihovně nenalezen (sezóna {issue.Season}, díl {issue.Episode}) – nechávám správci");
            _done.Set(id, "not found");
            return;
        }

        ActivityLog.Write($"SEERR     hlášení #{issue.Id} „{message}“: {videos.Count} díl(ů) – hledám jinou verzi titulků");
        var fixedLines = new List<string>();
        var failed = new List<string>();
        foreach (var video in videos)
        {
            var name = Describe(video);
            if (!Writable(Path.GetDirectoryName(video.Path!)!))
            {
                failed.Add($"{name} (do složky videa nelze zapisovat – plný disk nebo práva)");
                continue;
            }

            var before = _provider.SavedText(video.Id);
            List<string> backups;
            try
            {
                backups = _provider.SetAsideReported(video, $"Seerr #{issue.Id}: {message}");
            }
            catch (IOException ex)
            {
                failed.Add($"{name}: {ex.Message}");
                continue;
            }

            var oldHashes = backups.Select(Hash).ToHashSet();
            var saved = await SearchAgainAsync(video, ct).ConfigureAwait(false);
            var after = _provider.SavedText(video.Id);
            var fresh = NewSidecars(video, backups);
            if (saved && after is not null && fresh.Count > 0 && !fresh.All(f => oldHashes.Contains(Hash(f))))
            {
                CzAnimeSubtitleProvider.DeleteReported(backups);
                fixedLines.Add(before is null
                    ? $"{name}: znovu staženo a srovnáno – {after}"
                    : $"{name}: {after} (místo {before})");
            }
            else
            {
                CzAnimeSubtitleProvider.RestoreReported(backups);
                failed.Add(name);
            }

            await video.RefreshMetadata(new MetadataRefreshOptions(new DirectoryService(_fileSystem)), ct).ConfigureAwait(false);
        }

        var text = "CZ Anime Titulky: ";
        if (fixedLines.Count > 0)
        {
            text += "titulky vyměněny – " + string.Join("; ", fixedLines) + ". Zkuste prosím přehrát znovu.";
        }

        if (failed.Count > 0)
        {
            text += (fixedLines.Count > 0 ? " " : string.Empty) + "Jiná použitelná verze zatím není: " + string.Join(", ", failed) + " – původní titulky ponechány. Správce může přidat jiné titulky do vlastní sbírky nebo je vybrat ručně (Upravit titulky → Hledat).";
        }

        try
        {
            await _seerr.CommentAsync(issue.Id, text, ct).ConfigureAwait(false);
            if (failed.Count == 0 && Plugin.Instance?.Configuration.SeerrResolve != false)
            {
                await _seerr.ResolveAsync(issue.Id, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Seerr comment failed");
        }

        ActivityLog.Write($"SEERR     hlášení #{issue.Id}: vyměněno {fixedLines.Count}, beze změny {failed.Count}");
        _done.Set(id, $"fixed {fixedLines.Count}, failed {failed.Count}");
    }

    /// <summary>Automatic search through Jellyfin limited to this plugin, then download of the best result.</summary>
    private async Task<bool> SearchAgainAsync(Video video, CancellationToken ct)
    {
        var others = _subtitles.GetSupportedProviders(video).Select(p => p.Name).Where(n => n != _provider.Name).ToArray();
        var request = new SubtitleSearchRequest
        {
            ContentType = video is MediaBrowser.Controller.Entities.Movies.Movie ? VideoContentType.Movie : VideoContentType.Episode,
            Language = "cze",
            MediaPath = video.Path,
            Name = video.Name,
            IndexNumber = video.IndexNumber,
            ParentIndexNumber = video.ParentIndexNumber,
            ProductionYear = video.ProductionYear,
            ProviderIds = video.ProviderIds,
            SeriesName = (video as MediaBrowser.Controller.Entities.TV.Episode)?.SeriesName,
            SearchAllProviders = false,
            IsAutomated = true,
            DisabledSubtitleFetchers = others,
            SubtitleFetcherOrder = new[] { _provider.Name },
        };
        try
        {
            var result = (await _subtitles.SearchSubtitles(request, ct).ConfigureAwait(false)).FirstOrDefault();
            if (result is null)
            {
                return false;
            }

            await _subtitles.DownloadSubtitles(video, result.Id, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Seerr re-search for {Video}: {Error}", video.Name, ex.Message);
            return false;
        }
    }

    private List<Video> FindVideos(SeerrIssue issue)
    {
        if (!Guid.TryParse(issue.JellyfinId, out var itemId) || _library.GetItemById(itemId) is not BaseItem item)
        {
            return new List<Video>();
        }

        if (item is Video single)
        {
            return new List<Video> { single };
        }

        if (issue.Season <= 0)
        {
            return new List<Video>(); // whole series – too broad, needs a season/episode
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { itemId },
            ParentIndexNumber = issue.Season,
            Recursive = true,
        };
        if (issue.Episode > 0)
        {
            query.IndexNumber = issue.Episode;
        }

        return _library.GetItemList(query).OfType<Video>().Where(v => v.Path is not null).Take(MaxEpisodes).ToList();
    }

    private static bool Writable(string dir)
    {
        var probe = Path.Combine(dir, $".czat-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, new byte[16]);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Hash(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
    }

    /// <summary>Subtitle sidecars that appeared next to the video (the set-aside ones excluded); identical copies are removed.</summary>
    private static List<string> NewSidecars(Video video, List<string> backups)
    {
        var stem = Path.GetFileNameWithoutExtension(video.Path!);
        var files = Directory.EnumerateFiles(Path.GetDirectoryName(video.Path!)!, stem + ".*")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"\.(ass|ssa|srt)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();
        var old = backups.Select(Hash).ToHashSet();
        foreach (var f in files.Where(f => old.Contains(Hash(f))).ToList())
        {
            File.Delete(f); // the same version came back – the backup will be restored instead
            files.Remove(f);
        }

        return files.Count > 0 ? files : backups.Select(b => b[..^".nahlaseno".Length]).Where(File.Exists).ToList();
    }

    private static string Describe(Video v) => v is MediaBrowser.Controller.Entities.TV.Episode e
        ? $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00}"
        : v.Name;
}
