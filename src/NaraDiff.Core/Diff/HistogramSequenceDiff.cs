namespace NaraDiff.Core.Diff;

/// <summary>
/// Histogram diff: splits a region on the longest common run built from the rarest shared line, then
/// recurses on both sides. It is fast on large files, stable against repeated lines, and falls back
/// to Myers when no rare anchor can be found.
/// </summary>
public sealed class HistogramSequenceDiff : ISequenceDiffAlgorithm
{
    /// <summary>Lines occurring more often than this on the left are not considered as anchors.</summary>
    private const int MaxOccurrences = 64;

    private readonly MyersSequenceDiff _fallback = new();

    public DiffAlgorithmKind Kind => DiffAlgorithmKind.Histogram;

    public List<SequenceChange> Diff(int[] left, int[] right, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var changes = new List<SequenceChange>();
        Compute(left, 0, left.Length, right, 0, right.Length, changes, cancellationToken);
        return SequenceDiffAlgorithms.Normalize(changes);
    }

    private void Compute(int[] a, int aLow, int aHigh, int[] b, int bLow, int bHigh, List<SequenceChange> changes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (aLow < aHigh && bLow < bHigh && a[aLow] == b[bLow]) { aLow++; bLow++; }
        while (aLow < aHigh && bLow < bHigh && a[aHigh - 1] == b[bHigh - 1]) { aHigh--; bHigh--; }
        if (aLow == aHigh && bLow == bHigh) return;
        if (aLow == aHigh || bLow == bHigh)
        {
            changes.Add(new SequenceChange(aLow, aHigh - aLow, bLow, bHigh - bLow));
            return;
        }
        if (!TryFindAnchor(a, aLow, aHigh, b, bLow, bHigh, out var anchor))
        {
            var leftSlice = a[aLow..aHigh];
            var rightSlice = b[bLow..bHigh];
            foreach (var change in _fallback.Diff(leftSlice, rightSlice, cancellationToken))
                changes.Add(new SequenceChange(change.LeftStart + aLow, change.LeftCount, change.RightStart + bLow, change.RightCount));
            return;
        }
        Compute(a, aLow, anchor.LeftStart, b, bLow, anchor.RightStart, changes, cancellationToken);
        Compute(a, anchor.LeftStart + anchor.Length, aHigh, b, anchor.RightStart + anchor.Length, bHigh, changes, cancellationToken);
    }

    private readonly record struct Anchor(int LeftStart, int RightStart, int Length);

    /// <summary>
    /// Finds the common run to split on: the longest one, preferring runs built from lines that are
    /// rare on the left side.
    /// </summary>
    private static bool TryFindAnchor(int[] a, int aLow, int aHigh, int[] b, int bLow, int bHigh, out Anchor anchor)
    {
        var positions = new Dictionary<int, List<int>>(aHigh - aLow);
        for (var i = aLow; i < aHigh; i++)
        {
            if (!positions.TryGetValue(a[i], out var list)) positions[a[i]] = list = new List<int>(2);
            if (list.Count <= MaxOccurrences) list.Add(i);
        }
        var bestLength = 0;
        var bestOccurrences = int.MaxValue;
        anchor = default;
        for (var bIndex = bLow; bIndex < bHigh; bIndex++)
        {
            if (!positions.TryGetValue(b[bIndex], out var candidates) || candidates.Count > MaxOccurrences) continue;
            foreach (var aIndex in candidates)
            {
                var start = 0;
                while (aIndex - start - 1 >= aLow && bIndex - start - 1 >= bLow && a[aIndex - start - 1] == b[bIndex - start - 1]) start++;
                var end = 1;
                while (aIndex + end < aHigh && bIndex + end < bHigh && a[aIndex + end] == b[bIndex + end]) end++;
                var length = start + end;
                if (length < bestLength || (length == bestLength && candidates.Count >= bestOccurrences)) continue;
                bestLength = length;
                bestOccurrences = candidates.Count;
                anchor = new Anchor(aIndex - start, bIndex - start, length);
            }
        }
        return bestLength > 0;
    }
}
