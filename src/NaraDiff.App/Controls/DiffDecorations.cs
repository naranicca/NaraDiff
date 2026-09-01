using System.Windows.Media;
using NaraDiff.App.Services;
using NaraDiff.Core.Diff;
using NaraDiff.Core.Merge;

namespace NaraDiff.App.Controls;

/// <summary>How one document line is painted.</summary>
public sealed class LineDecoration
{
    public required Brush Fill { get; init; }

    public Brush? EdgeStroke { get; init; }

    public bool IsBlockStart { get; init; }

    public bool IsBlockEnd { get; init; }

    /// <summary>Character ranges inside the line that differ.</summary>
    public List<TextSpan>? Inline { get; init; }

    public Brush? InlineBrush { get; init; }
}

/// <summary>The painting instructions for one editor, addressed by zero based line index.</summary>
public sealed class DiffDecorationSet
{
    private readonly Dictionary<int, LineDecoration> _lines = [];

    public static DiffDecorationSet Empty { get; } = new();

    public int Count => _lines.Count;

    public void Set(int line, LineDecoration decoration) => _lines[line] = decoration;

    public bool TryGet(int line, out LineDecoration? decoration) => _lines.TryGetValue(line, out decoration);

    /// <summary>Builds the decorations of one side of a two way comparison.</summary>
    public static DiffDecorationSet FromDiff(DiffResult result, bool leftSide, DiffPalette palette)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(palette);
        var set = new DiffDecorationSet();
        foreach (var block in result.Blocks)
        {
            var start = leftSide ? block.LeftStart : block.RightStart;
            var count = leftSide ? block.LeftCount : block.RightCount;
            if (count == 0) continue;
            var fill = palette.FillFor(block.Kind, block.IsMoved);
            var stroke = palette.StrokeFor(block.Kind, block.IsMoved);
            var inlineBrush = palette.InlineFor(block.Kind);
            var inlineMap = leftSide ? block.LeftInline : block.RightInline;
            for (var line = start; line < start + count; line++)
            {
                inlineMap.TryGetValue(line, out var spans);
                set.Set(line, new LineDecoration
                {
                    Fill = fill,
                    EdgeStroke = stroke,
                    IsBlockStart = line == start,
                    IsBlockEnd = line == start + count - 1,
                    Inline = spans,
                    InlineBrush = inlineBrush
                });
            }
        }
        return set;
    }

    /// <summary>Builds the decorations of one pane of a three way merge.</summary>
    public static DiffDecorationSet FromMerge(MergeResult result, MergePane pane, DiffPalette palette)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(palette);
        var set = new DiffDecorationSet();
        foreach (var region in result.Regions)
        {
            if (region.Kind == MergeRegionKind.Unchanged) continue;
            var (start, count) = pane switch
            {
                MergePane.Base => (region.BaseStart, region.BaseCount),
                MergePane.Left => (region.LeftStart, region.LeftCount),
                _ => (region.RightStart, region.RightCount)
            };
            if (count == 0) continue;
            // An unresolved conflict is painted in the conflict colour; once it is resolved it becomes an
            // ordinary merged change so the eye is drawn only to what still needs a decision.
            var resolvedConflict = region.IsConflict && region.IsResolved;
            var fill = region.IsConflict && !region.IsResolved ? palette.ConflictFill : resolvedConflict ? palette.ModifyFill : palette.FillFor(region.Kind);
            var stroke = region.IsConflict && !region.IsResolved ? palette.ConflictStroke : resolvedConflict ? palette.ModifyStroke : palette.StrokeFor(region.Kind);
            for (var line = start; line < start + count; line++)
                set.Set(line, new LineDecoration
                {
                    Fill = fill,
                    EdgeStroke = stroke,
                    IsBlockStart = line == start,
                    IsBlockEnd = line == start + count - 1
                });
        }
        return set;
    }

    /// <summary>Builds a set that only marks search matches.</summary>
    public static DiffDecorationSet FromMatches(IEnumerable<(int Line, TextSpan Span)> matches, Brush brush)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var set = new DiffDecorationSet();
        foreach (var group in matches.GroupBy(match => match.Line))
            set.Set(group.Key, new LineDecoration
            {
                Fill = Brushes.Transparent,
                Inline = [.. group.Select(match => match.Span)],
                InlineBrush = brush
            });
        return set;
    }
}

public enum MergePane
{
    Left,
    Base,
    Right,
    Result
}
