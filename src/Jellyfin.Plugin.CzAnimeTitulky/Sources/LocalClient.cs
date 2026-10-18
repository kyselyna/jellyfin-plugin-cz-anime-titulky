using System.Text.RegularExpressions;
using Jellyfin.Plugin.CzAnimeTitulky.Subtitles;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>One series/season found in the local collection: a folder or an archive (or a folder inside an archive).</summary>
public sealed record LocalEntry(string Name, string Path, string? Inner)
{
    /// <summary>Index key (normalized name) – the only thing that leaves the server in a subtitle id.</summary>
    public string Key { get; init; } = string.Empty;
}

/// <summary>
/// Your own subtitle collection (folders configured in the settings): folders and zip/rar archives named after
/// the anime, matched by title like the TeamNS archive; the episode is taken from the file name. Read-only.
/// </summary>
public sealed partial class LocalClient
{
    private static readonly string[] SubExt = { ".ass", ".ssa", ".srt" };
    private readonly ILogger<LocalClient> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, LocalEntry>? _index;
    private string _indexKey = string.Empty;
    private DateTime _indexAt;

    public LocalClient(ILogger<LocalClient> logger)
    {
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    public static IReadOnlyList<string> Folders() =>
        Config.LocalFolders.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists).ToList();

    /// <summary>All indexed entries (re-scans when <paramref name="rescan"/>).</summary>
    public async Task<IReadOnlyList<LocalEntry>> ListAsync(bool rescan, CancellationToken ct)
    {
        if (rescan)
        {
            _index = null;
        }

        return (await IndexAsync(ct).ConfigureAwait(false)).Values.OrderBy(e => e.Name).ToList();
    }

    /// <summary>Entry for one of the season titles, or null.</summary>
    /// <summary>
    /// Entry for a season. An entry named with a season ("Nisekoi-s2", "Season 2", "Overlord II") only serves that
    /// season; an entry without one serves season 1 – and never a later season when it is just the series name
    /// (AniList calls Nisekoi season 2 "Nisekoi:", which would otherwise hit the season 1 archive).
    /// </summary>
    public async Task<LocalEntry?> FindAsync(IEnumerable<string> titles, int season, string? seriesName, CancellationToken ct)
    {
        var index = await IndexAsync(ct).ConfigureAwait(false);
        if (index.Count == 0)
        {
            return null;
        }

        var series = seriesName is null ? null : TeamNsClient.Normalize(seriesName);
        var usable = index.Where(kv => EntrySeason(kv.Value.Name) is { } s
                ? s == season
                : season <= 1 || (series is not null && kv.Key != series))
            .ToDictionary(k => k.Key, k => k.Key);
        var all = titles.ToList();
        if (season > 1 && seriesName is not null)
        {
            all.AddRange(new[] { $"{seriesName} s{season}", $"{seriesName} season {season}", $"{seriesName} {season}" });
        }

        var key = TeamNsClient.Match(all, usable);
        if (key is null && season > 1)
        {
            // A collection of several seasons under the series name is fine when its files say "S02E05".
            var whole = TeamNsClient.Match(all, index.Where(kv => EntrySeason(kv.Value.Name) is null).ToDictionary(k => k.Key, k => k.Key));
            if (whole is not null && HasSeasonFiles(index[whole], season))
            {
                key = whole;
            }
        }

        return key is null ? null : index[key];
    }

    private bool HasSeasonFiles(LocalEntry entry, int season)
    {
        if (!IsInside(entry.Path))
        {
            return false;
        }

        IEnumerable<string> names;
        if (Directory.Exists(entry.Path))
        {
            names = Directory.EnumerateFiles(entry.Path, "*", SearchOption.AllDirectories).Select(f => System.IO.Path.GetFileName(f));
        }
        else
        {
            using var archive = Open(entry.Path);
            names = archive.Entries.Where(e => !e.IsDirectory && (entry.Inner is null || (e.Key ?? string.Empty).Replace('\\', '/').StartsWith(entry.Inner, StringComparison.Ordinal)))
                .Select(e => System.IO.Path.GetFileName(e.Key ?? string.Empty)).ToList();
        }

        return names.Any(n => SxxEyy().Match(n) is { Success: true } m && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == season);
    }

