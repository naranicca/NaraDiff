using NaraDiff.Core.Settings;
using NaraDiff.Core.Text;

namespace NaraDiff.Core.Services;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>A loaded file, or a friendly error message when it could not be read.</summary>
public sealed class FileLoadResult
{
    public required string Path { get; init; }

    public required TextFileContent Content { get; init; }

    public bool IsReadOnly { get; init; }

    public bool Exists { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    public long Length { get; init; }

    /// <summary>Non null when loading failed; the UI shows this text instead of the file.</summary>
    public string? Error { get; init; }

    public bool Failed => Error is not null;

    public static FileLoadResult Failure(string path, string error) => new()
    {
        Path = path,
        Content = TextFileContent.Empty,
        Error = error,
        Exists = File.Exists(path)
    };
}

public sealed class FileSaveRequest
{
    public required string Path { get; init; }

    public required IReadOnlyList<TextLine> Lines { get; init; }

    public required EncodingChoice Encoding { get; init; }

    /// <summary>Terminator to write, or None to keep the terminators of the lines.</summary>
    public LineEndingKind LineEnding { get; init; } = LineEndingKind.None;

    /// <summary>Keeps a .bak copy of the previous content.</summary>
    public bool CreateBackup { get; init; } = true;
}

public sealed class FileSaveResult
{
    public required string Path { get; init; }

    public required bool Succeeded { get; init; }

    public string? Error { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    public long Length { get; init; }
}

public interface ITextFileService
{
    Task<FileLoadResult> LoadAsync(string path, EncodingChoice? encoding = null, CancellationToken cancellationToken = default);

    Task<FileSaveResult> SaveAsync(FileSaveRequest request, CancellationToken cancellationToken = default);
}