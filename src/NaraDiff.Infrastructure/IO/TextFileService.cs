using NaraDiff.Core.Services;
using NaraDiff.Core.Text;

namespace NaraDiff.Infrastructure.IO;

/// <summary>
/// Loads and saves text files. Every expected failure (missing file, no permission, locked file,
/// oversized file) becomes a message instead of an exception, and saving writes to a temporary file
/// first so the original is never left half written.
/// </summary>
public sealed class TextFileService : ITextFileService
{
    /// <summary>Files above this size are refused for text comparison.</summary>
    public const long MaxTextFileBytes = 256L * 1024 * 1024;

    public async Task<FileLoadResult> LoadAsync(string path, EncodingChoice? encoding = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return FileLoadResult.Failure(path ?? string.Empty, "No file was selected.");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return FileLoadResult.Failure(info.FullName, "The file does not exist.");
            if (info.Length > MaxTextFileBytes)
                return FileLoadResult.Failure(info.FullName, $"The file is larger than {MaxTextFileBytes / (1024 * 1024)} MB and cannot be compared as text.");
            byte[] bytes;
            var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                bytes = new byte[stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    offset += read;
                }
            }
            return new FileLoadResult
            {
                Path = info.FullName,
                Content = TextFileContent.FromBytes(bytes, encoding),
                IsReadOnly = info.IsReadOnly,
                Exists = true,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                Length = info.Length
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FileLoadResult.Failure(path, Describe(ex));
        }
    }

    public async Task<FileSaveResult> SaveAsync(FileSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var temporary = request.Path + ".naradiff.tmp";
        try
        {
            var lines = request.LineEnding == LineEndingKind.None ? request.Lines : LineEndings.Normalize(request.Lines, request.LineEnding);
            var bytes = request.Encoding.GetBytes(LineEndings.Join(lines));
            var directory = Path.GetDirectoryName(Path.GetFullPath(request.Path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(request.Path))
            {
                var backup = request.CreateBackup ? request.Path + ".bak" : null;
                File.Replace(temporary, request.Path, backup, true);
            }
            else File.Move(temporary, request.Path);
            var info = new FileInfo(request.Path);
            return new FileSaveResult { Path = info.FullName, Succeeded = true, LastWriteTimeUtc = info.LastWriteTimeUtc, Length = info.Length };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FileSaveResult { Path = request.Path, Succeeded = false, Error = Describe(ex) };
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    /// <summary>Turns file system exceptions into text a user can act on.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Access to the file was denied. Check the file permissions or the read-only flag.",
        FileNotFoundException => "The file does not exist.",
        DirectoryNotFoundException => "The folder does not exist.",
        PathTooLongException => "The path is too long for the file system.",
        IOException io when io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) => "The file is locked by another program.",
        IOException io => $"The file could not be read or written: {io.Message}",
        NotSupportedException => "The path format is not supported.",
        _ => exception.Message
    };
}
