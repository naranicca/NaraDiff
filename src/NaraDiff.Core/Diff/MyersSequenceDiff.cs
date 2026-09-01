namespace NaraDiff.Core.Diff;

/// <summary>
/// Myers' O(ND) difference algorithm with the linear space refinement: every recursion level finds
/// the middle snake of the remaining region and splits the problem in two.
/// </summary>
public sealed class MyersSequenceDiff : ISequenceDiffAlgorithm
{
    private readonly struct Snake
    {
        public Snake(int leftStart, int rightStart, int leftEnd, int rightEnd)
        {
            LeftStart = leftStart;
            RightStart = rightStart;
            LeftEnd = leftEnd;
            RightEnd = rightEnd;
        }

        public int LeftStart { get; }

        public int RightStart { get; }

        public int LeftEnd { get; }

        public int RightEnd { get; }
    }

    public DiffAlgorithmKind Kind => DiffAlgorithmKind.Myers;

    public List<SequenceChange> Diff(int[] left, int[] right, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var changes = new List<SequenceChange>();
        var size = 2 * (left.Length + right.Length) + 4;
        var forward = new int[size];
        var reverse = new int[size];
        Compute(left, 0, left.Length, right, 0, right.Length, forward, reverse, changes, cancellationToken);
        return SequenceDiffAlgorithms.Normalize(changes);
    }

    private static void Compute(int[] a, int aLow, int aHigh, int[] b, int bLow, int bHigh, int[] forward, int[] reverse, List<SequenceChange> changes, CancellationToken cancellationToken)
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
        var snake = FindMiddleSnake(a, aLow, aHigh, b, bLow, bHigh, forward, reverse, cancellationToken);
        var splitsProgress = snake.LeftStart > aLow || snake.RightStart > bLow || snake.LeftEnd < aHigh || snake.RightEnd < bHigh;
        if (!splitsProgress)
        {
            changes.Add(new SequenceChange(aLow, aHigh - aLow, bLow, bHigh - bLow));
            return;
        }
        Compute(a, aLow, snake.LeftStart, b, bLow, snake.RightStart, forward, reverse, changes, cancellationToken);
        Compute(a, snake.LeftEnd, aHigh, b, snake.RightEnd, bHigh, forward, reverse, changes, cancellationToken);
    }

    /// <summary>
    /// Runs a forward search from the top left and a reverse search from the bottom right until the
    /// two searches overlap; the snake that closed the gap splits the region.
    /// </summary>
    private static Snake FindMiddleSnake(int[] a, int aLow, int aHigh, int[] b, int bLow, int bHigh, int[] forward, int[] reverse, CancellationToken cancellationToken)
    {
        var n = aHigh - aLow;
        var m = bHigh - bLow;
        var delta = n - m;
        var oddDelta = (delta & 1) != 0;
        var max = (n + m + 1) / 2 + 1;
        var offset = max + 1;
        Array.Clear(forward, 0, Math.Min(forward.Length, 2 * max + 3));
        Array.Clear(reverse, 0, Math.Min(reverse.Length, 2 * max + 3));
        forward[offset + 1] = 0;
        reverse[offset + 1] = 0;
        for (var d = 0; d <= max; d++)
        {
            if ((d & 0x3F) == 0) cancellationToken.ThrowIfCancellationRequested();
            var low = -d + 2 * Math.Max(0, d - m);
            var high = d - 2 * Math.Max(0, d - n);
            for (var k = low; k <= high; k += 2)
            {
                int x;
                if (k == -d || (k != d && forward[offset + k - 1] < forward[offset + k + 1])) x = forward[offset + k + 1];
                else x = forward[offset + k - 1] + 1;
                var y = x - k;
                var startX = x;
                var startY = y;
                while (x < n && y < m && a[aLow + x] == b[bLow + y]) { x++; y++; }
                forward[offset + k] = x;
                if (!oddDelta) continue;
                var c = delta - k;
                if (c < -(d - 1) || c > d - 1) continue;
                if (x + reverse[offset + c] < n) continue;
                return new Snake(aLow + startX, bLow + startY, aLow + x, bLow + y);
            }
            for (var c = low; c <= high; c += 2)
            {
                int u;
                if (c == -d || (c != d && reverse[offset + c - 1] < reverse[offset + c + 1])) u = reverse[offset + c + 1];
                else u = reverse[offset + c - 1] + 1;
                var v = u - c;
                var startU = u;
                var startV = v;
                while (u < n && v < m && a[aHigh - 1 - u] == b[bHigh - 1 - v]) { u++; v++; }
                reverse[offset + c] = u;
                if (oddDelta) continue;
                var k = delta - c;
                if (k < -d || k > d) continue;
                if (forward[offset + k] + u < n) continue;
                return new Snake(aLow + n - u, bLow + m - v, aLow + n - startU, bLow + m - startV);
            }
        }
        return new Snake(aLow, bLow, aHigh, bHigh);
    }
}