    /// <summary>Season number written in an entry name, or null.</summary>
    internal static int? EntrySeason(string name)
    {
        var m = SeasonInName().Match(name);
        if (m.Success)
        {
            var g = m.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).First(x => x.Success).Value;
            return g.ToUpperInvariant() switch
            {
                "II" => 2,
                "III" => 3,
                "IV" => 4,
                "V" => 5,
                _ => int.Parse(g, System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        return null;
    }

    // "-s2", "S02", "Season 2", "série 2", "2nd Season", "… II" (also "Mushoku Tensei II  Isekai…"), "… 2" at the end
    [GeneratedRegex(@"(?:^|[\s_\-.\[(])(?:s|season|s[ée]rie|sez[óo]na)\s*0*(\d{1,2})(?=$|[\s_\-.\])])|(\d{1,2})(?:st|nd|rd|th)\s*season|\s(II|III|IV)(?=$|[\s:_\-])|\s(V)$|[\s\-_](\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonInName();

    /// <summary>Entry by its index key (from a subtitle id) – paths never come from outside.</summary>
    public async Task<LocalEntry?> GetAsync(string key, CancellationToken ct) =>
        (await IndexAsync(ct).ConfigureAwait(false)).GetValueOrDefault(key);

    /// <summary>The subtitle for <paramref name="episode"/> (season-aware when names carry SxxEyy).</summary>
    public SubtitleDocument? Load(LocalEntry entry, int season, int episode)
    {
        if (!IsInside(entry.Path))
        {
            return null; // only inside the configured folders
        }

        if (Directory.Exists(entry.Path))
        {
            var file = Directory.EnumerateFiles(entry.Path, "*", SearchOption.AllDirectories)
                .Where(f => SubExt.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()) && new FileInfo(f).Length < 10 * 1024 * 1024)
                .Where(f => Fits(f, season, episode))
                .OrderBy(f => f.Length)
                .FirstOrDefault();
            return file is null ? null : SubtitleDocument.TryLoad(File.ReadAllBytes(file));
        }

        using var archive = Open(entry.Path);
        var hit = archive.Entries
            .Where(e => !e.IsDirectory && e.Size < 10 * 1024 * 1024)
            .Where(e => entry.Inner is null || (e.Key ?? string.Empty).Replace('\\', '/').StartsWith(entry.Inner, StringComparison.Ordinal))
            .Where(e => SubExt.Contains(System.IO.Path.GetExtension(e.Key ?? string.Empty).ToLowerInvariant()) && Fits(e.Key!, season, episode))
            .FirstOrDefault();
        if (hit is null)
        {
            return null;
        }

        using var s = hit.OpenEntryStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return SubtitleDocument.TryLoad(ms.ToArray());
    }

    /// <summary>Normalized name → entry for every configured folder (refreshed every 6 h or when the folders change).</summary>
    private async Task<Dictionary<string, LocalEntry>> IndexAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = Config.LocalFolders;
            if (_index is not null && _indexKey == key && _indexAt > DateTime.UtcNow.AddHours(-6))
            {
                return _index;
            }

            var map = new Dictionary<string, LocalEntry>();
            foreach (var root in Folders())
            {
                await Task.Run(() => Scan(root, map), ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Local subtitle collection: {Count} series/seasons", map.Count);
            Util.ActivityLog.Write($"VLASTNÍ   sbírka titulků: {map.Count} sérií/sezón v {Folders().Count} složkách");
            (_index, _indexKey, _indexAt) = (map, key, DateTime.UtcNow);
            return map;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Scan(string root, Dictionary<string, LocalEntry> map)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir).ToList();
                dirs = Directory.EnumerateDirectories(dir).Where(d => !System.IO.Path.GetFileName(d).StartsWith('@') && !System.IO.Path.GetFileName(d).StartsWith('.')).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (dir != root && files.Any(f => SubExt.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())))
            {
                Add(map, System.IO.Path.GetFileName(dir), new LocalEntry(System.IO.Path.GetFileName(dir), dir, null));
            }

            foreach (var f in files.Where(f => System.IO.Path.GetExtension(f).ToLowerInvariant() is ".zip" or ".rar"))
            {
                try
                {
                    using var archive = Open(f);
                    var name = System.IO.Path.GetFileNameWithoutExtension(f);
                    var inner = archive.Entries
                        .Where(e => !e.IsDirectory && SubExt.Contains(System.IO.Path.GetExtension(e.Key ?? string.Empty).ToLowerInvariant()))
                        .Select(e => System.IO.Path.GetDirectoryName((e.Key ?? string.Empty).Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty)
                        .Distinct()
                        .ToList();
                    Add(map, name, new LocalEntry(name, f, null));

                    // collection archives (e.g. teamns-archiv.zip: hotovo/<series>/…) – every folder with subtitles is an entry
                    foreach (var d in inner.Where(d => d.Length > 0))
                    {
                        Add(map, d.Split('/')[^1], new LocalEntry(d.Split('/')[^1], f, d + "/"));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Cannot read archive {File}", f);
                }
            }

            foreach (var d in dirs)
            {
                pending.Push(d);
            }
        }
    }

    private static void Add(Dictionary<string, LocalEntry> map, string name, LocalEntry entry)
    {
        var n = TeamNsClient.Normalize(Cleanup().Replace(name.Replace('_', ' ').Replace('`', '\''), " "));
        if (n.Length >= 3 && IsInside(entry.Path))
        {
            map.TryAdd(n, entry with { Key = n });
        }
    }

    /// <summary>True when <paramref name="path"/> (symlinks resolved) lies inside one of the configured folders.</summary>
    private static bool IsInside(string path)
    {
        static string Real(string p)
        {
            var full = System.IO.Path.GetFullPath(p);
            FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            return info.ResolveLinkTarget(true)?.FullName ?? full;
        }

        try
        {
            var target = Real(path);
            return Folders().Any(f =>
            {
                var root = System.IO.Path.TrimEndingDirectorySeparator(Real(f));
                return target == root || target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static IArchive Open(string path) =>
        ArchiveFactory.OpenArchive(path, new ReaderOptions { Password = string.IsNullOrEmpty(Config.ArchivePassword) ? null : Config.ArchivePassword });

    /// <summary>True when the file name is for this episode (and season, when the name says so).</summary>
    private static bool Fits(string name, int season, int episode)
    {
        var file = System.IO.Path.GetFileName(name);
        var se = SxxEyy().Match(file);
        if (se.Success)
        {
            return int.Parse(se.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == season
                && int.Parse(se.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) == episode;
        }

        return PayloadExtractor.EpisodeFromName(file) == episode;
    }

    [GeneratedRegex(@"\s*-\s*všechny titulky|\bkomplet\b|\[[^\]]*\]|\([^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Cleanup();

    [GeneratedRegex(@"S(\d{1,2})E(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SxxEyy();
}
