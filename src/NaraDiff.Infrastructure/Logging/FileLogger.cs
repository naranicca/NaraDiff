namespace NaraDiff.Infrastructure.Logging;

/// <summary>Minimal append only log used for unexpected failures.</summary>
public sealed class FileLogger
{
    private readonly string _path;
    private readonly object gate = new();

    public FileLogger(string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaraDiff", "logs");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, $"naradiff-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    public void Error(string area, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write($"ERROR\t{area}\t{exception.GetType().Name}\t{exception.Message}{Environment.NewLine}{exception.StackTrace}");
    }

    public void Info(string area, string message) => Write($"INFO\t{area}\t{message}");

    private void Write(string body)
    {
        var line = $"{DateTimeOffset.Now:0}\t{body}{Environment.NewLine}";
        try
        {
            lock (_gate) File.AppendAllText(_path, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}