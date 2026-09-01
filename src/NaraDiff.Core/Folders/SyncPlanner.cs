namespace NaraDiff.Core.Folders;

public enum SyncDirection
{
    LeftToRight,
    RightToLeft,
    /// <summary>Copy in both directions; the newer file wins, nothing is deleted.</summary>
    Bidirectional
}

public enum SyncActionKind
{
    CopyLeftToRight,
    CopyRightToLeft,
    CreateDirectoryRight,
    CreateDirectoryLeft,
    DeleteRight,
    DeleteLeft
}

/// <summary>One planned file system operation. Nothing is executed until the user confirms.</summary>
public sealed class SyncAction
{
    public required SyncActionKind Kind { get; init; }

    public required string RelativePath { get; init; }

    public string? SourcePath { get; init; }

    public required string TargetPath { get; init; }

    public long Bytes { get; init; }

    /// <summary>True when an existing file is replaced.</summary>
    public bool Overwrites { get; init; }

    public bool IsDelete => Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight;

    public bool IsDirectory { get; init; }

    public required string Reason { get; init; }

    public string DirectionText => Kind switch
    {
        SyncActionKind.CopyLeftToRight => "Left to right",
        SyncActionKind.CopyRightToLeft => "Right to left",
        SyncActionKind.CreateDirectoryRight => "Create on right",
        SyncActionKind.CreateDirectoryLeft => "Create on left",
        SyncActionKind.DeleteRight => "Delete on right",
        _ => "Delete on left"
    };
}

public sealed class SyncOptions
{
    public SyncDirection Direction { get; set; } = SyncDirection.LeftToRight;

    /// <summary>Replace files that exist on both sides but differ.</summary>
    public bool OverwriteDifferent { get; set; } = true;

    /// <summary>Copy files that exist only on the source side.</summary>
    public bool CopyMissing { get; set; } = true;

    /// <summary>Delete files that exist only on the target side. Off by default.</summary>
    public bool DeleteOrphans { get; set; }
}

/// <summary>The dry run of a folder synchronisation.</summary>
public sealed class SyncPlan
{
    public required string LeftPath { get; init; }

    public required string RightPath { get; init; }

    public required SyncOptions Options { get; init; }

    public required List<SyncAction> Actions { get; init; }

    public int CopyCount => Actions.Count(action => action.Kind is SyncActionKind.CopyLeftToRight or SyncActionKind.CopyRightToLeft);

    public int DeleteCount => Actions.Count(action => action.IsDelete);

    public int OverwriteCount => Actions.Count(action => action.Overwrites);

    public int DirectoryCount => Actions.Count(action => action.Kind is SyncActionKind.CreateDirectoryLeft or SyncActionKind.CreateDirectoryRight);

    public long TotalBytes => Actions.Sum(action => action.Bytes);

    public bool IsEmpty => Actions.Count == 0;

    /// <summary>True when the plan removes or replaces existing content and needs explicit confirmation.</summary>
    public bool NeedsExplicitConfirmation => DeleteCount > 0 || OverwriteCount > 0;
}

/// <summary>
/// Builds the list of copy and delete operations for a folder comparison. The planner never touches
/// the file system; it only describes what would happen.
/// </summary>
public static class SyncPlanner
{
    public static SyncPlan Create(FolderComparisonResult comparison, SyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(options);
        var actions = new List<SyncAction>();
        Walk(comparison.Root, comparison, options, actions);
        actions.Sort(static (first, second) => string.Compare(first.RelativePath, second.RelativePath, StringComparison.OrdinalIgnoreCase));
        return new SyncPlan { LeftPath = comparison.LeftPath, RightPath = comparison.RightPath, Options = options, Actions = actions };
    }

    private static void Walk(FolderEntry entry, FolderComparisonResult comparison, SyncOptions options, List<SyncAction> actions)
    {
        foreach (var child in entry.Children)
        {
            Plan(child, comparison, options, actions);
            if (child.IsDirectory) Walk(child, comparison, options, actions);
        }
    }

