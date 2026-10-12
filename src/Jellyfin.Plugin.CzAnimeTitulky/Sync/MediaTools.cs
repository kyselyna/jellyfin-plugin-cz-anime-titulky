using System.Diagnostics;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.CzAnimeTitulky.Subtitles;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sync;

/// <summary>Runs Jellyfin's own ffmpeg/ffprobe to read embedded subtitles and audio.</summary>
public sealed class MediaTools
{
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<MediaTools> _logger;

    public MediaTools(IMediaEncoder encoder, ILogger<MediaTools> logger)
    {
        _encoder = encoder;
        _logger = logger;
    }

    /// <summary>
    /// Embedded text subtitle tracks in English or without language, each parsed.
    /// One ffmpeg pass over the file (reads the whole file – ~15 s over a network share).
    /// </summary>
    public async Task<List<SubtitleDocument>> ExtractReferenceTracksAsync(string video, CancellationToken ct)
    {
        var result = new List<SubtitleDocument>();
        var probe = await RunAsync(_encoder.ProbePath, new[] { "-v", "error", "-select_streams", "s", "-show_entries", "stream=index,codec_name:stream_tags=language", "-of", "json", video }, ct).ConfigureAwait(false);
        if (probe is null)
        {
            return result;
        }

        var text = new HashSet<string> { "ass", "ssa", "subrip", "srt", "mov_text", "webvtt", "text" };
        var streams = (JsonNode.Parse(System.Text.Encoding.UTF8.GetString(probe))?["streams"]?.AsArray() ?? new JsonArray())
            .Where(s => text.Contains((string?)s?["codec_name"] ?? string.Empty))
            .Select(s => (Index: (int)s!["index"]!, Lang: ((string?)s?["tags"]?["language"] ?? "und").ToLowerInvariant()))
            .Where(s => s.Lang is not ("cze" or "ces" or "cs" or "slo" or "slk" or "sk")) // never compare with ourselves
            .ToList();
        // English first; other languages are just as good for timing (only start times are compared).
        var english = streams.Where(s => s.Lang is "eng" or "en" or "und" or "").Select(s => s.Index).ToList();
        var other = streams.Select(s => s.Index).Except(english).ToList();
        return await ExtractAsync(video, english, ct).ConfigureAwait(false) is { Count: > 0 } en && en.Max(d => d.DialogueStarts().Length) >= 50
            ? en
            : (await ExtractAsync(video, english.Concat(other).ToList(), ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The embedded text subtitle to translate: English with the most dialogue lines, otherwise any
    /// non-Czech/Slovak track. Returns the parsed track and its language code.
    /// </summary>
    public async Task<(SubtitleDocument Doc, string Language)?> ExtractTranslationSourceAsync(string video, CancellationToken ct)
    {
        var probe = await RunAsync(_encoder.ProbePath, new[] { "-v", "error", "-select_streams", "s", "-show_entries", "stream=index,codec_name:stream_tags=language", "-of", "json", video }, ct).ConfigureAwait(false);
        if (probe is null)
        {
            return null;
        }

        var text = new HashSet<string> { "ass", "ssa", "subrip", "srt", "mov_text", "webvtt", "text" };
        var streams = (JsonNode.Parse(System.Text.Encoding.UTF8.GetString(probe))?["streams"]?.AsArray() ?? new JsonArray())
            .Where(s => text.Contains((string?)s?["codec_name"] ?? string.Empty))
            .Select(s => (Index: (int)s!["index"]!, Lang: ((string?)s?["tags"]?["language"] ?? "und").ToLowerInvariant()))
            .Where(s => s.Lang is not ("cze" or "ces" or "cs" or "slo" or "slk" or "sk"))
            .ToList();
        (SubtitleDocument Doc, string Language)? best = null;
        foreach (var group in new[] { streams.Where(s => s.Lang is "eng" or "en").ToList(), streams.Where(s => s.Lang is not ("eng" or "en")).ToList() })
        {
            foreach (var s in group)
            {
                var docs = await ExtractAsync(video, new List<int> { s.Index }, ct).ConfigureAwait(false);
                var d = docs.FirstOrDefault();
                if (d is not null && d.DialogueStarts().Length >= 50 && (best is null || d.DialogueStarts().Length > best.Value.Doc.DialogueStarts().Length))
                {
                    best = (d, s.Lang);
                }
            }

            if (best is not null)
            {
                return best; // English found – do not translate from a third language
            }
        }

        return best;
    }

    private async Task<List<SubtitleDocument>> ExtractAsync(string video, List<int> streams, CancellationToken ct)
    {
        var result = new List<SubtitleDocument>();
        if (streams.Count == 0)
        {
            return result;
        }

        var dir = Directory.CreateTempSubdirectory("czat-");
        try
        {
            var args = new List<string> { "-v", "error", "-nostdin", "-y", "-i", video };
            foreach (var i in streams)
            {
                args.AddRange(new[] { "-map", "0:" + i, "-c:s", "ass", "-f", "ass", Path.Combine(dir.FullName, i + ".ass") });
            }

            await RunAsync(_encoder.EncoderPath, args, ct, discardOutput: true).ConfigureAwait(false);
            foreach (var f in dir.GetFiles("*.ass"))
            {
                var doc = SubtitleDocument.TryLoad(await File.ReadAllBytesAsync(f.FullName, ct).ConfigureAwait(false));
                if (doc is not null)
                {
                    result.Add(doc);
                }
            }
        }
        finally
        {
            dir.Delete(true);
        }

        return result;
    }

    /// <summary>
    /// Speech-band energy per 10 ms frame (log scale) of the first audio track.
    /// Decodes to 8 kHz mono and band-passes 200–3000 Hz.
    /// </summary>
    /// <returns>Energy frames and the start time of the audio stream.</returns>
    public async Task<(float[] Energy, double Start)?> AudioEnergyAsync(string video, CancellationToken ct)
    {
        // Decoded audio starts at the audio stream's start_time; subtitles count from the container start.
        var probe = await RunAsync(_encoder.ProbePath, new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=start_time:format=start_time", "-of", "json", video }, ct).ConfigureAwait(false);
        double start = 0;
        try
        {
            var j = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(probe ?? Array.Empty<byte>()));
            start = Seconds((string?)j?["streams"]?[0]?["start_time"]) - Seconds((string?)j?["format"]?["start_time"]);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (Math.Abs(start) > 30)
        {
            start = 0;
        }

        var psi = Start(_encoder.EncoderPath, new[] { "-v", "error", "-nostdin", "-i", video, "-vn", "-map", "0:a:0", "-ac", "1", "-ar", "8000", "-af", "highpass=f=200,lowpass=f=3000", "-f", "s16le", "-" });
        using var p = Process.Start(psi);
        if (p is null)
        {
            return null;
        }

        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        float[] frames;
        await using (var s = p.StandardOutput.BaseStream)
        {
            frames = await EnergyMeter.FromPcmAsync(s, ct).ConfigureAwait(false);
        }

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return frames.Length > 6000 ? (frames, start) : null;
    }

    private static double Seconds(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static ProcessStartInfo Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    private async Task<byte[]?> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, bool discardOutput = false)
    {
        using var p = Process.Start(Start(exe, args));
        if (p is null)
        {
            return null;
        }

        var err = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(discardOutput ? Stream.Null : ms, ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            _logger.LogDebug("{Exe} exited {Code}: {Err}", Path.GetFileName(exe), p.ExitCode, (await err.ConfigureAwait(false)).Trim());
        }

        return ms.ToArray();
    }
}
