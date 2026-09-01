namespace NaraDiff.Core.Diff;

/// <summary>
/// One replaced region of an edit script: <paramref name="LeftCount"/> elements of the left
/// sequence starting at <paramref name="LeftStart"/> are replaced by <paramref name="RightCount"/>
/// elements of the right sequence starting at <paramref name="RightStart"/>.
/// </summary>
public readonly record struct SequenceChange(int LeftStart, int LeftCount, int RightStart, int RightCount)
{
    public int LeftEnd => LeftStart + LeftCount;

    public int RightEnd => RightStart + RightCount;

    public bool IsEmpty => LeftCount == 0 && RightCount == 0;
}

/// <summary>A line sequence comparison strategy.</summary>
public interface ISequenceDiffAlgorithm
{
    DiffAlgorithmKind Kind { get; }

    /// <summary>Compares two sequences of interned element identifiers.</summary>
    List<SequenceChange> Diff(int[] left, int[] right, CancellationToken cancellationToken);
}

public static class SequenceDiffAlgorithms
{
    public static ISequenceDiffAlgorithm Create(DiffAlgorithmKind kind) => kind switch
    {
        DiffAlgorithmKind.Myers => new MyersSequenceDiff(),
        DiffAlgorithmKind.Patience => new PatienceSequenceDiff(),
        _ => new HistogramSequenceDiff()
    };

    /// <summary>Sorts an edit script and merges touching or overlapping regions.</summary>
    public static List<SequenceChange> Normalize(List<SequenceChange> changes)
    {
        changes.RemoveAll(change => change.IsEmpty);
        if (changes.Count < 2) return changes;
        changes.Sort(static (first, second) => first.LeftStart != second.LeftStart
            ? first.LeftStart.CompareTo(second.LeftStart)
            : first.RightStart.CompareTo(second.RightStart));
        var merged = new List<SequenceChange>(changes.Count) { changes[0] };
        for (var i = 1; i < changes.Count; i++)
        {
            var previous = merged[^1];
            var current = changes[i];
            if (current.LeftStart <= previous.LeftEnd && current.RightStart <= previous.RightEnd)
            {
                var leftEnd = Math.Max(previous.LeftEnd, current.LeftEnd);
                var rightEnd = Math.Max(previous.RightEnd, current.RightEnd);
                merged[^1] = new SequenceChange(previous.LeftStart, leftEnd - previous.LeftStart, previous.RightStart, rightEnd - previous.RightStart);
            }
            else merged.Add(current);
        }
        return merged;
    }

    /// <summary>Verifies that an edit script really turns the left sequence into the right one.</summary>
    public static bool Validate(int[] left, int[] right, IReadOnlyList<SequenceChange> changes)
    {
        var result = new List<int>(right.Length);
        var leftIndex = 0;
        foreach (var change in changes)
        {
            if (change.LeftStart < leftIndex) return false;
            for (var i = leftIndex; i < change.LeftStart; i++) result.Add(left[i]);
            for (var i = 0; i < change.RightCount; i++)
            {
                var index = change.RightStart + i;
                if (index < 0 || index >= right.Length) return false;
                result.Add(right[index]);
            }
            leftIndex = change.LeftEnd;
            if (leftIndex > left.Length) return false;
        }
        for (var i = leftIndex; i < left.Length; i++) result.Add(left[i]);
        return result.Count == right.Length && result.SequenceEqual(right);
    }
}
