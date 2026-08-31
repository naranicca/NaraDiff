namespace NaraDiff.Core.Text;

/// <summary>Line terminator style of a single line or of a whole document.</summary>
public enum LineEndingKind
{
    /// <summary>The line has no terminator; it is the last line of the file.</summary>
    None,
    Lf,
    CrLf,
    Cr,
    /// <summary>Document level only: the file mixes several terminator styles.</summary>
    Mixed
}

/// <summary>A single logical line together with the terminator that followed it in the source file.</summary>
public readonly record struct TextLine(string Text, LineEndingKind Ending)
{
    public int Length => Text.Length;

    public override string ToString() => Text;
}

public static class LineEndings
{
    public static string ToLiteral(LineEndingKind kind) => kind switch
    {
        LineEndingKind.Lf => "\n",
        LineEndingKind.CrLf => "\r\n",
        LineEndingKind.Cr => "\r",
        _ => string.Empty
    };

    public static string DisplayName(LineEndingKind kind) => kind switch
    {
        LineEndingKind.Lf => "LF",
        LineEndingKind.CrLf => "CRLF",
        LineEndingKind.Cr => "CR",
        LineEndingKind.Mixed => "Mixed",
        _ => "None"
    };

    /// <summary>Splits text into lines, preserving the terminator of every line.</summary>
    /// <remarks>
    /// A file that ends with a terminator produces a final empty line so that the split is loss-free:
    /// Join(Split(text)) always equals text.
    /// </remarks>
    public static List<TextLine> Split(string text)
    {
        var lines = new List<TextLine>(Math.Max(4, text.Length / 32));
        var start = 0;
        for (var i= 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            var end = i;
            LineEndingKind ending;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') {ending = LineEndingKind.CrLf; i++; }
            else ending = c == '\n' ? LineEndingKind.Lf : LineEndingKind.Cr;
            lines.Add(new TextLine(text[start .. end], ending));
            start = i + 1;
        }
        lines.Add(new TextLine(text[start .. ], LineEndingKind.None));
        return lines;
    }

    public static string Join(IReadOnlyList<TextLine> lines)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in lines) builder.Append(line.Text).Append(ToLiteral(line.Ending));
        return builder.ToString();
    }

    /// <summary>Returns the dominant terminator of the document, or Mixed when several styles are present.</summary>
    public static LineEndingKind Detect(IReadOnlyList<TextLine> lines)
    {
        var seen = LineEndingKind.None;
        foreach (var line in lines)
        {
            if (line.Ending == LineEndingKind.None) continue;
            if (seen == LineEndingKind.None) seen = line.Ending;
            else if (seen != line.Ending) return LineEndingKind.Mixed;
        }
        return seen;
    }

    /// <summary>Rewrites every terminator of the document to the requested style.</summary>
    public static List<TextLine> Normalize(IReadOnlyList<TextLine> lines, LineEndingKind target)
    {
        var result = new List<TextLine>(lines.Count);
        foreach (var line in lines) result.Add(line.Ending == LineEndingKind.None ? line : new TextLine(line.Text, target));
        return result;
    }
}