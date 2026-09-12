using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO.Compression;
using NaraDiff.Core.Folders;
using System.Reflection.Metadata.Ecma335;

namespace NaraDiff.App.Services;

/// <summary>Builds a folder-comparison tree for the changes between HEAD and a Git working tree.</summary>
internal sealed class GitWorktreeComparer : IDisposable
{
    private readonly string _snapshotDirectory;
    private bool _disposed;

    private GitWorktreeComparer(string repositoryRoot, string snapshotDirectory, FolderComparisonResult result)
    {
        RepositoryRoot = repositoryRoot;
        _snapshotDirectory = snapshotDirectory;
        Result = result;
    }

    public string RepositoryRoot { get; }
    public FolderComparisonResult Result { get; }

    public static async Task<GitWorktreeComparer?> CreateAsync(string folder, CancellationToken cancellationToken)
    {
        var root = (await RunGitAsync(folder, "rev-parse", "--show-toplevel", cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        root = Path.GetFullPath(root);
        var snapshot = Path.Combine(Path.GetTempPath(), "NaraDiff", "git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshot);
        var archive = Path.Combine(snapshot, "head.zip");
        try
        {
            await RunGitAsync(root, "archive", "--format=zip", "--output=" + archive, "HEAD", cancellationToken);
            ZipFile.ExtractToDirectory(archive, snapshot, true);
            File.Delete(archive);
            var records = await RunGitBytesAsync(root, "status", "--porcelain=v1", "-z", "--untracked-files=all", cancellationToken);
            var rootEntry = new FolderEntry { Name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), RelativePath = string.Empty, IsDirectory = true };
            var entries = ParseStatus(records);
            foreach (var change in entries) AddEntry(rootEntry, change, snapshot, root);
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
            return new GitWorktreeComparer(root, snapshot, new FolderComparisonResult
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

    private static List<(string Path, FolderEntryStatus Status)> ParseStatus(byte[] bytes)
    {
        var fields = System.Text.Encoding.UTF8.GetString(bytes).Split('\0');
        var result = new List<(string, FolderEntryStatus)>();
        for (var index = 0; index < fields.Length; index++)
        {
            var record = fields[index];
            if (record.Length < 4) continue;
            var code = record[..2];
            var path = record[3..];
            if (code[0] is 'R' or 'C') index++; //consume the old namein a rename/copy record
            var status = code == "??" || code[0] == 'A' ? FolderEntryStatus.RightOnly
                : code[0] == 'D' || code[1] == 'D' ? FolderEntryStatus.LeftOnly
                : FolderEntryStatus.Modified;
            result.Add((path.Replace('/', Path.DirectorySeparatorChar), status));
        }
        return result;
    }

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

    private static string CreateEmptySide(string snapshot, string side, string relative)
    {
        var path = Path.Combine(snapshot, ".empty", side, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllBytes(path, []);
        return path;
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

    private static async Task<string> RunGitAsync(string directory, params object[] args)
    {
        var cancellationToken = args[^1] is CancellationToken token ? token : default;
        var arguments = args.Take(args.Length - 1).Cast<string>().ToArray();
        var bytes = await RunGitBytesAsync(directory, arguments, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> RunGitBytesAsync(string directory, string first, string second, string third, string fourth, CancellationToken cancellationToken) =>
        await RunGitBytesAsync(directory, new[] { first, second, third, fourth }, cancellationToken);

    private static async Task<byte[]> RunGitBytesAsync(string directory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException("Git is not installed or is not available on PATH.", ex); }
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Git command failed." : error.Trim());
        return output.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_snapshotDirectory, true); } catch { }
    }
}