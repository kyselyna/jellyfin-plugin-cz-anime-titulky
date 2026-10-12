using System.Collections.Concurrent;
using Jellyfin.Plugin.CzAnimeTitulky.Subtitles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sync;

/// <summary>How the timing was checked.</summary>
public enum SyncMethod
{
    /// <summary>Not checked (disabled, or nothing to compare with).</summary>
    None,

    /// <summary>Matched against the embedded English subtitles.</summary>
    Reference,

    /// <summary>Matched against the embedded subtitles part by part (different cut).</summary>
    ReferencePiecewise,

    /// <summary>Matched against the audio.</summary>
    Audio,

    /// <summary>Matched against the audio part by part (different cut).</summary>
    AudioPiecewise,
}

/// <summary>Outcome of a timing check.</summary>
/// <param name="Mismatch">True when a reference exists and the subtitles provably do not fit this video.</param>
public sealed record SyncOutcome(SyncMethod Method, double Offset, double Score, bool Mismatch, double Ratio = 1)
{
    public override string ToString() => Method switch
    {
        SyncMethod.Reference => $"reference {Offset:+0.00;-0.00} s, {Score:P0}{(Ratio == 1 ? string.Empty : $", speed ×{Ratio:0.0000}")}",
        SyncMethod.ReferencePiecewise => $"reference po částech, {Score:P0}{(Ratio == 1 ? string.Empty : $", speed ×{Ratio:0.0000}")}",
        SyncMethod.Audio => $"audio {Offset:+0.00;-0.00} s, z={Score:0.0}{(Ratio == 1 ? string.Empty : $", speed ×{Ratio:0.0000}")}",
        SyncMethod.AudioPiecewise => $"audio po částech (kolem {Offset:+0.00;-0.00} s), z={Score:0.0}{(Ratio == 1 ? string.Empty : $", speed ×{Ratio:0.0000}")}",
        _ => Mismatch ? $"MISMATCH ({Score:P0})" : "unverified",
    };
}

/// <summary>Checks and fixes subtitle timing against the video.</summary>
public sealed class SyncService
{
    private readonly MediaTools _tools;
    private readonly ILogger<SyncService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, double[]? Ref)> _refCache = new();
    private readonly ConcurrentDictionary<string, (DateTime At, (float[] Energy, double Start)? Energy)> _audioCache = new();

    public SyncService(MediaTools tools, ILogger<SyncService> logger)
    {
        _tools = tools;
        _logger = logger;
    }

    /// <summary>Aligns <paramref name="doc"/> to <paramref name="video"/> in place.</summary>
    public async Task<SyncOutcome> SyncAsync(string video, SubtitleDocument doc, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var starts = doc.DialogueStarts();
        if (starts.Length < 20)
        {
            return new SyncOutcome(SyncMethod.None, 0, 0, false);
        }

        if (cfg.EnableReferenceSync)
        {
            var reference = await ReferenceAsync(video, ct).ConfigureAwait(false);
            if (reference is { Length: >= 50 })
            {
                var r = ReferenceAligner.Align(starts, reference, cfg.MaxOffsetSeconds);
                if (!r.Verified)
                {
                    return new SyncOutcome(SyncMethod.None, r.Offset, r.MatchRatio, true);
                }

                doc.Shift(r.ShiftAt);
                return new SyncOutcome(r.Piecewise is null ? SyncMethod.Reference : SyncMethod.ReferencePiecewise, r.Offset, r.MatchRatio, false, r.Ratio);
            }
        }

        if (cfg.EnableAudioSync)
        {
            var energy = await EnergyAsync(video, ct).ConfigureAwait(false);
            if (energy is not null)
            {
                var a = AudioAligner.Align(energy.Value.Energy, doc.DialogueIntervals(), cfg.MaxOffsetSeconds, energy.Value.Start);
                if (a.Verified)
                {
                    doc.Shift(a.ShiftAt);
                    return new SyncOutcome(a.Piecewise is null ? SyncMethod.Audio : SyncMethod.AudioPiecewise, a.Offset, a.Confidence, false, a.Ratio);
                }

                return new SyncOutcome(SyncMethod.None, a.Offset, a.Confidence, false);
            }
        }

        return new SyncOutcome(SyncMethod.None, 0, 0, false);
    }

    private async Task<double[]?> ReferenceAsync(string video, CancellationToken ct)
    {
        Prune();
        if (_refCache.TryGetValue(video, out var c))
        {
            return c.Ref;
        }

        double[]? best = null;
        try
        {
            foreach (var track in await _tools.ExtractReferenceTracksAsync(video, ct).ConfigureAwait(false))
            {
                var s = track.DialogueStarts();
                if (best is null || s.Length > best.Length)
                {
                    best = s; // the "Full Subtitles" track has the most dialogue lines
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading embedded subtitles failed for {Video}", video);
        }

        _refCache[video] = (DateTime.UtcNow, best);
        return best;
    }

    private async Task<(float[] Energy, double Start)?> EnergyAsync(string video, CancellationToken ct)
    {
        if (_audioCache.TryGetValue(video, out var c))
        {
            return c.Energy;
        }

        (float[] Energy, double Start)? e = null;
        try
        {
            e = await _tools.AudioEnergyAsync(video, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading audio failed for {Video}", video);
        }

        _audioCache[video] = (DateTime.UtcNow, e);
        return e;
    }

    private void Prune()
    {
        var old = DateTime.UtcNow.AddMinutes(-30);
        foreach (var k in _refCache.Where(x => x.Value.At < old).Select(x => x.Key).ToList())
        {
            _refCache.TryRemove(k, out _);
        }

        foreach (var k in _audioCache.Where(x => x.Value.At < old).Select(x => x.Key).ToList())
        {
            _audioCache.TryRemove(k, out _);
        }
    }
}
