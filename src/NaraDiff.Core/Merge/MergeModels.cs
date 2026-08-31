using NaraDiff.Core.Text;

namespace NaraDiff.Core.Merge;

public enum MergeRegionKind
{
    /// <summary>Identical in base, left and right.</summary>
    Unchanged,
    /// <summary>Changed on the left only; merges automatically.</summary>
    LeftChange,
    /// <summary>Changed on the right only; merges automatically.</summary>
    RightChange,
    /// <summary>Changed on both sides in exactly the same way; merges automatically.</summary>
    SameChange,
    /// <summary>Changed on both sides in different ways; needs a decision.</summary>
    Conflict
}

public enum MergeResolution
{
    /// <summary>Use the automatic result; conflicts stay unresolved.</summary>
    Automatic,
    Base,
    Left,
    Right,
    LeftThenRight,
    RightThenLeft,
    /// <summary>Use the lines the user typed into the result pane.</summary>
    Custom
}

/// <summary>One region of the three way merge, addressed by base, left and right line ranges.</summary>
public sealed class MergeRegion
{
    public MergeRegion(int index, MergeRegionKind kind, int baseStart, int baseCount, int leftStart, int leftCount, int rightStart, int rightCount)
    {
        Index = index;
        Kind = kind;
        BaseStart = baseStart;
        BaseCount = baseCount;
        LeftStart = leftStart;
        LeftCount = leftCount;
        RightStart = rightStart;
        RightCount = rightCount;
    }

    public int Index { get; }

    public MergeRegionKind Kind { get; }

    public int BaseStart { get; }

    public int BaseCount { get; }

    public int LeftStart { get; }

    public int LeftCount { get; }

    public int RightStart { get; }

    public int RightCount { get; }

    public int BaseEnd => BaseStart + BaseCount;

    public int LeftEnd => LeftStart + LeftCount;

    public int RightEnd => RightStart + RightCount;

    public bool IsConflict => Kind == MergeRegionKind.Conflict;

    public bool IsAutomatic => Kind is MergeRegionKind.Unchanged or MergeRegionKind.LeftChange or MergeRegionKind.RightChange or MergeRegionKind.SameChange;

    /// <summary>How this region is resolved; only conflicts start out unresolved.</summary>
    public MergeResolution Resolution { get; set; } = MergeResolution.Automatic;

    /// <summary>Lines typed by the user; used when <see cref="Resolution"/> is Custom.</summary>
    public List<TextLine>? CustomLines { get; set; }

    public bool IsResolved => !IsConflict || Resolution != MergeResolution.Automatic;

    public override string ToString() => $"{Kind} base[{BaseStart},{BaseCount}] left[{LeftStart},{LeftCount}] right[{RightStart},{RightCount}]";
}

/// <summary>The merged document plus the mapping from regions to merged line ranges.</summary>
public sealed class MergedDocument
{
    public required List<TextLine> Lines { get; init; }

    /// <summary>Merged line range of each region, indexed by region index.</summary>
    public required Dictionary<int, (int Start, int Count)> RegionRanges { get; init; }

    public string Text => LineEndings.Join(Lines);
}