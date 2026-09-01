namespace NaraDiff.Infrastructure.IO;

/// <summary>
/// Watches the files of a comparison and raises one coalesced event per change, so that an external
/// editor saving a file results in a single reload prompt.
/// </summary>
public sealed class FileChangeWatcher : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastSignal = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly TimeSpan _coalesce = TimeSpan.FromMilliseconds(400);
    private bool _disposed;

    /// <summary>Raised with the full path of the file that changed on disk.</summary>
    public event EventHandler<string>? FileChanged;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Replaces the watched set with the given paths.</summary>
    public void Watch(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var wanted = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Where(File.Exists)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var existing in _watchers.Keys.Where(key => !wanted.Contains(key)).ToList())
            {
                _watchers[existing].Dispose();
                _watchers.Remove(existing);
            }
            foreach (var path in wanted)
            {
                if (_watchers.ContainsKey(path)) continue;
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true
                    };
                    watcher.Changed += OnChanged;
                    watcher.Created += OnChanged;
                    watcher.Renamed += OnChanged;
                    _watchers[path] = watcher;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    // A path that cannot be watched simply is not watched; the manual refresh still works.
                }
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (!IsEnabled || _disposed) return;
        var path = Path.GetFullPath(args.FullPath);
        lock (_gate)
        {
            if (_lastSignal.TryGetValue(path, out var previous) && DateTime.UtcNow - previous < _coalesce) return;
            _lastSignal[path] = DateTime.UtcNow;
        }
        FileChanged?.Invoke(this, path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
