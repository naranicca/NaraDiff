using NaraDiff.Core.Diff;
using NaraDiff.Core.Text;

namespace NaraDiff.Core.Folders;

/// <summary>
/// Recursive folder comparison. Runs off the UI thread, honours a cancellation token, reports
/// progress per directory, and turns unreadable entries into error rows instead of exceptions.
/// </summary>
public sealed class DirectoryComparer
{
    private readonly FolderCompareOptions _options;
    private readonly DiffOptions _diffOptions;
    private readonly GlobMatcher _excludes;
    private int _same;
    private int _modified;
    private int _leftOnly;
    private int _rightOnly;
    private int _errors;
    private int _directories;
    private int _files;

    public DirectoryComparer(FolderCompareOptions? options = null, DiffOptions? diffOptions = null)
    {
        _options = (options ?? new FolderCompareOptions()).Clone();
        _diffOptions = (diffOptions ?? DiffOptions.Default).Sanitized();
        _excludes = new GlobMatcher(_options.ExcludePatterns, _options.CaseSensitiveNames);
    }

    public static Task<FolderComparisonResult> CompareAsync(string leftPath, string rightPath, FolderCompareOptions? options = null, DiffOptions? diffOptions = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => new DirectoryComparer(options, diffOptions).Compare(leftPath, rightPath, progress, cancellationToken), cancellationToken);

    public FolderComparisonResult Compare(string leftPath, string rightPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightPath);
        var left = Path.GetFullPath(leftPath);
        var right = Path.GetFullPath(rightPath);
        var root = new FolderEntry
        {
            Name = Path.GetFileName(left.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : left,
            RelativePath = string.Empty,
            IsDirectory = true,
            Left = TryDescribe(left, true),
            Right = TryDescribe(right, true)
        };
        CompareDirectory(root, left, right, string.Empty, progress, cancellationToken);
        root.Status = RollUp(root);
        return new FolderComparisonResult
        {
            LeftPath = left,
            RightPath = right,
            Root = root,
            Options = _options,
            Statistics = new FolderComparisonStatistics
            {
                Same = _same,
                Modified = _modified,
                LeftOnly = _leftOnly,
                RightOnly = _rightOnly,
                Errors = _errors,
                Directories = _directories,
                Files = _files
            }
        };
    }

