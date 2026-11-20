using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NaraDiff.App.Services;

/// <summary>A revision the working tree can be compared with.</summary>
/// <param name="Revision">What is passed to Git, such as HEAD, a branch name or a commit hash.</param>
/// <param name="Display">The text shown in the revision picker.</param>
public sealed record GitRevision(string Revision, string Display)
{
    public override string ToString() => Display;
}

/// <summary>One changed path of the working tree relative to HEAD, as <c>git status</c> reports it.</summary>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="OriginalPath">The path before a rename or copy; null otherwise.</param>
/// <param name="Code">The two-letter porcelain status code (index, working tree).</param>
public sealed record GitStatusEntry(string Path, string? OriginalPath, string Code)
{
    public bool IsUntracked => Code == "??";

    /// <summary>Some of this change is in the index.</summary>
    public bool IsStaged => Code[0] is not ' ' and not '?';

    /// <summary>Some of this change is only in the working tree.</summary>
    public bool HasUnstagedChanges => Code[1] != ' ';

    /// <summary>"staged", "partly staged" or empty, for showing next to the change.</summary>
    public string StageText => !IsStaged ? string.Empty : HasUnstagedChanges ? "partly staged" : "staged";

    public string Description => Code switch
    {
        "??" => "untracked",
        _ when Code.Contains('U') || Code is "AA" or "DD" => "conflicted",
        _ when Code[0] == 'R' => "renamed",
        _ when Code[0] == 'C' => "copied",
        _ when Code.Contains('A') => "added",
        _ when Code.Contains('D') => "deleted",
        _ => "modified"
    };
}

/// <summary>
/// The Git operations behind the version-control view. Every call runs the <c>git</c> executable
/// found on PATH; paths are passed as literal pathspecs through a file so that long selections and
/// names containing wildcard characters are handled exactly.
/// </summary>
internal sealed class GitRepository
{
    private GitRepository(string root) => Root = root;

    /// <summary>The top-level folder of the working tree.</summary>
    public string Root { get; }

