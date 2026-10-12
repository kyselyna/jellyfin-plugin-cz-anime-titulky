using System.Numerics;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sync;

/// <summary>Result of the audio alignment.</summary>
/// <param name="Confidence">Peak z-score of the cross-correlation.</param>
/// <param name="RunnerUp">Best z-score more than 1 s away from the peak.</param>
/// <param name="Piecewise">Shift as a function of the (scaled) start time when parts of the episode need different shifts.</param>
public sealed record AudioResult(double Offset, double Confidence, double RunnerUp, bool Verified, double Ratio = 1, Func<double, double>? Piecewise = null)
{
    /// <summary>Shift to apply to an event starting at <paramref name="t"/>.</summary>
    public double ShiftAt(double t)
    {
        var scaled = t * Ratio;
        return (scaled - t) + (Piecewise is null ? Offset : Piecewise(scaled));
    }
}

/// <summary>
/// Aligns subtitles to the audio: speech activity (from 10 ms speech-band energy) is
/// cross-correlated with "a subtitle is on screen" (same principle as ffsubsync).
/// </summary>
public static class AudioAligner
{
    public const int FramesPerSecond = 100;

    /// <summary>Minimal peak z-score to trust the shift.</summary>
    public const double MinConfidence = 3.0;

    /// <summary>Minimal lead of the peak over any other shift (z-score units).</summary>
    public const double MinLead = 0.7;

    /// <param name="audioStart">Time of the first audio sample in the container (stream start_time).</param>
    public static AudioResult Align(float[] energy, (double Start, double End)[] subs, double maxOffset, double audioStart = 0)
    {
        var speech = SpeechMask(energy);
        AudioResult? best = null;
        foreach (var r in ReferenceAligner.Ratios)
        {
            var scaled = subs.Select(x => (x.Start * r, x.End * r)).ToArray();
            var a = AlignOnce(speech, scaled, maxOffset);
            if (best is null || a.Confidence > best.Confidence + (r == 1 ? -0.3 : 0.3))
            {
                best = a with { Ratio = r };
            }
        }

        // frame i of the decoded audio plays at audioStart + i/100
        best = best! with { Offset = Math.Round(best.Offset + audioStart, 2) };
        if (!best.Verified)
        {
            return best;
        }

        // Check the shift part by part – a different cut (BD vs TV, eyecatch) changes it mid-episode.
        var scaledSubs = subs.Select(x => (x.Start * best.Ratio, x.End * best.Ratio)).OrderBy(x => x.Item1).ToArray();
        var fn = Pieces(speech, scaledSubs, best.Offset - audioStart, audioStart);
        return fn is null ? best : best with { Piecewise = fn };
    }

    /// <summary>
    /// Splits the dialogue into ~8 parts and aligns each against the audio within ±20 s of the global shift.
    /// Returns a piecewise shift when confident parts disagree by more than 0.6 s, otherwise null.
    /// </summary>
    private static Func<double, double>? Pieces(double[] speechMask, (double Start, double End)[] subs, double globalLag, double audioStart)
    {
        const int Window = 20 * FramesPerSecond;
        var n = speechMask.Length;
        var size = Math.Max(15, subs.Length / 10);
        var parts = new List<(double From, double Offset, double Z, double Lead)>();
        for (var i = 0; i < subs.Length; i += size)
        {
            var chunk = subs.Skip(i).Take(size).ToArray();
            if (chunk.Length < 8 && parts.Count > 0)
            {
                break;
            }

            var a = Math.Max(0, (int)((chunk[0].Start - 3) * FramesPerSecond));
            var b = Math.Min(n, (int)((chunk[^1].End + 3) * FramesPerSecond));
            var u = new double[Math.Max(0, b - a)];
            for (var k = 0; k < u.Length; k++)
            {
                u[k] = -1;
            }

            foreach (var (st, en) in chunk)
            {
                for (var f = Math.Max(a, (int)(st * FramesPerSecond)); f < Math.Min(b, (int)(en * FramesPerSecond)); f++)
                {
                    u[f - a] = 1;
                }
            }

            var center = (int)Math.Round(globalLag * FramesPerSecond);
            var scores = new double[(2 * Window) + 1];
            for (var l = -Window; l <= Window; l++)
            {
                var lag = center + l;
                double sum = 0;
                for (var k = 0; k < u.Length; k++)
                {
                    var f = a + k + lag;
                    sum += u[k] * (f >= 0 && f < n ? (2 * speechMask[f]) - 1 : 0);
                }

                scores[l + Window] = sum;
            }

            var bi = 0;
            for (var k = 1; k < scores.Length; k++)
            {
                if (scores[k] > scores[bi])
                {
                    bi = k;
                }
            }

            var mean = scores.Average();
            var std = Math.Sqrt(scores.Select(v => (v - mean) * (v - mean)).Average());
            var second = scores.Where((_, k) => Math.Abs(k - bi) > FramesPerSecond).DefaultIfEmpty(double.MinValue).Max();
            var z = std > 0 ? (scores[bi] - mean) / std : 0;
            var z2 = std > 0 ? (second - mean) / std : 0;
            var off = ((center + bi - Window) / (double)FramesPerSecond) + audioStart;
            parts.Add((chunk[0].Start, Math.Round(off, 2), z, z - z2));
        }

        // A part is trusted when it is confident on its own (z ≥ 3), or fairly confident (z ≥ 2.3) and a
        // neighbouring part agrees with it within 1.5 s – short parts are noisy, a real cut affects several.
        bool Strong(int i) => parts[i].Z >= MinConfidence && parts[i].Lead >= 0.5;
        bool Soft(int i) => parts[i].Z >= 2.3 && parts[i].Lead >= 0.3;
        var trusted = new bool[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            trusted[i] = Strong(i) || (Soft(i) && new[] { i - 1, i + 1 }.Any(j => j >= 0 && j < parts.Count && Soft(j) && Math.Abs(parts[j].Offset - parts[i].Offset) <= 1.5));
        }

        if (trusted.Count(t => t) < Math.Max(2, parts.Count * 0.6))
        {
            return null;
        }

        var global = globalLag + audioStart;
        if (parts.Where((_, i) => trusted[i]).All(p => Math.Abs(p.Offset - global) <= 0.6))
        {
            return null; // consistent – the single shift is right
        }

        var fixedParts = new List<(double From, double Offset)>();
        double? last = null;
        var firstTrusted = parts.First(p => trusted[parts.IndexOf(p)]).Offset;
        for (var i = 0; i < parts.Count; i++)
        {
            var o = trusted[i] ? parts[i].Offset : last ?? firstTrusted;
            fixedParts.Add((parts[i].From, o));
            last = o;
        }

        return t =>
        {
            var o = fixedParts[0].Offset;
            foreach (var p in fixedParts)
            {
                if (p.From <= t + 1e-6)
                {
                    o = p.Offset;
                }
            }

            return o;
        };
    }

