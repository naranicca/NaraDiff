using System.Text.RegularExpressions;

namespace NaraDiff.Core.Diff;

public enum DiffAlgorithmKind
{
    /// <summary>Minimal edit script (Myers, linear space divide and conquer).</summary>
    Myers,
    /// <summary>Anchors on lines that are unique on both sides; produces very readable hunks.</summary>
    Patience,
    /// <summary>Anchors on the rarest common lines; the default because it is fast and readable.</summary>
    Histogram
}

public enum InlineDiffMode
{
    None,
    Word,
    Character
}

/// <summary>Everything that influences how two line sequences are compared.</summary>
public sealed class DiffOptions
{
    public const int MaxTabWidth = 16;

    public string Name { get; set; } = "Default";

    public DiffAlgorithmKind Algorithm { get; set; } = DiffAlgorithmKind.Histogram;

    public bool IgnoreLeadingWhitespace { get; set; }

    public bool IgnoreTrailingWhitespace { get; set; }

    /// <summary>Removes every whitespace character before comparing.</summary>
    public bool IgnoreAllWhitespace { get; set; }

    /// <summary>Collapses runs of whitespace so that only the number of spaces differs.</summary>
    public bool IgnoreWhitespaceRuns { get; set; }

    /// <summary>Expands tabs to <see cref="TabWidth"/> columns so tabs and spaces compare equal.</summary>
    public bool TreatTabsAsSpaces { get; set; }

    public int TabWidth { get; set; } = 4;

    /// <summary>When false, CRLF, LF and CR terminators are reported as differences.</summary>
    public bool IgnoreLineEndings { get; set; } = true;

    public bool IgnoreCase { get; set; }

    /// <summary>Blank lines are hidden from the comparison; they still appear in the editors.</summary>
    public bool IgnoreBlankLines { get; set; }

    /// <summary>Regular expressions; a line matching any of them is excluded from the comparison.</summary>
    public List<string> IgnoredLinePatterns { get; set; } = [];

    /// <summary>Prefixes (after optional leading whitespace) that exclude a line, for example comments.</summary>
    public List<string> IgnoredLinePrefixes { get; set; } = [];

    public bool DetectMoves { get; set; } = true;

    public InlineDiffMode InlineMode { get; set; } = InlineDiffMode.Word;

    /// <summary>Inline (word or character) refinement is skipped for blocks larger than this.</summary>
    public int InlineBlockLineLimit { get; set; } = 500;

    public static DiffOptions Default => new();

    public DiffOptions Clone() => new()
    {
        Name = Name,
        Algorithm = Algorithm,
        IgnoreLeadingWhitespace = IgnoreLeadingWhitespace,
        IgnoreTrailingWhitespace = IgnoreTrailingWhitespace,
        IgnoreAllWhitespace = IgnoreAllWhitespace,
        IgnoreWhitespaceRuns = IgnoreWhitespaceRuns,
        TreatTabsAsSpaces = TreatTabsAsSpaces,
        TabWidth = TabWidth,
        IgnoreLineEndings = IgnoreLineEndings,
        IgnoreCase = IgnoreCase,
        IgnoreBlankLines = IgnoreBlankLines,
        IgnoredLinePatterns = [.. IgnoredLinePatterns],
        IgnoredLinePrefixes = [.. IgnoredLinePrefixes],
        DetectMoves = DetectMoves,
        InlineMode = InlineMode,
        InlineBlockLineLimit = InlineBlockLineLimit
    };

    /// <summary>Clamps values that arrive from settings files or the option panel.</summary>
    public DiffOptions Sanitized()
    {
        var clone = Clone();
        clone.TabWidth = Math.Clamp(clone.TabWidth, 1, MaxTabWidth);
        clone.InlineBlockLineLimit = Math.Clamp(clone.InlineBlockLineLimit, 0, 100_000);
        clone.IgnoredLinePatterns = [.. clone.IgnoredLinePatterns.Where(pattern => !string.IsNullOrWhiteSpace(pattern) && IsValidPattern(pattern))];
        clone.IgnoredLinePrefixes = [.. clone.IgnoredLinePrefixes.Where(prefix => !string.IsNullOrEmpty(prefix))];
        return clone;
    }

    public static bool IsValidPattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A short human readable summary used in the status bar.</summary>
    public string Describe()
    {
        var parts = new List<string> { Algorithm.ToString() };
        if (IgnoreAllWhitespace) parts.Add("no whitespace");
        else
        {
            if (IgnoreLeadingWhitespace) parts.Add("no indent");
            if (IgnoreTrailingWhitespace) parts.Add("no trailing");
            if (IgnoreWhitespaceRuns) parts.Add("space runs");
        }
        if (TreatTabsAsSpaces) parts.Add($"tab={TabWidth}");
        if (IgnoreCase) parts.Add("no case");
        if (IgnoreBlankLines) parts.Add("no blanks");
        if (!IgnoreLineEndings) parts.Add("EOL aware");
        if (IgnoredLinePatterns.Count > 0 || IgnoredLinePrefixes.Count > 0) parts.Add("line rules");
        return string.Join(", ", parts);
    }
}
