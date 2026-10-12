namespace Jellyfin.Plugin.CzAnimeTitulky.Sync;

/// <summary>Speech-band loudness of decoded audio, one value per 10 ms.</summary>
public static class EnergyMeter
{
    /// <summary>Log energy per 10 ms frame of 8 kHz mono s16le PCM.</summary>
    public static async Task<float[]> FromPcmAsync(Stream s, CancellationToken ct)
    {
        var frames = new List<float>(200_000);
        var buf = new byte[160 * 64];
        var carry = new byte[160];
        var carryLen = 0;
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            var off = 0;
            if (carryLen > 0)
            {
                var take = Math.Min(160 - carryLen, n);
                Array.Copy(buf, 0, carry, carryLen, take);
                carryLen += take;
                off = take;
                if (carryLen == 160)
                {
                    frames.Add(Energy(carry, 0));
                    carryLen = 0;
                }
            }

            for (; off + 160 <= n; off += 160)
            {
                frames.Add(Energy(buf, off));
            }

            if (off < n)
            {
                Array.Copy(buf, off, carry, 0, n - off);
                carryLen = n - off;
            }
        }

        return frames.ToArray();
    }

    private static float Energy(byte[] b, int off)
    {
        double sum = 0;
        for (var i = 0; i < 160; i += 2)
        {
            double v = (short)(b[off + i] | (b[off + i + 1] << 8));
            sum += v * v;
        }

        return (float)Math.Log10(1 + (sum / 80));
    }
}
