using System.Text.RegularExpressions;
using Jellyfin.Plugin.CzAnimeTitulky.Sources;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Jellyfin.Plugin.CzAnimeTitulky.Subtitles;

/// <summary>Turns a downloaded file (subtitle, zip, rar, 7z – possibly password protected) into a subtitle.</summary>
public static partial class PayloadExtractor
{
    private const long MaxEntryBytes = 10 * 1024 * 1024;
    private const long MaxTotalBytes = 50 * 1024 * 1024;
    private const int MaxEntries = 500;
    private static readonly string[] SubExt = { ".ass", ".ssa", ".srt" };

    public static SubtitleDocument? Extract(Payload payload, int episode, string password)
    {
        var b = payload.Content;
        // zip and rar only (what the fansub sites use); 7z/LZMA headers can demand huge dictionaries.
        var isArchive = (b[0] == 'P' && b[1] == 'K') || (b.Length > 4 && b[0] == 'R' && b[1] == 'a' && b[2] == 'r' && b[3] == '!');
        if (!isArchive)
        {
            return SubtitleDocument.TryLoad(b);
        }

        var files = new List<(string Name, byte[] Data)>();
        long total = 0;
        Collect(b, password, files, depth: 0, ref total);
        if (files.Count == 0)
        {
            return null;
        }

        // Several files (batch archive) → the one for our episode, else the biggest.
        var pick = files.Count == 1 ? files[0]
            : files.FirstOrDefault(f => EpisodeFromName(f.Name) == episode) is { Data: not null } hit ? hit
            : files.OrderByDescending(f => f.Data.Length).First();
        return SubtitleDocument.TryLoad(pick.Data);
    }

    /// <summary>Episode number from a typical fansub file name ("… - 05 [1080p].ass", "S01E05", "_05.").</summary>
    public static int? EpisodeFromName(string name)
    {
        var b = Cleanup().Replace(Path.GetFileName(name), string.Empty);
        foreach (var re in new[] { SxxEyy(), Dash(), Separated() })
        {
            var m = re.Match(b);
            if (m.Success)
            {
                return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static void Collect(byte[] data, string password, List<(string, byte[])> output, int depth, ref long total)
    {
        using var ms = new MemoryStream(data);
        using var archive = ArchiveFactory.OpenArchive(ms, new ReaderOptions { Password = string.IsNullOrEmpty(password) ? null : password });
        if (archive.Type is not (SharpCompress.Common.ArchiveType.Zip or SharpCompress.Common.ArchiveType.Rar))
        {
            throw new InvalidDataException("Unsupported archive type " + archive.Type);
        }

        if (archive.TotalUncompressedSize > MaxTotalBytes)
        {
            throw new InvalidDataException("Archive expands too much");
        }

        var count = 0;
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            if (++count > MaxEntries)
            {
                throw new InvalidDataException("Archive has too many entries");
            }

            var key = entry.Key ?? string.Empty;
            var ext = Path.GetExtension(key).ToLowerInvariant();
            if (!SubExt.Contains(ext) && !(depth < 2 && ext is ".zip" or ".rar"))
            {
                continue;
            }

            if (entry.Size > MaxEntryBytes)
            {
                continue;
            }

            using var es = entry.OpenEntryStream();
            using var buf = new MemoryStream();
            CopyLimited(es, buf, MaxEntryBytes);
            total += buf.Length;
            if (total > MaxTotalBytes)
            {
                throw new InvalidDataException("Archive expands too much");
            }

            if (SubExt.Contains(ext))
            {
                output.Add((key, buf.ToArray()));
            }
            else
            {
                Collect(buf.ToArray(), password, output, depth + 1, ref total);
            }
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes – the declared size of an entry can lie.</summary>
    private static void CopyLimited(Stream from, Stream to, long limit)
    {
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = from.Read(buf, 0, buf.Length)) > 0)
        {
            done += n;
            if (done > limit)
            {
                throw new InvalidDataException("Archive entry too large");
            }

            to.Write(buf, 0, n);
        }
    }

    [GeneratedRegex(@"\[[0-9A-Fa-f]{8}\]|\(?(?:480|576|720|1080|2160)p\)?|[xh]26[45]|10.?bit", RegexOptions.IgnoreCase)]
    private static partial Regex Cleanup();

    [GeneratedRegex(@"S\d+E(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SxxEyy();

    [GeneratedRegex(@" - (\d{1,3})\b")]
    private static partial Regex Dash();

    [GeneratedRegex(@"[ _E](\d{1,3})[ _.\[]")]
    private static partial Regex Separated();
}
