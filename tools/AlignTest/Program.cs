// Offline check of the timing algorithms against episodes with a known correct shift.
// Usage: AlignTest <cases.json> <pcm-dir>   (pcm-dir/<n>.pcm = 8 kHz mono s16le, band-passed)
using System.Text.Json;
using Jellyfin.Plugin.CzAnimeTitulky.Subtitles;
using Jellyfin.Plugin.CzAnimeTitulky.Sync;

var cases = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement.EnumerateArray().ToList();
var i = 0;
foreach (var c in cases)
{
    i++;
    var sub = SubtitleDocument.TryLoad(File.ReadAllBytes(c.GetProperty("sub").GetString()!))!;
    var truth = c.GetProperty("off").GetDouble();
    var line = $"{c.GetProperty("how").GetString(),-14} {c.GetProperty("show").GetString()![..Math.Min(28, c.GetProperty("show").GetString()!.Length)],-28} truth {truth,6:+0.00;-0.00}";
    if (c.TryGetProperty("ref", out var rp) && rp.ValueKind == JsonValueKind.String)
    {
        var r = SubtitleDocument.TryLoad(File.ReadAllBytes(rp.GetString()!))!;
        var rr = ReferenceAligner.Align(sub.DialogueStarts(), r.DialogueStarts(), 90);
        line += $" | ref {rr.Offset,6:+0.00;-0.00} {rr.MatchRatio:P0} ok={rr.Verified} x{rr.Ratio:0.000}{(rr.Piecewise is null ? "" : " piecewise")}";
        if (rr.Piecewise is not null) { var st = sub.DialogueStarts(); line += " [" + string.Join(" ", Enumerable.Range(0, 10).Select(k => st[k * st.Length / 10]).Select(t => $"{t / 60:0.0}m:{rr.ShiftAt(t):+0.0;-0.0}")) + "]"; }
    }

    var pcm = Path.Combine(args[1], i + ".pcm");
    if (File.Exists(pcm))
    {
        await using var fs = File.OpenRead(pcm);
        var e = await EnergyMeter.FromPcmAsync(fs, CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = AudioAligner.Align(e, sub.DialogueIntervals(), 90, c.TryGetProperty("astart", out var st) ? st.GetDouble() : 0);
        if (a.Piecewise is not null) { var ts = sub.DialogueIntervals().Select(x => x.Start).OrderBy(x => x).ToArray(); line += " | parts:" + string.Join(",", Enumerable.Range(0, 12).Select(k => ts[k * ts.Length / 12]).Select(t => $"{t:0}s→{a.ShiftAt(t):+0.0;-0.0}")); }
        line += $" | audio {a.Offset,6:+0.00;-0.00} z={a.Confidence,5:0.0}/{a.RunnerUp,4:0.0} ok={a.Verified} x{a.Ratio:0.000} ({sw.ElapsedMilliseconds} ms)";
    }

    Console.WriteLine(line);
}
