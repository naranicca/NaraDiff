using NaraDiff.Core.Diff;
using NaraDiff.Core.Text;

namespace NaraDiff.Core.Merge;

/// <summary>
/// Diff3 style three way merge. Base is compared with both sides; changes that touch the same base
/// lines are grouped, and a group changed by both sides is a conflict unless both sides produced
/// exactly the same replacement.
/// </summary>
public static class ThreeWayMerger
{
    private readonly record struct BaseChange(int BaseStart, int BaseEnd, int OtherStart, int OtherEnd);

    public static MergeResult Merge(IReadOnlyList<TextLine> baseLines, IReadOnlyList<TextLine> leftLines, IReadOnlyList<TextLine> rightLines, DiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseLines);
        ArgumentNullException.ThrowIfNull(leftLines);
        ArgumentNullException.ThrowIfNull(rightLines);
        var effective = (options ?? DiffOptions.Default).Sanitized();
        var leftDiff = DiffEngine.Compare(baseLines, leftLines, effective, cancellationToken);
        var rightDiff = DiffEngine.Compare(baseLines, rightLines, effective, cancellationToken);
        var leftChanges = ToBaseChanges(leftDiff);
        var rightChanges = ToBaseChanges(rightDiff);
        var leftDelta = PrefixDeltas(leftChanges);
        var rightDelta = PrefixDeltas(rightChanges);
        var keys = new LineKeyBuilder(effective);
        var regions = new List<MergeRegion>();
        var basePosition = 0;
        int leftIndex = 0, rightIndex = 0;
        while (leftIndex < leftChanges.Count || rightIndex < rightChanges.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftBefore = leftIndex;
            var rightBefore = rightIndex;
            var groupStart = int.MaxValue;
            if (leftIndex < leftChanges.Count) groupStart = leftChanges[leftIndex].BaseStart;
            if (rightIndex < rightChanges.Count) groupStart = Math.Min(groupStart, rightChanges[rightIndex].BaseStart);
            var groupEnd = groupStart;
            var usesLeft = false;
            var usesRight = false;
            var grew = true;
            while (grew)
            {
                grew = false;
                while (leftIndex < leftChanges.Count && leftChanges[leftIndex].BaseStart <= groupEnd)
                {
                    groupEnd = Math.Max(groupEnd, leftChanges[leftIndex].BaseEnd);
                    leftIndex++;
                    usesLeft = true;
                    grew = true;
                }
                while (rightIndex < rightChanges.Count && rightChanges[rightIndex].BaseStart <= groupEnd)
                {
                    groupEnd = Math.Max(groupEnd, rightChanges[rightIndex].BaseEnd);
                    rightIndex++;
                    usesRight = true;
                    grew = true;
                }
            }
            if (groupStart > basePosition)
                regions.Add(new MergeRegion(regions.Count, MergeRegionKind.Unchanged, basePosition, groupStart - basePosition,
                    basePosition + leftDelta[leftBefore], groupStart - basePosition,
                    basePosition + rightDelta[rightBefore], groupStart - basePosition));
            var leftRange = Range(groupStart, groupEnd, leftDelta, leftBefore, leftIndex);
            var rightRange = Range(groupStart, groupEnd, rightDelta, rightBefore, rightIndex);
            var kind = !usesRight ? MergeRegionKind.LeftChange
                : !usesLeft ? MergeRegionKind.RightChange
                : SameContent(leftLines, leftRange, rightLines, rightRange, keys) ? MergeRegionKind.SameChange
                : MergeRegionKind.Conflict;
            regions.Add(new MergeRegion(regions.Count, kind, groupStart, groupEnd - groupStart, leftRange.Start, leftRange.Count, rightRange.Start, rightRange.Count));
            basePosition = groupEnd;
        }
        if (basePosition < baseLines.Count)
            regions.Add(new MergeRegion(regions.Count, MergeRegionKind.Unchanged, basePosition, baseLines.Count - basePosition,
                basePosition + leftDelta[leftChanges.Count], baseLines.Count - basePosition,
                basePosition + rightDelta[rightChanges.Count], baseLines.Count - basePosition));
        return new MergeResult(baseLines, leftLines, rightLines, regions, leftDiff, rightDiff, effective);
    }

    public static Task<MergeResult> MergeAsync(IReadOnlyList<TextLine> baseLines, IReadOnlyList<TextLine> leftLines, IReadOnlyList<TextLine> rightLines, DiffOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Merge(baseLines, leftLines, rightLines, options, cancellationToken), cancellationToken);

    /// <summary>Diff blocks of base against one side, expressed as base line ranges.</summary>
    private static List<BaseChange> ToBaseChanges(DiffResult diff)
    {
        var changes = new List<BaseChange>(diff.Blocks.Count);
        foreach (var block in diff.Blocks) changes.Add(new BaseChange(block.LeftStart, block.LeftEnd, block.RightStart, block.RightEnd));
        return changes;
    }

    /// <summary>Cumulative line count difference introduced by the first n changes.</summary>
    private static int[] PrefixDeltas(List<BaseChange> changes)
    {
        var deltas = new int[changes.Count + 1];
        for (var i = 0; i < changes.Count; i++)
        deltas[i + 1] = deltas[i] + (changes[i].OtherEnd - changes[i].OtherStart) - (changes[i].BaseEnd - changes[i].BaseStart);
        return deltas;
    }

    /// <summary>
    /// Translates a base line range into the matching range of one side. The group boundaries sit on
    /// unchanged base lines, so adding the cumulative delta before and after the group is exact and
    /// also covers pure insertions, whose base range is empty.
    /// </summary>
    private static (int Start, int Count) Range(int groupStart, int groupEnd, int[] deltas, int firstChange, int lastChangeExclusive)
    {
        var start = groupStart + deltas[firstChange];
        var end = groupEnd + deltas[lastChangeExclusive];
        return (start, Math.Max(0, end - start));
    }

    private static bool SameContent(IReadOnlyList<TextLine> leftLines, (int Start, int Count) left, IReadOnlyList<TextLine> rightLines, (int Start, int Count) right, LineKeyBuilder keys)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            var leftIndex = left.Start + i;
            var rightIndex = right.Start + i;
            if (leftIndex >= leftLines.Count || rightIndex >= rightLines.Count) return false;
            if (!string.Equals(keys.BuildKey(leftLines[leftIndex]), keys.BuildKey(rightLines[rightIndex]), StringComparison.Ordinal)) return false;
        }
        return true;
    }
}