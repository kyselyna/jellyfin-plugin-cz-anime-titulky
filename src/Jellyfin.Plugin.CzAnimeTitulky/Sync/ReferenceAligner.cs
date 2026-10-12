namespace Jellyfin.Plugin.CzAnimeTitulky.Sync;

/// <summary>Result of aligning against the embedded reference subtitles.</summary>
/// <param name="Ratio">Speed correction (t' = t·Ratio + Offset) for releases with a different frame rate.</param>
public sealed record ReferenceResult(double Offset, double MatchRatio, double Spread, bool Verified, Func<double, double>? Piecewise, double Ratio = 1)
{
    /// <summary>Shift to apply to an event starting at <paramref name="t"/>.</summary>
    public double ShiftAt(double t)
    {
        var scaled = t * Ratio;
        return (scaled - t) + (Piecewise is null ? Offset : Piecewise(scaled));
    }
}

/// <summary>
/// Aligns subtitle start times with the reference track (the English subtitles embedded in the video):
/// best global shift, then a per-part check that the shift is the same across the whole episode.
/// When the video is cut differently (BD vs TV), a piecewise shift is tried.
/// </summary>
public static class ReferenceAligner
{
    private const double Tolerance = 0.25;

    /// <summary>Speed ratios between common frame rates (23.976 / 24 / 25 fps).</summary>
    public static readonly double[] Ratios = { 1, 24000.0 / 23976, 23976 / 24000.0, 25 / 23.976, 23.976 / 25, 25 / 24.0, 24 / 25.0 };

    public static ReferenceResult Align(double[] subs, double[] reference, double maxOffset)
    {
        // Pick the speed first: a release with a different frame rate drifts steadily.
        var bestRatio = 1.0;
        var bestHits = -1;
        foreach (var r in Ratios)
        {
            var (h, _) = Best(subs.Select(t => t * r).ToArray(), reference, -maxOffset, maxOffset);
            if (h > bestHits * 1.05 || (r == 1 && h >= bestHits))
            {
                (bestHits, bestRatio) = (h, r);
            }
        }

        var res = AlignAtSpeed(subs.Select(t => t * bestRatio).ToArray(), reference, maxOffset);
        return res with { Ratio = bestRatio };
    }

    private static ReferenceResult AlignAtSpeed(double[] subs, double[] reference, double maxOffset)
    {
        var (hits, o) = Best(subs, reference, -maxOffset, maxOffset);
        var pct = (double)hits / subs.Length;

        // Consistency: 5 parts, each must agree on the shift.
        var k = Math.Max(1, subs.Length / 5);
        var good = new List<double>();
        for (var i = 0; i < subs.Length - (k / 2); i += k)
        {
            var part = subs.Skip(i).Take(k).ToArray();
            var (h, po) = Best(part, reference, o - 40, o + 40);
            if ((double)h / part.Length > 0.5)
            {
                good.Add(po);
            }
        }

        var spread = good.Count > 0 ? good.Max() - good.Min() : 99;
        var globalOk = pct >= 0.6 && spread <= 0.5;

        // Different cuts (cold open, eyecatch, preview) can need their own shift even when the global
        // shift fits most of the episode: find the cut points with a small Viterbi segmentation.
        var seg = Segment(subs, reference, o, maxOffset);
        if (seg is not null)
        {
            var after = (double)subs.Count(t => Hit(reference, t + seg(t))) / subs.Length;
            if (after >= 0.6 && (!globalOk || after >= pct + 0.03))
            {
                return new ReferenceResult(o, after, spread, true, seg);
            }
        }

        return new ReferenceResult(o, pct, spread, globalOk, null);
    }

