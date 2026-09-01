using System.Text;
using System.Text.RegularExpressions;
using NaraDiff.Core.Text;

namespace NaraDiff.Core.Diff;

/// <summary>
/// Turns a source line into the string that the diff algorithms compare, applying the ignore
/// options, and decides whether a line is excluded from the comparison altogether.
/// </summary>
public sealed class LineKeyBuilder
{
    private readonly DiffOptions _options;
    private readonly Regex[] _ignorePatterns;
    private readonly string[] _ignorePrefixes;

    public LineKeyBuilder(DiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        var patterns = new List<Regex>();
        foreach (var pattern in options.IgnoredLinePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try { patterns.Add(new Regex(pattern, RegexOptions.CultureInvariant)); }
            catch (ArgumentException) { }
        }
        _ignorePatterns = [.. patterns];
        _ignorePrefixes = [.. options.IgnoredLinePrefixes.Where(prefix => !string.IsNullOrEmpty(prefix))];
    }

    /// <summary>Separator inserted before the terminator name so it cannot collide with line text.</summary>
    private static readonly string EndingMarker = ((char)1).ToString();

    public bool HasIgnoreRules => _options.IgnoreBlankLines || _ignorePatterns.Length > 0 || _ignorePrefixes.Length > 0;

    /// <summary>True when the line must not take part in the comparison.</summary>
    public bool IsIgnored(TextLine line)
    {
        var text = line.Text;
        if (_options.IgnoreBlankLines && text.AsSpan().IsWhiteSpace()) return true;
        if (_ignorePrefixes.Length > 0)
        {
            var trimmed = text.AsSpan().TrimStart();
            foreach (var prefix in _ignorePrefixes)
                if (trimmed.StartsWith(prefix, _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
        }
        foreach (var pattern in _ignorePatterns)
            if (pattern.IsMatch(text)) return true;
        return false;
    }

    /// <summary>Builds the comparison key of a line.</summary>
    public string BuildKey(TextLine line)
    {
        var text = line.Text;
        if (_options.TreatTabsAsSpaces && text.Contains('\t')) text = ExpandTabs(text, _options.TabWidth);
        if (_options.IgnoreAllWhitespace) text = RemoveWhitespace(text);
        else
        {
            if (_options.IgnoreLeadingWhitespace) text = text.TrimStart();
            if (_options.IgnoreTrailingWhitespace) text = text.TrimEnd();
            if (_options.IgnoreWhitespaceRuns) text = CollapseWhitespace(text);
        }
        if (_options.IgnoreCase) text = text.ToUpperInvariant();
        if (!_options.IgnoreLineEndings && line.Ending != LineEndingKind.None) text = string.Concat(text, EndingMarker, LineEndings.DisplayName(line.Ending));
        return text;
    }

    public static string ExpandTabs(string text, int tabWidth)
    {
        if (tabWidth <= 0) tabWidth = 1;
        var builder = new StringBuilder(text.Length + 8);
        var column = 0;
        foreach (var c in text)
        {
            if (c == '\t')
            {
                var spaces = tabWidth - column % tabWidth;
                builder.Append(' ', spaces);
                column += spaces;
            }
            else
            {
                builder.Append(c);
                column++;
            }
        }
        return builder.ToString();
    }

    private static string RemoveWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsWhiteSpace(c)) builder.Append(c);
        return builder.ToString();
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inRun = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inRun) builder.Append(' ');
                inRun = true;
            }
            else
            {
                builder.Append(c);
                inRun = false;
            }
        }
        return builder.ToString();
    }
}
