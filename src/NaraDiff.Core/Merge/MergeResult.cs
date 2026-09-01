using NaraDiff.Core.Diff;
using NaraDiff.Core.Text;

namespace NaraDiff.Core.Merge;

/// <summary>The result of a three way merge: the region list plus the two contributing diffs.</summary>
public sealed class MergeResult
{
    internal MergeResult(IReadOnlyList<TextLine> baseLines, IReadOnlyList<TextLine> leftLines, IReadOnlyList<TextLine> rightLines, List<MergeRegion> regions, DiffResult leftDiff, DiffResult rightDiff, DiffOptions options)
    {
        BaseLines = baseLines;
        LeftLines = leftLines;
        RightLines = rightLines;
        Regions = regions;
        LeftDiff = leftDiff;
        RightDiff = rightDiff;
        Options = options;
    }

    public IReadOnlyList<TextLine> BaseLines { get; }

    public IReadOnlyList<TextLine> LeftLines { get; }

    public IReadOnlyList<TextLine> RightLines { get; }

    public IReadOnlyList<MergeRegion> Regions { get; }

    /// <summary>Base compared with left; the left pane highlighting is taken from here.</summary>
    public DiffResult LeftDiff { get; }

    /// <summary>Base compared with right.</summary>
    public DiffResult RightDiff { get; }

    public DiffOptions Options { get; }

    public IEnumerable<MergeRegion> Conflicts => Regions.Where(region => region.IsConflict);

    public IEnumerable<MergeRegion> Changes => Regions.Where(region => region.Kind != MergeRegionKind.Unchanged);

    public int ConflictCount => Conflicts.Count();

    public int UnresolvedConflictCount => Conflicts.Count(region => !region.IsResolved);

    public int AutomaticMergeCount => Regions.Count(region => region.Kind is MergeRegionKind.LeftChange or MergeRegionKind.RightChange or MergeRegionKind.SameChange);

    /// <summary>Builds the merged text from the current resolutions.</summary>
    public MergedDocument Build(LineEndingKind fallbackEnding = LineEndingKind.Lf)
    {
        var lines = new List<TextLine>();
        var ranges = new Dictionary<int, (int Start, int Count)>(Regions.Count);
        foreach (var region in Regions)
        {
            var start = lines.Count;
            lines.AddRange(Resolve(region));
            ranges[region.Index] = (start, lines.Count - start);
        }
        for (var i = 0; i < lines.Count - 1; i++)
            if (lines[i].Ending == LineEndingKind.None) lines[i] = new TextLine(lines[i].Text, fallbackEnding);
        if (lines.Count == 0) lines.Add(new TextLine(string.Empty, LineEndingKind.None));
        return new MergedDocument { Lines = lines, RegionRanges = ranges };
    }

    /// <summary>The lines a single region contributes to the merged document.</summary>
    public IEnumerable<TextLine> Resolve(MergeRegion region)
    {
        var resolution = region.Resolution;
        if (resolution == MergeResolution.Custom && region.CustomLines is not null) return region.CustomLines;
        if (resolution == MergeResolution.Automatic)
            resolution = region.Kind switch
            {
                MergeRegionKind.LeftChange => MergeResolution.Left,
                MergeRegionKind.RightChange => MergeResolution.Right,
                MergeRegionKind.SameChange => MergeResolution.Left,
                MergeRegionKind.Unchanged => MergeResolution.Base,
                _ => MergeResolution.Base
            };
        return resolution switch
        {
            MergeResolution.Left => Slice(LeftLines, region.LeftStart, region.LeftCount),
            MergeResolution.Right => Slice(RightLines, region.RightStart, region.RightCount),
            MergeResolution.LeftThenRight => Slice(LeftLines, region.LeftStart, region.LeftCount).Concat(Slice(RightLines, region.RightStart, region.RightCount)),
            MergeResolution.RightThenLeft => Slice(RightLines, region.RightStart, region.RightCount).Concat(Slice(LeftLines, region.LeftStart, region.LeftCount)),
            _ => Slice(BaseLines, region.BaseStart, region.BaseCount)
        };
    }

    private static IEnumerable<TextLine> Slice(IReadOnlyList<TextLine> lines, int start, int count)
    {
        for (var i = start; i < start + count && i < lines.Count; i++) yield return lines[i];
    }
}
