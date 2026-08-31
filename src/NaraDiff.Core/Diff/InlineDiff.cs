namespace NaraDiff.Core.Diff;

/// <summary>
/// Word and character level refinement inside a pair of changed lines, used for the inline
/// highlighting that shows exactly which part of a line was edited.
/// </summary>
public static class InlineDiff
{
    private readonly record struct Token(int Start, int Length, string Key);

    /// <summary>Computes the differing character ranges of two lines.</summary>
    public static (List<TextSpan> Left, List<TextSpan> Right) Compute(string left, string right, InlineDiffMode mode, DiffOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (mode == InlineDiffMode.None || (left.Length == 0 && right.Length == 0)) return ([], []);
        var leftTokens = Tokenize(left, mode, options);
        var rightTokens = Tokenize(right, mode, options);
        var interner = new Dictionary<string, int>(StringComparer.Ordinal);
        var leftIds = Intern(leftTokens, interner);
        var rightIds = Intern(rightTokens, interner);
        var changes = new MyersSequenceDiff().Diff(leftIds, rightIds, cancellationToken);
        return (BuildSpans(leftTokens, changes, true), BuildSpans(rightTokens, changes, false));
    }

    private static int[] Intern(List<Token> tokens, Dictionary<string, int> interner)
    {
        var ids = new int[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!interner.TryGetValue(tokens[i].Key, out var id))
            {
                id = interner.Count + 1;
                interner[tokens[i].Key] = id;
            }
            ids[i]=id;
        }
        return ids;
    }

    private static List<TextSpan> BuildSpans(List<Token> tokens, List<SequenceChange> changes, bool left)
    {
        var spans = new List<TextSpan>();
        foreach (var change in changes)
        {
            var start = left ? change.LeftStart : change.RightStart;
            var count = left ? change.LeftCount : change.RightCount;
            if (count == 0) continue;
            var first = tokens[start];
            var last = tokens[start + count - 1];
            var span = new TextSpan(first.Start, last.Start + last.Length - first.Start);
            if (spans.Count > 0 && spans[^1].End >= span.Start)
            {
                var previous = spans[^1];
                spans[^1] = new TextSpan(previous.Start, Math.Max(previous.End, span.End) - previous.Start);
            }
            else spans.Add(span);
        }
        return spans;
    }

    /// <summary>
    /// Splits a line into comparison tokens. Word mode keeps identifiers and whitespace runs
    /// together and treats every other character as its own token.
    /// </summary>
    private static List<Token> Tokenize(string text, InlineDiffMode mode, DiffOptions options)
    {
        var tokens = new List<Token>(mode == InlineDiffMode.Character ? text.Length : text.Length / 4 + 4);
        if (mode == InlineDiffMode.Character)
        {
            for (var i = 0; i < text.Length; i++) tokens.Add(new Token(i, 1, NormalizeToken(text.Substring(i, 1), options)));
            return tokens;
        }
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            var c = text[index];
            if (IsWordCharacter(c)) while (index < text.Length && IsWordCharacter(text[index])) index++;
            else if (char.IsWhiteSpace(c)) while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            else index++;
            tokens.Add(new Token(start, index - start, NormalizeToken(text[start .. index], options)));
        }
        return tokens;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string NormalizeToken(string token, DiffOptions options)
    {
        if (token.Length > 0 && char.IsWhiteSpace(token[0]))
        {
            if (options.IgnoreAllWhitespace) return " ";
            if (options.IgnoreWhitespaceRuns) return " ";
            if (options.TreatTabsAsSpaces) return new string(' ', LineKeyBuilder.ExpandTabs(token, options. TabWidth).Length);
        }
        return options.IgnoreCase ? token.ToUpperInvariant() : token;
    }
}