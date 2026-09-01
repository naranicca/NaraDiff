using NaraDiff.Core.Text;

namespace NaraDiff.Core.Diff;

public enum DiffBlockKind
{
    /// <summary>Lines exist only on the right side.</summary>
    Insert,
    /// <summary>Lines exist only on the left side.</summary>
    Delete,
    /// <summary>Lines exist on both sides but differ.</summary>
    Modify
}

/// <summary>A character range inside a single line.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>One hunk of the comparison: a left line range paired with a right line range.</summary>
public sealed class DiffBlock
{
    public DiffBlock(int index, DiffBlockKind kind, int leftStart, int leftCount, int rightStart, int rightCount)
    {
        Index = index;
        Kind = kind;
        LeftStart = leftStart;
        LeftCount = leftCount;
        RightStart = rightStart;
        RightCount = rightCount;
    }

    /// <summary>Zero based position of this block in the result, used by navigation commands.</summary>
    public int Index { get; internal set; }

    public DiffBlockKind Kind { get; }

    public int LeftStart { get; }

    public int LeftCount { get; }

    public int RightStart { get; }

    public int RightCount { get; }

    public int LeftEnd => LeftStart + LeftCount;

    public int RightEnd => RightStart + RightCount;

    /// <summary>True when the same content was found as a deletion or insertion elsewhere.</summary>
    public bool IsMoved { get; internal set; }

    public int MovePartner { get; internal set; } = -1;

    /// <summary>Character ranges that differ, keyed by absolute left line index.</summary>
    public Dictionary<int, List<TextSpan>> LeftInline { get; } = [];

    /// <summary>Character ranges that differ, keyed by absolute right line index.</summary>
    public Dictionary<int, List<TextSpan>> RightInline { get; } = [];

    public bool ContainsLeftLine(int line) => LeftCount > 0 && line >= LeftStart && line < LeftEnd;

    public bool ContainsRightLine(int line) => RightCount > 0 && line >= RightStart && line < RightEnd;

    public override string ToString() => $"{Kind} L{LeftStart}+{LeftCount} R{RightStart}+{RightCount}";
}

public sealed class DiffStatistics
{
    public int Inserted { get; init; }

    public int Deleted { get; init; }

    public int Modified { get; init; }

    public int Moved { get; init; }

    public int BlockCount { get; init; }

    public int ChangedLeftLines { get; init; }

    public int ChangedRightLines { get; init; }
}

/// <summary>A left/right line pair used to keep the two editors scrolled to matching content.</summary>
public readonly record struct LineAnchor(int Left, int Right);

/// <summary>The result of comparing two documents.</summary>
public sealed class DiffResult
{
    private readonly List<LineAnchor> _anchors;

    internal DiffResult(IReadOnlyList<TextLine> leftLines, IReadOnlyList<TextLine> rightLines, List<DiffBlock> blocks, DiffOptions options, DiffStatistics statistics)
    {
        LeftLines = leftLines;
        RightLines = rightLines;
        Blocks = blocks;
        Options = options;
        Statistics = statistics;
        _anchors = BuildAnchors(blocks, leftLines.Count, rightLines.Count);
    }

    public IReadOnlyList<TextLine> LeftLines { get; }

    public IReadOnlyList<TextLine> RightLines { get; }

    public IReadOnlyList<DiffBlock> Blocks { get; }

    public DiffOptions Options { get; }

    public DiffStatistics Statistics { get; }

    public bool AreIdentical => Blocks.Count == 0;

    public static DiffResult Empty { get; } = new([], [], [], DiffOptions.Default, new DiffStatistics());

    /// <summary>The block that contains the given left line, or null.</summary>
    public DiffBlock? BlockAtLeftLine(int line) => Blocks.FirstOrDefault(block => block.ContainsLeftLine(line));

    public DiffBlock? BlockAtRightLine(int line) => Blocks.FirstOrDefault(block => block.ContainsRightLine(line));

    /// <summary>The first block starting at or after the given left line.</summary>
    public DiffBlock? BlockAtOrAfterLeftLine(int line) => Blocks.FirstOrDefault(block => block.LeftStart >= line);

    /// <summary>The first block that starts strictly after the given left line.</summary>
    public DiffBlock? NextBlockAfterLeftLine(int line) => Blocks.FirstOrDefault(block => block.LeftStart > line);

    /// <summary>The last block that starts strictly before the given left line.</summary>
    public DiffBlock? PreviousBlockBeforeLeftLine(int line) => Blocks.LastOrDefault(block => block.LeftStart < line);

    /// <summary>Maps a left line to the matching right line, interpolating inside changed blocks.</summary>
    public double MapLeftToRight(double line) => Map(line, true);

    public double MapRightToLeft(double line) => Map(line, false);

    private double Map(double line, bool leftToRight)
    {
        if (_anchors.Count == 0) return line;
        var low = 0;
        var high = _anchors.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var value = leftToRight ? _anchors[middle].Left : _anchors[middle].Right;
            if (value <= line) low = middle;
            else high = middle - 1;
        }
        var start = _anchors[low];
        if (low + 1 >= _anchors.Count)
            return (leftToRight ? start.Right : start.Left) + (line - (leftToRight ? start.Left : start.Right));
        var end = _anchors[low + 1];
        var sourceStart = leftToRight ? start.Left : start.Right;
        var sourceEnd = leftToRight ? end.Left : end.Right;
        var targetStart = leftToRight ? start.Right : start.Left;
        var targetEnd = leftToRight ? end.Right : end.Left;
        if (sourceEnd <= sourceStart) return targetStart;
        var ratio = (line - sourceStart) / (sourceEnd - sourceStart);
        return targetStart + ratio * (targetEnd - targetStart);
    }

    private static List<LineAnchor> BuildAnchors(List<DiffBlock> blocks, int leftCount, int rightCount)
    {
        var anchors = new List<LineAnchor>(blocks.Count * 2 + 2) { new(0, 0) };
        foreach (var block in blocks)
        {
            var start = new LineAnchor(block.LeftStart, block.RightStart);
            if (anchors[^1] != start) anchors.Add(start);
            anchors.Add(new LineAnchor(block.LeftEnd, block.RightEnd));
        }
        var last = new LineAnchor(leftCount, rightCount);
        if (anchors[^1] != last) anchors.Add(last);
        return anchors;
    }
}