    private static AudioResult AlignOnce(double[] speechMask, (double Start, double End)[] subs, double maxOffset)
    {
        var n = speechMask.Length;
        var speech = (double[])speechMask.Clone();

        var subMask = new double[n];
        foreach (var (s, e) in subs)
        {
            var a = Math.Max(0, (int)(s * FramesPerSecond));
            var b = Math.Min(n, (int)(e * FramesPerSecond));
            for (var i = a; i < b; i++)
            {
                subMask[i] = 1;
            }
        }

        // ±1 signals (zero padding = "unknown"), as in ffsubsync
        for (var i = 0; i < n; i++)
        {
            speech[i] = (2 * speech[i]) - 1;
            subMask[i] = (2 * subMask[i]) - 1;
        }

        var maxLag = (int)(maxOffset * FramesPerSecond);
        var size = 1;
        while (size < n + maxLag + 1)
        {
            size <<= 1;
        }

        var fs = Fft(speech, size, false);
        var fu = Fft(subMask, size, false);
        for (var i = 0; i < size; i++)
        {
            fs[i] *= Complex.Conjugate(fu[i]);
        }

        var corr = Fft(fs, size, true);

        // corr[lag] = Σ subs[t]·speech[t+lag]  (lag > 0 → subtitles must move later)
        var values = new double[(2 * maxLag) + 1];
        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            values[lag + maxLag] = corr[(lag + size) % size].Real;
        }

        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }

        var mean = values.Average();
        var std = Math.Sqrt(values.Select(v => (v - mean) * (v - mean)).Average());
        var z = std > 0 ? (values[best] - mean) / std : 0;
        var second = double.MinValue;
        for (var i = 0; i < values.Length; i++)
        {
            if (Math.Abs(i - best) > FramesPerSecond && values[i] > second)
            {
                second = values[i];
            }
        }

        var z2 = std > 0 ? (second - mean) / std : 0;
        var offset = (best - maxLag) / (double)FramesPerSecond;
        return new AudioResult(offset, z, z2, z >= MinConfidence && z - z2 >= MinLead);
    }

    /// <summary>
    /// Speech/no-speech per frame: louder than the local background (10th percentile of the
    /// surrounding 3 s) by a fixed margin. Robust against background music, unlike a global threshold.
    /// </summary>
    public static double[] SpeechMask(float[] energy)
    {
        const int Half = 150;      // ±1.5 s
        const double Margin = 0.35; // log10 energy
        const int Bins = 1000;
        const double Scale = 100;   // bin = 0.01 log10 units
        var n = energy.Length;
        var bin = new int[n];
        for (var i = 0; i < n; i++)
        {
            bin[i] = Math.Clamp((int)(energy[i] * Scale), 0, Bins - 1);
        }

        var hist = new int[Bins];
        int lo = 0, hi = -1;
        var m = new double[n];
        for (var i = 0; i < n; i++)
        {
            var from = Math.Max(0, i - Half);
            var to = Math.Min(n - 1, i + Half);
            while (hi < to)
            {
                hist[bin[++hi]]++;
            }

            while (lo < from)
            {
                hist[bin[lo++]]--;
            }

            var rank = (int)((to - from + 1) * 0.1);
            var acc = 0;
            var b = 0;
            for (; b < Bins; b++)
            {
                acc += hist[b];
                if (acc > rank)
                {
                    break;
                }
            }

            m[i] = energy[i] - (b / Scale) > Margin ? 1 : 0;
        }

        return m;
    }

    private static Complex[] Fft(double[] x, int size, bool inverse)
    {
        var c = new Complex[size];
        for (var i = 0; i < x.Length && i < size; i++)
        {
            c[i] = x[i];
        }

        return Fft(c, size, inverse);
    }

    private static Complex[] Fft(Complex[] input, int size, bool inverse)
    {
        var a = (Complex[])input.Clone();
        for (int i = 1, j = 0; i < size; i++)
        {
            var bit = size >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        for (var len = 2; len <= size; len <<= 1)
        {
            var ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (var i = 0; i < size; i += len)
            {
                Complex w = 1;
                for (var k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + (len / 2)] * w;
                    a[i + k] = u + v;
                    a[i + k + (len / 2)] = u - v;
                    w *= wl;
                }
            }
        }

        if (inverse)
        {
            for (var i = 0; i < size; i++)
            {
                a[i] /= size;
            }
        }

        return a;
    }
}
