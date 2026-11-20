using System.IO;
using System.IO.Compression;
using System.Text;
using NaraDiff.Core.Folders;

namespace NaraDiff.App.Services;

/// <summary>Builds a folder-comparison tree for the changes between a revision and a Git working tree.</summary>
internal sealed class GitWorktreeComparer : IDisposable
{
    private readonly string _snapshotDirectory;
    private bool _disposed;

    private GitWorktreeComparer(GitRepository repository, string revision, string snapshotDirectory, FolderComparisonResult result)
    {
        Repository = repository;
        Revision = revision;
        _snapshotDirectory = snapshotDirectory;
        Result = result;
    }

    public GitRepository Repository { get; }

    /// <summary>The revision on the left side of the comparison.</summary>
    public string Revision { get; }

    public FolderComparisonResult Result { get; }

    /// <summary>
    /// Extracts <paramref name="revision"/> into a temporary folder and lists every path whose
    /// working-tree copy differs from it, including untracked files.
    /// </summary>
    public static async Task<GitWorktreeComparer> CreateAsync(GitRepository repository, string revision, CancellationToken cancellationToken)
    {
        var root = repository.Root;
        var snapshot = Path.Combine(Path.GetTempPath(), "NaraDiff", "git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshot);
        try
        {
            List<(string Path, FolderEntryStatus Status)> changes;
            if (await HasCommitAsync(repository, revision, cancellationToken))
            {
                var archive = Path.Combine(snapshot, "snapshot.zip");
                await repository.RunAsync(["archive", "--format=zip", "--output=" + archive, revision], cancellationToken);
                ZipFile.ExtractToDirectory(archive, snapshot, true);
                File.Delete(archive);
                // Refresh the cached file stats so files that were only touched are not reported as changed.
                try { await repository.RunAsync(["update-index", "-q", "--refresh"], cancellationToken); } catch (GitException) { }
                changes = ParseNameStatus(await repository.RunBytesAsync(["diff", "--no-renames", "--name-status", "-z", revision], cancellationToken));
            }
            else
            {
                // A repository without commits: everything that is tracked is new.
                changes = ParseList(await repository.RunBytesAsync(["ls-files", "--cached", "-z"], cancellationToken))
                    .Select(path => (path, FolderEntryStatus.RightOnly)).ToList();
            }
            changes.AddRange(ParseList(await repository.RunBytesAsync(["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken))
                .Select(path => (path, FolderEntryStatus.RightOnly)));

            var rootEntry = new FolderEntry { Name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), RelativePath = string.Empty, IsDirectory = true };
            foreach (var change in changes.DistinctBy(change => change.Path)) AddEntry(rootEntry, change, snapshot, root);
            RollUp(rootEntry);
            var all = rootEntry.Descendants().ToList();
            var statistics = new FolderComparisonStatistics
            {
                Files = all.Count(entry => !entry.IsDirectory),
                Directories = all.Count(entry => entry.IsDirectory),
                Same = 0,
                Modified = all.Count(entry => !entry.IsDirectory && entry.Status == FolderEntryStatus.Modified),
                LeftOnly = all.Count(entry => !entry.IsDirectory && entry.Status == FolderEntryStatus.LeftOnly),
                RightOnly = all.Count(entry => !entry.IsDirectory && entry.Status == FolderEntryStatus.RightOnly),
                Errors = all.Count(entry => entry.Status == FolderEntryStatus.Error)
            };
            return new GitWorktreeComparer(repository, revision, snapshot, new FolderComparisonResult
            {
                LeftPath = snapshot,
                RightPath = root,
                Root = rootEntry,
                Options = new FolderCompareOptions { ContentMode = FolderContentMode.TextAware },
                Statistics = statistics
            });
        }
        catch
        {
            try { Directory.Delete(snapshot, true); } catch { }
            throw;
        }
    }

    private static async Task<bool> HasCommitAsync(GitRepository repository, string revision, CancellationToken cancellationToken)
    {
        try
        {
            await repository.RunAsync(["rev-parse", "--verify", "--quiet", revision + "^{commit}"], cancellationToken);
            return true;
        }
        catch (GitException) when (revision == "HEAD")
        {
            return false;
        }
    }

    /// <summary>Parses <c>git diff --name-status -z</c>: a status letter and a path per change.</summary>
    private static List<(string Path, FolderEntryStatus Status)> ParseNameStatus(byte[] bytes)
    {
        var fields = Encoding.UTF8.GetString(bytes).Split('\0');
        var result = new List<(string, FolderEntryStatus)>();
        for (var index = 0; index + 1 < fields.Length; index += 2)
        {
            var code = fields[index];
            var path = fields[index + 1];
            if (code.Length == 0 || path.Length == 0) continue;
            var status = code[0] switch
            {
                'A' => FolderEntryStatus.RightOnly,
                'D' => FolderEntryStatus.LeftOnly,
                _ => FolderEntryStatus.Modified
            };
            result.Add((path.Replace('/', Path.DirectorySeparatorChar), status));
        }
        return result;
    }

    private static IEnumerable<string> ParseList(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Replace('/', Path.DirectorySeparatorChar));

    private static void AddEntry(FolderEntry root, (string Path, FolderEntryStatus Status) change, string snapshot, string worktree)
    {
        var parts = change.Path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var parent = root;
        var relative = string.Empty;
        for (var index = 0; index < parts.Length; index++)
        {
            relative = relative.Length == 0 ? parts[index] : Path.Combine(relative, parts[index]);
            var last = index == parts.Length - 1;
            var child = parent.Children.FirstOrDefault(entry => entry.Name == parts[index]);
            if (child is null)
            {
                child = new FolderEntry { Name = parts[index], RelativePath = relative.Replace(Path.DirectorySeparatorChar, '/'), IsDirectory = !last };
                parent.Children.Add(child);
            }
            if (last)
            {
                child.Status = change.Status;
                child.Left = Describe(Path.Combine(snapshot, relative));
                child.Right = Describe(Path.Combine(worktree, relative));
            }
            parent = child;
        }
    }

    private static FolderSideInfo? Describe(string path)
    {
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return new FolderSideInfo { FullPath = path, IsDirectory = false, Length = file.Length, LastWriteTimeUtc = file.LastWriteTimeUtc, IsReadOnly = file.IsReadOnly, IsHidden = false };
        }
        return null;
    }

    private static FolderEntryStatus RollUp(FolderEntry entry)
    {
        foreach (var child in entry.Children) RollUp(child);
        if (entry.IsDirectory && entry.Children.Count > 0)
            entry.Status = entry.Children.Any(child => child.Status != FolderEntryStatus.Same) ? FolderEntryStatus.Modified : FolderEntryStatus.Same;
        return entry.Status;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_snapshotDirectory, true); } catch { }
    }
}