    /// <summary>
    /// Candidate shifts from sliding windows of 12 lines; then every line gets one candidate so that
    /// (lines that do not match) + 4 × (number of changes) is minimal. Null when one shift is best anyway.
    /// </summary>
    private static Func<double, double>? Segment(double[] subs, double[] reference, double global, double maxOffset)
    {
        const int Win = 12;
        const double SwitchCost = 4;
        const int MaxLines = 3000;      // an episode has ~300–600 dialogue lines
        const int MaxCandidates = 8;
        if (subs.Length > MaxLines)
        {
            return null;
        }

        // window shifts, clustered to 0.15 s, the most frequent ones only (bounded work: Viterbi is n·k²)
        var votes = new List<(double Offset, int Count)>();
        for (var i = 0; i + Win <= subs.Length; i += Win / 2)
        {
            var part = subs.Skip(i).Take(Win).ToArray();
            var (h, po) = Best(part, reference, -maxOffset, maxOffset);
            if ((double)h / Win < 0.6)
            {
                continue;
            }

            var idx = votes.FindIndex(v => Math.Abs(v.Offset - po) <= 0.15);
            if (idx >= 0)
            {
                votes[idx] = (votes[idx].Offset, votes[idx].Count + 1);
            }
            else
            {
                votes.Add((po, 1));
            }
        }

        var cands = new List<double> { global };
        cands.AddRange(votes.Where(v => Math.Abs(v.Offset - global) > 0.15).OrderByDescending(v => v.Count).Take(MaxCandidates - 1).Select(v => v.Offset));
        if (cands.Count == 1)
        {
            return null;
        }

        var n = subs.Length;
        var k = cands.Count;
        var cost = new double[n, k];
        var from = new int[n, k];
        for (var j = 0; j < k; j++)
        {
            cost[0, j] = Hit(reference, subs[0] + cands[j]) ? 0 : 1;
        }

        for (var i = 1; i < n; i++)
        {
            for (var j = 0; j < k; j++)
            {
                var miss = Hit(reference, subs[i] + cands[j]) ? 0 : 1;
                var best = cost[i - 1, j];
                var arg = j;
                for (var q = 0; q < k; q++)
                {
                    if (q != j && cost[i - 1, q] + SwitchCost < best)
                    {
                        best = cost[i - 1, q] + SwitchCost;
                        arg = q;
                    }
                }

                cost[i, j] = best + miss;
                from[i, j] = arg;
            }
        }

        var end = 0;
        for (var j = 1; j < k; j++)
        {
            if (cost[n - 1, j] < cost[n - 1, end])
            {
                end = j;
            }
        }

        var assign = new int[n];
        assign[n - 1] = end;
        for (var i = n - 1; i > 0; i--)
        {
            assign[i - 1] = from[i, assign[i]];
        }

        if (assign.All(a => a == assign[0]))
        {
            return null;
        }

        var starts = (double[])subs.Clone();
        var offsets = assign.Select(a => cands[a]).ToArray();
        return t =>
        {
            var i = Array.BinarySearch(starts, t + 1e-6);
            i = i >= 0 ? i : Math.Max(0, ~i - 1);
            return offsets[Math.Min(i, offsets.Length - 1)];
        };
    }

    public static (int Hits, double Offset) Best(double[] a, double[] b, double from, double to)
    {
        var best = (-1, 0.0);
        for (var o = from; o <= to; o += 0.1)
        {
            var s = Score(a, b, o);
            if (s > best.Item1)
            {
                best = (s, o);
            }
        }

        var c = best.Item2;
        for (var o = c - 0.3; o <= c + 0.3; o += 0.02)
        {
            var s = Score(a, b, o);
            if (s > best.Item1)
            {
                best = (s, o);
            }
        }

        return (best.Item1, Math.Round(best.Item2, 2));
    }

    private static int Score(double[] a, double[] b, double off)
    {
        var n = 0;
        foreach (var x in a)
        {
            if (Hit(b, x + off))
            {
                n++;
            }
        }

        return n;
    }

    private static bool Hit(double[] b, double y)
    {
        var i = Array.BinarySearch(b, y - Tolerance);
        if (i < 0)
        {
            i = ~i;
        }

        return i < b.Length && b[i] <= y + Tolerance;
    }
}