    private static void Plan(FolderEntry entry, FolderComparisonResult comparison, SyncOptions options, List<SyncAction> actions)
    {
        var leftPath = Path.Combine(comparison.LeftPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var rightPath = Path.Combine(comparison.RightPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var toRight = options.Direction is SyncDirection.LeftToRight or SyncDirection.Bidirectional;
        var toLeft = options.Direction is SyncDirection.RightToLeft or SyncDirection.Bidirectional;
        switch (entry.Status)
        {
            case FolderEntryStatus.LeftOnly when toRight && options.CopyMissing:
                actions.Add(entry.IsDirectory
                    ? new SyncAction { Kind = SyncActionKind.CreateDirectoryRight, RelativePath = entry.RelativePath, TargetPath = rightPath, IsDirectory = true, Reason = "Missing on the right" }
                    : new SyncAction { Kind = SyncActionKind.CopyLeftToRight, RelativePath = entry.RelativePath, SourcePath = leftPath, TargetPath = rightPath, Bytes = entry.LeftLength, Reason = "Missing on the right" });
                break;
            case FolderEntryStatus.LeftOnly when !toRight && toLeft && options.DeleteOrphans:
                actions.Add(new SyncAction { Kind = SyncActionKind.DeleteLeft, RelativePath = entry.RelativePath, TargetPath = leftPath, Bytes = entry.LeftLength, IsDirectory = entry.IsDirectory, Overwrites = true, Reason = "Not present on the right" });
                break;
            case FolderEntryStatus.RightOnly when toLeft && options.CopyMissing:
                actions.Add(entry.IsDirectory
                    ? new SyncAction { Kind = SyncActionKind.CreateDirectoryLeft, RelativePath = entry.RelativePath, TargetPath = leftPath, IsDirectory = true, Reason = "Missing on the left" }
                    : new SyncAction { Kind = SyncActionKind.CopyRightToLeft, RelativePath = entry.RelativePath, SourcePath = rightPath, TargetPath = leftPath, Bytes = entry.RightLength, Reason = "Missing on the left" });
                break;
            case FolderEntryStatus.RightOnly when !toLeft && toRight && options.DeleteOrphans:
                actions.Add(new SyncAction { Kind = SyncActionKind.DeleteRight, RelativePath = entry.RelativePath, TargetPath = rightPath, Bytes = entry.RightLength, IsDirectory = entry.IsDirectory, Overwrites = true, Reason = "Not present on the left" });
                break;
            case FolderEntryStatus.Modified when !entry.IsDirectory && options.OverwriteDifferent:
                var newerIsLeft = (entry.Left?.LastWriteTimeUtc ?? DateTime.MinValue) >= (entry.Right?.LastWriteTimeUtc ?? DateTime.MinValue);
                if (options.Direction == SyncDirection.Bidirectional)
                    actions.Add(newerIsLeft
                        ? new SyncAction { Kind = SyncActionKind.CopyLeftToRight, RelativePath = entry.RelativePath, SourcePath = leftPath, TargetPath = rightPath, Bytes = entry.LeftLength, Overwrites = true, Reason = "Left file is newer" }
                        : new SyncAction { Kind = SyncActionKind.CopyRightToLeft, RelativePath = entry.RelativePath, SourcePath = rightPath, TargetPath = leftPath, Bytes = entry.RightLength, Overwrites = true, Reason = "Right file is newer" });
                else if (toRight)
                    actions.Add(new SyncAction { Kind = SyncActionKind.CopyLeftToRight, RelativePath = entry.RelativePath, SourcePath = leftPath, TargetPath = rightPath, Bytes = entry.LeftLength, Overwrites = true, Reason = "Different content" });
                else
                    actions.Add(new SyncAction { Kind = SyncActionKind.CopyRightToLeft, RelativePath = entry.RelativePath, SourcePath = rightPath, TargetPath = leftPath, Bytes = entry.RightLength, Overwrites = true, Reason = "Different content" });
                    break;
        }
    }
}