    /// <summary>Opens the repository containing <paramref name="folder"/>, or returns null when it is not inside one.</summary>
    public static async Task<GitRepository?> OpenAsync(string folder, CancellationToken cancellationToken = default)
    {
        string root;
        try
        {
            root = (await RunAsync(folder, ["rev-parse", "--show-toplevel"], cancellationToken)).Trim();
        }
        catch (GitException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        return new GitRepository(Path.GetFullPath(root));
    }

    public Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        RunAsync(Root, arguments, cancellationToken);

    public Task<byte[]> RunBytesAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        RunBytesAsync(Root, arguments, cancellationToken);

    /// <summary>The checked-out branch, or null when HEAD is detached.</summary>
    public async Task<string?> CurrentBranchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var name = (await RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken)).Trim();
            return name.Length == 0 ? null : name;
        }
        catch (GitException)
        {
            return null;
        }
    }

    /// <summary>
    /// Revisions to offer in the picker: HEAD first, then local branches, remote branches, tags and
    /// the most recent commits of the current branch.
    /// </summary>
    public async Task<IReadOnlyList<GitRevision>> ListRevisionsAsync(int commitCount, CancellationToken cancellationToken = default)
    {
        var result = new List<GitRevision> { new("HEAD", "HEAD (last commit)") };
        var current = await CurrentBranchAsync(cancellationToken);
        var refs = await RunAsync(["for-each-ref", "--sort=refname", "--format=%(refname)%09%(refname:short)", "refs/heads", "refs/remotes", "refs/tags"], cancellationToken);
        foreach (var line in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[0].EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            var kind = parts[0].StartsWith("refs/heads/", StringComparison.Ordinal) ? "branch"
                : parts[0].StartsWith("refs/remotes/", StringComparison.Ordinal) ? "remote"
                : "tag";
            if (kind == "branch" && parts[1] == current) continue;
            result.Add(new GitRevision(parts[0], $"{kind}: {parts[1]}"));
        }
        try
        {
            var log = await RunAsync(["log", $"-{commitCount}", "--date=short", "--format=%H%x09%h%x09%ad%x09%an%x09%s"], cancellationToken);
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t', 5);
                if (parts.Length < 5) continue;
                result.Add(new GitRevision(parts[0], $"{parts[1]}  {parts[2]}  {parts[4]}  ({parts[3]})"));
            }
        }
        catch (GitException)
        {
            // A repository without commits has no log; HEAD alone is offered.
        }
        return result;
    }

    /// <summary>The changes of the working tree and index relative to HEAD.</summary>
    public async Task<IReadOnlyList<GitStatusEntry>> StatusAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await RunBytesAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        var fields = Encoding.UTF8.GetString(bytes).Split('\0');
        var result = new List<GitStatusEntry>();
        for (var index = 0; index < fields.Length; index++)
        {
            var record = fields[index];
            if (record.Length < 4) continue;
            var code = record[..2];
            string? original = null;
            if (code[0] is 'R' or 'C' && index + 1 < fields.Length) original = fields[++index];
            result.Add(new GitStatusEntry(record[3..], original, code));
        }
        return result;
    }

    /// <summary>
    /// The folders that hold no tracked file at all, as Git itself decides: with untracked files listed
    /// "normally", such a folder is reported once, as "dir/", instead of file by file. Each path ends
    /// in a slash; folders inside a listed one are untracked too but are not listed separately.
    /// </summary>
    public async Task<IReadOnlySet<string>> UntrackedDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await RunBytesAsync(["status", "--porcelain=v1", "-z", "--untracked-files=normal"], cancellationToken);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var fields = Encoding.UTF8.GetString(bytes).Split('\0');
        for (var index = 0; index < fields.Length; index++)
        {
            var record = fields[index];
            if (record.Length < 4) continue;
            if (record[0] is 'R' or 'C') index++;
            if (record.StartsWith("?? ", StringComparison.Ordinal) && record.EndsWith('/')) result.Add(record[3..]);
        }
        return result;
    }

    /// <summary>The status of one repository-relative path, or null when it has no changes against HEAD.</summary>
    public async Task<GitStatusEntry?> PathStatusAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await RunBytesAsync(["--literal-pathspecs", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", path], cancellationToken);
        var fields = Encoding.UTF8.GetString(bytes).Split('\0');
        for (var index = 0; index < fields.Length; index++)
        {
            var record = fields[index];
            if (record.Length < 4) continue;
            var code = record[..2];
            string? original = null;
            if (code[0] is 'R' or 'C' && index + 1 < fields.Length) original = fields[++index];
            if (record[3..] == path || original == path) return new GitStatusEntry(record[3..], original, code);
        }
        return null;
    }

    /// <summary>Stages exactly the given changes and commits them, leaving anything else that is staged out of the commit.</summary>
    public async Task CommitAsync(IReadOnlyCollection<GitStatusEntry> entries, string message, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) throw new GitException("Select at least one file to commit.");
        if (string.IsNullOrWhiteSpace(message)) throw new GitException("Enter a commit message.");
        // Only paths whose working-tree copy differs from the index need adding; a staged deletion or the
        // old name of a staged rename is in neither the index nor the working tree, and "git add" rejects it.
        var toAdd = entries.Where(entry => entry.Code[1] != ' ').Select(entry => entry.Path).ToList();
        if (toAdd.Count > 0)
        {
            using var addspec = PathspecFile.Create(toAdd);
            await RunAsync(["--literal-pathspecs", "add", "--all", $"--pathspec-from-file={addspec.Path}", "--pathspec-file-nul"], cancellationToken);
        }
        var paths = entries.SelectMany(entry => entry.OriginalPath is null || entry.Code[0] == 'C' ? [entry.Path] : new[] { entry.Path, entry.OriginalPath }).Distinct().ToList();
        using var pathspec = PathspecFile.Create(paths);
        var messageFile = Path.Combine(Path.GetTempPath(), $"naradiff-commit-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(messageFile, message.Replace("\r\n", "\n"), new UTF8Encoding(false), cancellationToken);
        try
        {
            await RunAsync(["--literal-pathspecs", "commit", "--quiet", "--only", $"--file={messageFile}", "--cleanup=whitespace",
                            $"--pathspec-from-file={pathspec.Path}", "--pathspec-file-nul"], cancellationToken);
        }
        finally
        {
            try { File.Delete(messageFile); } catch (IOException) { }
        }
    }

    /// <summary>Stages the working-tree state of the given changes (new, modified and deleted files alike).</summary>
    public async Task StageAsync(IReadOnlyCollection<GitStatusEntry> entries, CancellationToken cancellationToken = default)
    {
        // Entries whose working tree already matches the index have nothing left to stage.
        await StagePathsAsync(entries.Where(entry => entry.Code[1] != ' ').Select(entry => entry.Path).Distinct().ToList(), cancellationToken);
    }

    /// <summary>Stages the working-tree state of repository-relative paths: new, modified and deleted files alike.</summary>
    public async Task StagePathsAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return;
        using var pathspec = PathspecFile.Create(paths);
        await RunAsync(["--literal-pathspecs", "add", "--all", $"--pathspec-from-file={pathspec.Path}", "--pathspec-file-nul"], cancellationToken);
    }

    /// <summary>Takes the given changes out of the index again, leaving the working tree untouched.</summary>
    public async Task UnstageAsync(IReadOnlyCollection<GitStatusEntry> entries, CancellationToken cancellationToken = default)
    {
        var paths = entries.Where(entry => entry.IsStaged)
                           .SelectMany(entry => entry.OriginalPath is null || entry.Code[0] == 'C' ? [entry.Path] : new[] { entry.Path, entry.OriginalPath })
                           .Distinct().ToList();
        if (paths.Count == 0) return;
        using var pathspec = PathspecFile.Create(paths);
        await RunAsync(["--literal-pathspecs", "reset", "--quiet", $"--pathspec-from-file={pathspec.Path}", "--pathspec-file-nul"], cancellationToken);
    }

    /// <summary>Pushes the current branch, setting its upstream on the first push when it has none.</summary>
    public async Task<string> PushAsync(CancellationToken cancellationToken = default)
    {
        var branch = await CurrentBranchAsync(cancellationToken) ?? throw new GitException("HEAD is detached; check out a branch before pushing.");
        var hasUpstream = true;
        try { await RunAsync(["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], cancellationToken); }
        catch (GitException) { hasUpstream = false; }
        if (hasUpstream) return await RunWithMessagesAsync(["push"], cancellationToken);
        var remotes = (await RunAsync(["remote"], cancellationToken)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (remotes.Length == 0) throw new GitException("This repository has no remote to push to.");
        var remote = remotes.Contains("origin") ? "origin" : remotes[0];
        return await RunWithMessagesAsync(["push", "--set-upstream", remote, branch], cancellationToken);
    }

    /// <summary>
    /// Makes the given working-tree paths match <paramref name="revision"/>. Paths that exist in the
    /// revision are restored from it; paths that do not are moved to the Recycle Bin. When the revision
    /// is HEAD the index is reset as well, so the paths no longer show up as changed at all.
    /// </summary>
    public async Task RevertAsync(string revision, IReadOnlyCollection<string> restorePaths, IReadOnlyCollection<string> removePaths, CancellationToken cancellationToken = default)
    {
        var isHead = revision == "HEAD";
        if (restorePaths.Count > 0)
        {
            using var pathspec = PathspecFile.Create(restorePaths);
            var arguments = new List<string> { "--literal-pathspecs", "restore", $"--source={revision}", "--worktree" };
            if (isHead) arguments.Add("--staged");
            arguments.Add($"--pathspec-from-file={pathspec.Path}");
            arguments.Add("--pathspec-file-nul");
            await RunAsync(arguments, cancellationToken);
        }
        if (removePaths.Count == 0) return;
        if (isHead)
        {
            // Newly added files leave the index too; untracked ones are simply not in it.
            using var pathspec = PathspecFile.Create(removePaths);
            await RunAsync(["--literal-pathspecs", "reset", "--quiet", $"--pathspec-from-file={pathspec.Path}", "--pathspec-file-nul"], cancellationToken);
        }
        var files = removePaths.Select(path => System.IO.Path.Combine(Root, path.Replace('/', System.IO.Path.DirectorySeparatorChar)))
                               .Where(File.Exists).ToList();
        RecycleBin.Send(files);
    }

    private async Task<string> RunWithMessagesAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var (output, error) = await RunCoreAsync(Root, arguments, cancellationToken);
        var text = (Encoding.UTF8.GetString(output) + "\n" + error).Trim();
        return text;
    }

    public static async Task<string> RunAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        Encoding.UTF8.GetString(await RunBytesAsync(directory, arguments, cancellationToken));

    public static async Task<byte[]> RunBytesAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        (await RunCoreAsync(directory, arguments, cancellationToken)).Output;

    private static async Task<(byte[] Output, string Error)> RunCoreAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Never wait for a password on a console nobody can see; credential helpers with their own window still work.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new GitException("Git is not installed or is not available on PATH.", ex); }
        process.StandardInput.Close();
        using var output = new MemoryStream();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var error = await errorTask;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new GitException(string.IsNullOrWhiteSpace(error) ? "Git command failed." : error.Trim());
        return (output.ToArray(), error);
    }

    /// <summary>A temporary NUL-separated pathspec file, deleted on dispose.</summary>
    private sealed class PathspecFile : IDisposable
    {
        private PathspecFile(string path) => Path = path;

        public string Path { get; }

        public static PathspecFile Create(IEnumerable<string> paths)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"naradiff-paths-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, string.Join('\0', paths.Select(item => item.Replace('\\', '/'))), new UTF8Encoding(false));
            return new PathspecFile(path);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { }
        }
    }
}

/// <summary>A Git command failed; the message is Git's own error output.</summary>
public sealed class GitException : Exception
{
    public GitException(string message) : base(message) { }

    public GitException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Moves files to the Windows Recycle Bin instead of deleting them outright.</summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    public static void Send(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // The list is NUL separated and ends with a double NUL.
            pFrom = string.Join('\0', paths.Select(Path.GetFullPath)) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        var code = SHFileOperation(ref operation);
        if (code != 0 || operation.fAnyOperationsAborted)
            throw new IOException($"Some files could not be moved to the Recycle Bin (error 0x{code:X}).");
    }
}
