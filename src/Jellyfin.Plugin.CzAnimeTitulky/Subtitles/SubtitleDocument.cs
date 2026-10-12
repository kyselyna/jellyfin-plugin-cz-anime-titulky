using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CzAnimeTitulky.Subtitles;

/// <summary>One timed line of a subtitle file.</summary>
public sealed class SubEvent
{
    public int Line { get; init; }

    public double Start { get; set; }

    public double End { get; set; }

    /// <summary>True for spoken dialogue (not signs, songs, karaoke…). Used for timing alignment.</summary>
    public bool IsDialogue { get; init; }
}

/// <summary>
/// A text subtitle (ASS/SSA or SRT) kept line-by-line so that shifting changes nothing but the times.
/// </summary>
public sealed partial class SubtitleDocument
{
    private readonly List<string> _lines;

    private SubtitleDocument(List<string> lines, string format)
    {
        _lines = lines;
        Format = format;
        Events = format == "srt" ? ParseSrt() : ParseAss();
    }

    /// <summary>"ass", "ssa" or "srt".</summary>
    public string Format { get; }

    public List<SubEvent> Events { get; }

    public string Text => string.Join("\n", _lines);

    /// <summary>Sorted, de-duplicated dialogue start times (the alignment fingerprint).</summary>
    public double[] DialogueStarts() =>
        Events.Where(e => e.IsDialogue).Select(e => Math.Round(e.Start, 2)).Distinct().OrderBy(x => x).ToArray();

    /// <summary>Dialogue intervals (start, end).</summary>
    public (double Start, double End)[] DialogueIntervals() =>
        Events.Where(e => e.IsDialogue && e.End > e.Start).Select(e => (e.Start, e.End)).ToArray();

    /// <summary>Parses bytes (UTF-8, UTF-16 or Windows-1250); null when it is not a text subtitle.</summary>
    public static SubtitleDocument? TryLoad(byte[] bytes)
    {
        var text = Decode(bytes);
        var head = text.Length > 4000 ? text[..4000] : text;
        string? format = null;
        if (head.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) || head.Contains("[Events]", StringComparison.OrdinalIgnoreCase))
        {
            format = head.Contains("v4.00+", StringComparison.OrdinalIgnoreCase) || !head.Contains("ScriptType: v4.00", StringComparison.OrdinalIgnoreCase) ? "ass" : "ssa";
        }
        else if (SrtTime().IsMatch(head))
        {
            format = "srt";
        }

        if (format is null)
        {
            return null;
        }

