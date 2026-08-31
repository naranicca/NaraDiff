using NaraDiff.Core.Folders;

namespace NaraDiff.Infrastructure.IO;

public sealed class SyncActionResult
{
    public required SyncAction Action { get; init; }

    public required bool Succeeded { get; init; }

    public string? Error { get; init; }
}

public sealed class SyncExecutionReport
{
    public required List<SyncActionResult> Results { get; init; }

    public int SucceededCount => Results.Count(result => result.Succeeded);

    public int FailedCount => Results.Count(result => !result.Succeeded);

    public IEnumerable<SyncActionResult> Failures => Results.Where(result => !result.Succeeded);
}

/// <summary>
/// Executes a confirmed synchronisation plan. Deletions are only performed when the caller passes
/// <c>allowDeletions</c>, so a plan can never remove files by accident.
/// </summary>
public sealed class DirectorySyncExecutor
{
    public Task<SyncExecutionReport> ExecuteAsync(SyncPlan plan, bool allowDeletions, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => Execute(plan, allowDeletions, progress, cancellationtoken), cancellationToken);
    }

    private static SyncExecutionReport Execute(SyncPlan plan, bool allowDeletions, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var results = new List<SyncActionResult>(plan.Actions.Count);
        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(action.RelativePath);
            if (action.IsDelete && !allowDeletions)
            {
                results.Add(new SyncActionResult { Action = action, Succeeded = false, Error = "Deletions were not confirmed." });
                continue;
            }
            try
            {
                switch (action.Kind)
                {
                    case SyncActionKind.CreateDirectoryLeft:
                    case SyncActionKind.CreateDirectoryRight:
                        Directory.CreateDirectory(action.TargetPath);
                        break;
                    case SyncActionKind.CopyLeftToRight:
                    case SyncActionKind.CopyRightToLeft:
                        var directory = Path.GetDirectoryName(action.TargetPath);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        if (action.SourcePath is null) throw new InvalidOperationException("The copy action has no source path.");
                        ClearReadOnly(action.TargetPath);
                        File.Copy(action.SourcePath, action.TargetPath, true);
                        break;
                    case SyncActionKind.DeleteLeft:
                    case SyncActionKind.DeleteRight:
                        if (action.IsDirectory) Directory.Delete(action.TargetPath, true);
                        else
                        {
                            ClearReadOnly(action.TargetPath);
                            File.Delete(action.TargetPath);
                        }
                        break;
                }
                results.Add(new SyncActionResult { Action = action, Succeeded = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                results.Add(new SyncActionResult { Action = action, Succeeded = false, Error = TextFileService.Describe(ex) });
            }
        }
        return new SyncExecutionReport { Results = results };
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
}