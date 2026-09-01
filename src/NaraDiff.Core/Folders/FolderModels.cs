namespace NaraDiff.Core.Folders;

public enum FolderEntryStatus
{
    Same,
    Modified,
    LeftOnly,
    RightOnly,
    /// <summary>A file on one side and a directory on the other.</summary>
    TypeConflict,
    /// <summary>The entry could not be read; see the message.</summary>
    Error
}

/// <summary>How file equality is decided during a folder comparison.</summary>
public enum FolderContentMode
{
    /// <summary>Size and last write time, with a small tolerance. The fastest mode.</summary>
    SizeAndTime,
    SizeOnly,
    /// <summary>Byte for byte.</summary>
    BinaryContent,
    /// <summary>Text files are compared with the current diff options; other files byte for byte.</summary>
    TextAware
}

public sealed class FolderCompareOptions
{
    public bool Recursive { get; set; } = true;

    /// <summary>When false, names that differ only in case are treated as the same entry.</summary>
    public bool CaseSensitiveNames { get; set; }

    public bool IncludeHidden { get; set; }

    public bool IncludeSystem { get; set; }

    /// <summary>Glob patterns; matching entries are skipped completely.</summary>
    public List<string> ExcludePatterns { get; set; } = [];

    public FolderContentMode ContentMode { get; set; } = FolderContentMode.BinaryContent;

    public int TimeToleranceSeconds { get; set; } = 2;

    /// <summary>Files larger than this are compared by size and time even in content modes.</summary>
    public long MaxContentCompareBytes { get; set; } = 64L * 1024 * 1024;

    public FolderCompareOptions Clone() => new()
    {
        Recursive = Recursive,
        CaseSensitiveNames = CaseSensitiveNames,
        IncludeHidden = IncludeHidden,
        IncludeSystem = IncludeSystem,
        ExcludePatterns = [.. ExcludePatterns],
        ContentMode = ContentMode,
        TimeToleranceSeconds = TimeToleranceSeconds,
        MaxContentCompareBytes = MaxContentCompareBytes
    };

    public StringComparer NameComparer => CaseSensitiveNames ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}

/// <summary>One side of a compared entry.</summary>
public sealed class FolderSideInfo
{
    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public long Length { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    public bool IsReadOnly { get; init; }

    public bool IsHidden { get; init; }
}

/// <summary>A node of the folder comparison tree.</summary>
public sealed class FolderEntry
{
    public required string Name { get; init; }

    public required string RelativePath { get; init; }

    public required bool IsDirectory { get; init; }

    public FolderSideInfo? Left { get; set; }

    public FolderSideInfo? Right { get; set; }

    public FolderEntryStatus Status { get; set; } = FolderEntryStatus.Same;

    public string? Message { get; set; }

    public List<FolderEntry> Children { get; } = [];

    public long LeftLength => Left?.Length ?? 0;

    public long RightLength => Right?.Length ?? 0;

    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name);

    public bool HasDifference => Status != FolderEntryStatus.Same;

    public IEnumerable<FolderEntry> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var nested in child.Descendants()) yield return nested;
        }
    }
}

public sealed class FolderComparisonStatistics
{
    public int Same { get; init; }

    public int Modified { get; init; }

    public int LeftOnly { get; init; }

    public int RightOnly { get; init; }

    public int Errors { get; init; }

    public int Directories { get; init; }

    public int Files { get; init; }
}

public sealed class FolderComparisonResult
{
    public required string LeftPath { get; init; }

    public required string RightPath { get; init; }

    public required FolderEntry Root { get; init; }

    public required FolderCompareOptions Options { get; init; }

    public required FolderComparisonStatistics Statistics { get; init; }

    public bool AreIdentical => Statistics.Modified == 0 && Statistics.LeftOnly == 0 && Statistics.RightOnly == 0 && Statistics.Errors == 0;
}
