using System.Text;
using System. Text.RegularExpressions;

namespace NaraDiff.Core.Folders;

/// <summary>
/// Exclusion patterns for the folder comparison. A pattern without a slash is matched against the
/// entry name, a pattern with a slash against the relative path; * and ? never cross a separator
/// while ** does.
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<(Regex Regex, bool PathScoped)> _patterns = [];

    public GlobMatcher(IEnumerable<string> patterns, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        foreach (var pattern in patterns)
        {
            var trimmed = pattern?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var pathScoped = trimmed.Contains('/') || trimmed.Contains('\\');
            try {_patterns.Add((new Regex(Translate(trimmed), options), pathScoped)); }
            catch (ArgumentException) { }
        }
    }

    public bool IsEmpty => _patterns.Count == 0;

    public bool IsExcluded(string name, string relativePath)
    {
        if (_patterns.Count == 0) return false;
        var normalized = relativePath.Replace('\\','/');
        foreach (var (regex, pathScoped) in patterns)
        if (regex. IsMatch(pathScoped ? normalized : name)) return true;
        return false;
    }

    /// <summary>Converts a glob to an anchored regular expression.</summary>
    public static string Translate(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        var normalized = glob.Replace('\\','/').Trim();
        var directoryOnly = normalized.EndsWith('/');
        if (directoryOnly) normalized = normalized.TrimEnd('/');
        var builder = new StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            switch (c)
            {
                case '*':
                    if (i+1 < normalized.Length && normalized[i + 1] == '*')
                    {
                        i++;
                        if (i+1< normalized.Length && normalized[i + 1] == '/') { i++; builder.Append("( ?:.* /)?"); }
                        else builder.Append(" .* ");
                    }
                    else builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                case '[':
                    var close = normalized.IndexOf(']', i + 1);
                    if (close < 0) builder.Append("\\[");
                    else
                    {
                        var set = normalized[(i + 1) .. close];
                        builder.Append('[').Append(set.StartsWith('!')?"^"+Regex.Escape(set[1 .. ]) : Regex.Escape(set)).Append(']');
                        i = close;
                    }
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        // A pattern that names a directory also excludes everything inside it.
        return builder.Append(directoryOnly ? "( ?: / .* )?$" : "$").ToString();
    }
}