        var doc = new SubtitleDocument(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList(), format);
        return doc.Events.Count > 0 ? doc : null;
    }

    /// <summary>Shifts every event by <paramref name="offsetFor"/>(start) seconds.</summary>
    public void Shift(Func<double, double> offsetFor)
    {
        foreach (var e in Events)
        {
            var o = offsetFor(e.Start);
            e.Start = Math.Max(0, e.Start + o);
            e.End = Math.Max(0, e.End + o);
            _lines[e.Line] = Format == "srt" ? RewriteSrt(_lines[e.Line], e) : RewriteAss(_lines[e.Line], e);
        }
    }

    public void Shift(double offset) => Shift(_ => offset);

    public byte[] ToUtf8() => new UTF8Encoding(false).GetBytes(Text);

    /// <summary>Text field of an ASS event (with override tags), or the text lines of an SRT block.</summary>
    public string GetText(SubEvent e)
    {
        if (Format != "srt")
        {
            var f = _lines[e.Line].Split(',', 10);
            return f.Length == 10 ? f[9] : string.Empty;
        }

        var sb = new List<string>();
        for (var i = e.Line + 1; i < _lines.Count && _lines[i].Trim().Length > 0; i++)
        {
            sb.Add(_lines[i]);
        }

        return string.Join("\\N", sb);
    }

    /// <summary>Replaces the text of an ASS event (ASS only).</summary>
    public void SetText(SubEvent e, string text)
    {
        if (Format == "srt")
        {
            throw new InvalidOperationException("SetText supports ASS only");
        }

        var f = _lines[e.Line].Split(',', 10);
        if (f.Length == 10)
        {
            f[9] = text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\N", StringComparison.Ordinal);
            _lines[e.Line] = string.Join(',', f);
        }
    }

    /// <summary>Style name of an ASS event.</summary>
    public string GetStyle(SubEvent e) => Format == "srt" ? "Default" : (_lines[e.Line].Split(',', 10) is { Length: 10 } f ? f[3] : "Default");

    private static string Decode(byte[] b)
    {
        if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)))
        {
            using var r = new StreamReader(new MemoryStream(b), detectEncodingFromByteOrderMarks: true);
            return r.ReadToEnd();
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(b).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1250).GetString(b);
        }
    }

    private List<SubEvent> ParseAss()
    {
        var list = new List<SubEvent>();
        for (var i = 0; i < _lines.Count; i++)
        {
            var l = _lines[i];
            var isDialogue = l.StartsWith("Dialogue:", StringComparison.Ordinal);
            if (!isDialogue && !l.StartsWith("Comment:", StringComparison.Ordinal))
            {
                continue;
            }

            var f = l.Split(',', 10);
            if (f.Length < 10 || !TryAssTime(f[1], out var st) || !TryAssTime(f[2], out var en))
            {
                continue;
            }

            var style = f[3].ToLowerInvariant();
            var text = AssTags().Replace(f[9], string.Empty).Trim();
            var sign = SignStyle().IsMatch(style) && !MainStyle().IsMatch(style);
            list.Add(new SubEvent { Line = i, Start = st, End = en, IsDialogue = isDialogue && !sign && text.Length > 0 });
        }

        return list;
    }

    private List<SubEvent> ParseSrt()
    {
        var list = new List<SubEvent>();
        for (var i = 0; i < _lines.Count; i++)
        {
            var m = SrtTime().Match(_lines[i]);
            if (m.Success)
            {
                list.Add(new SubEvent { Line = i, Start = SrtSeconds(m, 1), End = SrtSeconds(m, 5), IsDialogue = true });
            }
        }

        return list;
    }

    private static bool TryAssTime(string s, out double t)
    {
        t = 0;
        var p = s.Trim().Split(':');
        if (p.Length != 3
            || !int.TryParse(p[0], out var h)
            || !int.TryParse(p[1], out var m)
            || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
        {
            return false;
        }

        t = (h * 3600) + (m * 60) + sec;
        return true;
    }

    private static string AssTime(double t)
    {
        var cs = (long)Math.Round(t * 100);
        return string.Create(CultureInfo.InvariantCulture, $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }

    private static string SrtTimeString(double t)
    {
        var ms = (long)Math.Round(t * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    private static double SrtSeconds(Match m, int g) =>
        (int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) * 3600)
        + (int.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture) * 60)
        + int.Parse(m.Groups[g + 2].Value, CultureInfo.InvariantCulture)
        + (int.Parse(m.Groups[g + 3].Value.PadRight(3, '0')[..3], CultureInfo.InvariantCulture) / 1000.0);

    private static string RewriteAss(string line, SubEvent e)
    {
        var f = line.Split(',', 10);
        f[1] = AssTime(e.Start);
        f[2] = AssTime(e.End);
        return string.Join(',', f);
    }

    private static string RewriteSrt(string line, SubEvent e) =>
        SrtTime().Replace(line, $"{SrtTimeString(e.Start)} --> {SrtTimeString(e.End)}", 1);

    [GeneratedRegex(@"(\d+):(\d\d):(\d\d)[,.](\d{1,3})\s*-->\s*(\d+):(\d\d):(\d\d)[,.](\d{1,3})")]
    private static partial Regex SrtTime();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssTags();

    [GeneratedRegex("sign|song|op|ed|kara|title|note|ttl|screen|caption")]
    private static partial Regex SignStyle();

    [GeneratedRegex("default|main|dial|top|italic|flash")]
    private static partial Regex MainStyle();
}
