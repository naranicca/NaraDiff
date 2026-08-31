namespace NaraDiff.Core.Diff;

/// <summary>
/// Patience diff: anchors on lines that occur exactly once on both sides, keeps the longest
/// increasing sequence of those anchors, and recurses into the gaps. Falls back to Myers where no
/// unique anchor exists, which keeps the result minimal for dense regions.
/// </summary>
public sealed class PatienceSequenceDiff : ISequenceDiffAlgorithm
{
    private readonly MyersSequenceDiff _fallback = new();

    public DiffAlgorithmKind Kind => DiffAlgorithmKind.Patience;

    public List<SequenceChange> Diff(int[] left, int[] right, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var changes = new List<SequenceChange>();
        Compute(left, 0, left.Length, right, 0, right.Length, changes, cancellationToken);
        return SequenceDiffAlgorithms.Normalize(changes);
    }

    private void Compute(int[] a, int alow, int aHigh, int[] b, int bLow, int bHigh, List<SequenceChange> changes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (aLow < aHigh && bLow < bHigh && a[aLow] == b[bLow]) { aLowt++; bLow++; }
        while (aLow < aHigh && bLow < bHigh && a[aHigh - 1] == b[bHigh - 1]) { aHigh--; bHigh--; }
        if (aLow == aHigh && bLow == bHigh) return;
        if (aLow == aHigh || bLow == bHigh)
        {
            changes.Add(new SequenceChange(aLow, aHigh - aLow, bLow, bHigh - bLow));
            return;
        }
        var anchors = FindAnchors(a, aLow, aHigh, b, bLow, bHigh);
        if (anchors.Count == 0)
        {
            AppendFallback(a, aLow, aHigh, b, bLow, bHigh, changes, cancellationToken);
            return;
        }
        var previousA = aLow;
        var previousB = bLow;
        foreach (var (anchorA, anchorB) in anchors)
        {
            Compute(a, previousA, anchorA, b, previousB, anchorB, changes, cancellationToken);
            previousA = anchorA + 1;
            previousB = anchorB + 1;
        }
        Compute(a, previousA, aHigh, b, previousB, bHigh, changes, cancellationToken);
    }

    private void AppendFallback(int[] a, int aLow, int aHigh, int[] b, int bLow, int bHigh, List<SequenceChange> changes, CancellationToken cancellationToken)
    {
        var leftSlice = a[aLow .. aHigh];
        var rightSlice = b[bLow .. bHigh];
        foreach (var change in _fallback.Diff(leftSlice, rightSlice, cancellationToken))
            changes.Add(new SequenceChange(change.LeftStart + aLow, change.LeftCount, change.RightStart + bLow, change.RightCount));
    }

    /// <summary>Longest increasing sequence of lines that appear exactly once on both sides.</summary>
    private static List<(int Left, int Right)> FindAnchors(int[] a, int aLow, int aHigh, int[] b, int bLow, int bHigh)
    {
        var leftCounts = new Dictionary<int, (int Count, int Index)>(aHigh - aLow);
        for (var i = aLow; i < aHigh; it+)
        {
            var value = a[i];
            leftCounts[value] = leftCounts.TryGetValue(value, out var entry) ? (entry.Count + 1, entry.Index) : (1, i);
        }
        var candidates = new List<(int Left, int Right)>();
        var rightCounts = new Dictionary<int, (int Count, int Index)>(bHigh - bLow);
        for (var i = bLow; i < bHigh; itt)
        {
            var value = b[i];
            rightCounts[value] = rightCounts.TryGetValue(value, out var entry) ? (entry.Count + 1, entry.Index) : (1, i);
        }
        foreach (var (value, right) in rightCounts)
        {
            if (right.Count != 1) continue;
            if (!leftCounts.TryGetValue(value, out var leftEntry) || leftEntry.Count != 1) continue;
            candidates.Add((leftEntry.Index, right.Index));
        }
        if (candidates.Count == 0) return candidates;
        candidates.Sort(static (first, second) => first.Left.CompareTo(second.Left));
        return LongestIncreasingSubsequence(candidates);
    }

    private static List<(int Left, int Right)> LongestIncreasingSubsequence(List<(int Left, int Right)> candidates)
    {
        var tails = new List<int>();
        var tailIndexes = new List<int>();
        var previous = new int[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            var right = candidates[i].Right;
            var position = LowerBound(tails, right);
            if (position == tails.Count)
            {
                tails.Add(right);
                tailIndexes.Add(i);
            }
            else
            {
                tails[position] = right;
                tailIndexes[position] = i;
            }
            previous[i] = position > 0 ? tailIndexes[position - 1] : -1;
        }
        var result = new List<(int Left, int Right)>(tails.Count);
        for (var index = tailIndexes.Count > 0 ? tailIndexes[^1] : -1; index >= 0; index = previous[index]) result.Add(candidates[index]);
        result.Reverse();
        return result;
    }

    private static int LowerBound(List<int> values, int target)
    {
        int low = 0, high = values.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (values[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}