    private void CompareDirectory(FolderEntry parent, string leftDirectory, string rightDirectory, string relativePath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(relativePath.Length == 0 ? "." : relativePath);
        var leftEntries = Enumerate(leftDirectory, parent, "left");
        var rightEntries = Enumerate(rightDirectory, parent, "right");
        var names = new SortedSet<string>(_options.NameComparer);
        foreach (var name in leftEntries.Keys) names.Add(name);
        foreach (var name in rightEntries.Keys) names.Add(name);
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childRelative = relativePath.Length == 0 ? name : relativePath + "/" + name;
            leftEntries.TryGetValue(name, out var leftInfo);
            rightEntries.TryGetValue(name, out var rightInfo);
            if (_excludes.IsExcluded(name, childRelative)) continue;
            if (!_options.IncludeHidden && ((leftInfo?.IsHidden ?? false) || (rightInfo?.IsHidden ?? false))) continue;
            var isDirectory = (leftInfo?.IsDirectory ?? false) || (rightInfo?.IsDirectory ?? false);
            var entry = new FolderEntry
            {
                Name = name,
                RelativePath = childRelative,
                IsDirectory = isDirectory,
                Left = leftInfo,
                Right = rightInfo
            };
            parent.Children.Add(entry);
            if (leftInfo is not null && rightInfo is not null && leftInfo.IsDirectory != rightInfo.IsDirectory)
            {
                entry.Status = FolderEntryStatus.TypeConflict;
                entry.Message = "One side is a file and the other side is a directory.";
                _errors++;
                continue;
            }
            if (isDirectory)
            {
                _directories++;
                if (leftInfo is null) { entry.Status = FolderEntryStatus.RightOnly; _rightOnly++; }
                else if (rightInfo is null) { entry.Status = FolderEntryStatus.LeftOnly; _leftOnly++; }
                if (_options.Recursive && leftInfo is not null && rightInfo is not null)
                {
                    CompareDirectory(entry, leftInfo.FullPath, rightInfo.FullPath, childRelative, progress, cancellationToken);
                    entry.Status = RollUp(entry);
                }
                else if (_options.Recursive)
                {
                    var single = leftInfo ?? rightInfo!;
                    CollectSingleSide(entry, single.FullPath, childRelative, leftInfo is not null, cancellationToken);
                }
                continue;
            }
            _files++;
            if (leftInfo is null) { entry.Status = FolderEntryStatus.RightOnly; _rightOnly++; continue; }
            if (rightInfo is null) { entry.Status = FolderEntryStatus.LeftOnly; _leftOnly++; continue; }
            entry.Status = CompareFiles(entry, leftInfo, rightInfo, cancellationToken);
            switch (entry.Status)
            {
                case FolderEntryStatus.Same: _same++; break;
                case FolderEntryStatus.Modified: _modified++; break;
                case FolderEntryStatus.Error: _errors++; break;
            }
        }
    }

    /// <summary>Adds the contents of a directory that exists on one side only.</summary>
    private void CollectSingleSide(FolderEntry parent, string directory, string relativePath, bool isLeft, CancellationToken cancellationToken)
    {
        var entries = Enumerate(directory, parent, isLeft ? "left" : "right");
        foreach (var (name, info) in entries.OrderBy(pair => pair.Key, _options.NameComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childRelative = relativePath.Length == 0 ? name : relativePath + "/" + name;
            if (_excludes.IsExcluded(name, childRelative)) continue;
            if (!_options. IncludeHidden && info.IsHidden) continue;
            var entry = new FolderEntry
            {
                Name = name,
                RelativePath = childRelative,
                IsDirectory = info.IsDirectory,
                Left = isLeft ? info : null,
                Right = isLeft ? null : info,
                Status = isLeft ? FolderEntryStatus.LeftOnly : FolderEntryStatus.RightOnly
            };
            parent.Children.Add(entry);
            if (info.IsDirectory)
            {
                _directories++;
                if (isLeft) _leftOnly++; else _rightOnly++;
                if (_options.Recursive) CollectSingleSide(entry, info.FullPath, childRelative, isLeft, cancellationToken);
            }
            else
            {
                _files++;
                if (isLeft) _leftOnly++; else _rightOnly++;
            }
        }
    }

    private Dictionary<string, FolderSideInfo> Enumerate(string directory, FolderEntry owner, string side)
    {
        var result = new Dictionary<string, FolderSideInfo>(_options.NameComparer);
        if (!Directory.Exists(directory)) return result;
        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var attributes = info.Attributes;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (!_options.IncludeSystem && (attributes & FileAttributes.System) != 0) continue;
                var file = info as FileInfo;
                result[info.Name] = new FolderSideInfo
                {
                    FullPath = info.FullName,
                    IsDirectory = isDirectory,
                    Length = file?.Length ?? 0,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                    IsReadOnly = file?.IsReadOnly ?? false,
                    IsHidden = (attributes & FileAttributes.Hidden) != 0
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            owner.Message = $"The {side} folder could not be read: {ex.Message}";
            owner.Status = FolderEntryStatus.Error;
            _errors++;
        }
        return result;
    }

    private static FolderSideInfo? TryDescribe(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                var directory = new DirectoryInfo(path);
                if (!directory.Exists) return null;
                return new FolderSideInfo
                {
                    FullPath = directory.FullName,
                    IsDirectory = true,
                    LastWriteTimeUtc = directory.LastWriteTimeUtc,
                    IsHidden = (directory.Attributes & FileAttributes.Hidden) != 0
                };
            }
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            return new FolderSideInfo
            {
                FullPath = file.FullName,
                IsDirectory = false,
                Length = file.Length,
                LastWriteTimeUtc = file.LastWriteTimeUtc,
                IsReadOnly = file.IsReadOnly,
                IsHidden = (file.Attributes & FileAttributes.Hidden) != 0
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private FolderEntryStatus CompareFiles(FolderEntry entry, FolderSideInfo left, FolderSideInfo right, CancellationToken cancellationToken)
    {
        try
        {
            var withinLimit = left.Length <= _options.MaxContentCompareBytes && right.Length <= _options.MaxContentCompareBytes;
            switch (_options.ContentMode)
            {
                case FolderContentMode.SizeOnly:
                    return left.Length == right.Length ? FolderEntryStatus.Same : FolderEntryStatus.Modified;
                case FolderContentMode.BinaryContent when withinLimit:
                    return SameBytes(left.FullPath, right.FullPath, cancellationToken) ? FolderEntryStatus.Same : FolderEntryStatus.Modified;
                case FolderContentMode.TextAware when withinLimit:
                    return SameTextAware(left.FullPath, right.FullPath, cancellationToken) ? FolderEntryStatus.Same : FolderEntryStatus.Modified;
                default:
                    if (left.Length != right.Length) return FolderEntryStatus.Modified;
                    return Math.Abs((left.LastWriteTimeUtc - right.LastWriteTimeUtc).TotalSeconds) <= _options.TimeToleranceSeconds
                        ? FolderEntryStatus.Same
                        : FolderEntryStatus.Modified;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entry.Message = ex.Message;
            return FolderEntryStatus.Error;
        }
    }

    private static bool SameBytes(string leftPath, string rightPath, CancellationToken cancellationToken)
    {
        using var leftStream = File.OpenRead(leftPath);
        using var rightStream = File.OpenRead(rightPath);
        if (leftStream.Length != rightStream.Length) return false;
        var leftBuffer = new byte[64 * 1024];
        var rightBuffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
        }
    }

    /// <summary>Compares text files with the current diff options, and other files byte for byte.</summary>
    private bool SameTextAware(string leftPath, string rightPath, CancellationToken cancellationToken)
    {
        var leftContent = TextFileContent.FromBytes(File.ReadAllBytes(leftPath));
        var rightContent = TextFileContent.FromBytes(File.ReadAllBytes(rightPath));
        if (leftContent.IsBinary || rightContent.IsBinary) return leftContent.Bytes.AsSpan().SequenceEqual(rightContent.Bytes);
        return DiffEngine.Compare(leftContent.Lines, rightContent.Lines, _diffOptions, cancellationToken).AreIdentical;
    }

    private static FolderEntryStatus RollUp(FolderEntry entry)
    {
        if (entry.Status is FolderEntryStatus.Error or FolderEntryStatus.TypeConflict or FolderEntryStatus.LeftOnly or FolderEntryStatus.RightOnly) return entry.Status;
        if (entry.Children.Count == 0) return entry.Status;
        if (entry.Children.Any(child => child.Status is FolderEntryStatus.Error or FolderEntryStatus.TypeConflict)) return FolderEntryStatus.Error;
        return entry.Children.Any(child => child.Status != FolderEntryStatus.Same) ? FolderEntryStatus.Modified : FolderEntryStatus.Same;
    }
}
