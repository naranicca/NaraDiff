using NaraDiff.Core.Text;

namespace NaraDiff.Core.Diff;

/// <summary>
/// Compares two documents: applies the ignore options, runs the selected sequence algorithm on the
/// significant lines, maps the result back to real line numbers, detects moved blocks and refines
/// changed lines with inline word or character diffs.
/// </summary>
public static class DiffEngine
{
    /// <summary>Compares two line sequences. Safe to call on a background thread.</summary>
    public static DiffResult Compare(IReadOnlyList<TextLine> left, IReadOnlyList<TextLine> right, DiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var effective = (options ?? DiffOptions.Default).Sanitized();
        var keys = new LineKeyBuilder(effective);
        var interner = new Dictionary<string, int>(StringComparer.Ordinal);
        var (leftIds, leftMap) = Project(left, keys, interner, cancellationToken);
        var (rightIds, rightMap) = Project(right, keys, interner, cancellationToken);
        var algorithm = SequenceDiffAlgorithms.Create(effective.Algorithm);
        var changes = algorithm.Diff(leftIds, rightIds, cancellationToken);
        var blocks = BuildBlocks(changes, leftMap, rightMap, left.Count, right.Count);
        if (effective.DetectMoves) DetectMoves(blocks, left, right, keys, cancellationToken);
        if (effective. InlineMode != InlineDiffMode.None) RefineInline(blocks, left, right, effective, cancellationToken);
        return new DiffResult(left, right, blocks, effective, BuildStatistics(blocks));
    }

    public static Task<DiffResult> CompareAsync(IReadOnlyList<TextLine> left, IReadOnlyList<TextLine> right, DiffOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Compare(left, right, options, cancellationToken), cancellationToken);

    /// <summary>Maps every significant line to an interned identifier and remembers its real index.</summary>
    private static (int[] Ids, int[] Map) Project(IReadOnlyList<TextLine> lines, LineKeyBuilder keys, Dictionary<string, int> interner, CancellationToken cancellationToken)
    {
        var ids = new List<int>(lines.Count);
        var map = new List<int>(lines.Count);
        for (var i = 0; i < lines.Count; i++)

        if ((i & ØxFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
        var line = lines[i];
        if (keys.HasIgnoreRules && keys.IsIgnored(line)) continue;
        var key = keys.Buildey(line);
        if (!interner.TryGetValue(key, out var id))

        id = interner.Count + 1;
        interner[key] = id;

        ids.Add(id);
        map.Add(i);

        return ([ .. ids], [ .. map]);
    }

    /// <summary>Translates significant line indexes back to document line indexes.</summary>
    private static List<DiffBlock> BuildBlocks(List<SequenceChange> changes, int[] leftMap, int[] rightMap, int leftCount, int rightCount)
    {
        var blocks = new List<DiffBlock>(changes.Count);
        foreach (var change in changes)
        {
            var leftStart = Resolve(leftMap, change.LeftStart, leftCount);
            var leftEnd = change.LeftCount > 0 ? leftMap[change.LeftStart + change.LeftCount - 1] + 1 : leftStart;
            var rightStart = Resolve(rightMap, change.RightStart, rightCount);
            var rightEnd = change.RightCount > 0 ? rightMap[change.RightStart + change.RightCount - 1] + 1 : rightStart;
            var leftLength = leftEnd - leftStart;
            var rightLength = rightEnd - rightStart;
            var kind = leftLength == 0 ? DiffBlockKind.Insert : rightLength == 0 ? DiffBlockKind.Delete : DiffBlockKind.Modify;
            blocks.Add(new DiffBlock(blocks.Count, kind, leftStart, leftLength, rightStart, rightLength));
        }
        return blocks;
    }

    private static int Resolve(int[] map, int index, int documentLineCount) => index < map.Length ? map[index] : documentLineCount;

    /// <summary>
    /// Pairs deletions with insertions that carry the same content, so that relocated code is shown
    /// as a move instead of an unrelated delete plus insert.
    /// </summary>
    private static void DetectMoves(List<DiffBlock> blocks, IReadOnlyList<TextLine> left, IReadOnlyList<TextLine> right, LineKeyBuilder keys, CancellationToken cancellationToken)
    {
        var deletions = new Dictionary<string, List<DiffBlock>>(StringComparer.Ordinal);
        foreach (var block in blocks.Where(block => block.Kind == DiffBlockKind.Delete))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = Signature(left, block.LeftStart, block.LeftCount, keys);
            if (signature is null) continue;
            if (!deletions.TryGetValue(signature, out var list)) deletions[signature] = list = [];
            list.Add(block);
        }
        if (deletions.Count == 0) return;
        foreach (var block in blocks.Where(block => block.Kind == DiffBlockKind.Insert))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = Signature(right, block.RightStart, block.RightCount, keys);
            if (signature is null || !deletions.TryGetValue(signature, out var list) || list.Count == 0) continue;
            var partner = list[0];
            list.RemoveAt(0);
            block.IsMoved = partner.IsMoved = true;
            block.MovePartner = partner.Index;
            partner.MovePartner = block.Index;
        }
    }

    /// <summary>Content signature of a block; null when the block is too small to be a useful move.</summary>
    private static string? Signature(IReadOnlyList<TextLine> lines, int start, int count, LineKeyBuilder keys)
    {
        if (count == 0) return null;
        var builder = new System.Text.StringBuilder();
        var significant = 0;
        for (var i = start; i < start + count && i < lines.Count; i++)
        {
            var key = keys.BuildKey(lines[i]);
            if (key.Trim().Length > 0) significant++;
            builder.Append(key).Append('\n');
        }
        return significant == 0 ? null : builder.ToString();
    }

    private static void RefineInline(List<DiffBlock> blocks, IReadOnlyList<TextLine> left, IReadOnlyList<TextLine> right, DiffOptions options, CancellationToken cancellationToken)
    {
        foreach (var block in blocks)
        {
            if (block.Kind != DiffBlockKind.Modify) continue;
            if (Math.Max(block.LeftCount, block.RightCount) > options.InlineBlockLineLimit) continue;
            var pairs = Math.Min(block.LeftCount, block.RightCount);
            for (var i = 0; i < pairs; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leftLine = block.LeftStart + i;
                var rightLine = block.RightStart + i;
                var (leftSpans, rightSpans) = InlineDiff.Compute(left[leftLine].Text, right[rightLine].Text, options.InlineMode, options, cancellationToken);
                if (leftSpans.Count > 0) block.LeftInline[leftLine] = leftSpans;
                if (rightSpans.Count > 0) block.RightInline[rightLine] = rightSpans;
            }
        }
    }

    private static DiffStatistics BuildStatistics(List<DiffBlock> blocks) => new()
    {
        BlockCount = blocks.Count,
        Inserted = blocks.Count(block => block.Kind == DiffBlockKind.Insert),
        Deleted = blocks.Count(block => block.Kind == DiffBlockKind.Delete),
        Modified = blocks.Count(block => block.Kind == DiffBlockKind.Modify),
        Moved = blocks.Count(block => block. IsMoved),
        ChangedLeftLines = blocks.Sum(block => block.LeftCount),
        ChangedRightLines = blocks.Sum(block => block.RightCount)
    };
}