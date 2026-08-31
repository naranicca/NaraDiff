using System.Text.Json;
using System.Text.Json.Serialization;
using NaraDiff.Core.Services;
using NaraDiff.Core.Settings;

namespace NaraDiff.Infrastructure.Persistence;

/// <summary>
/// Stores the settings as JSON under %LOCALAPPDATA%\NaraDiff, writing through a temporary file and
/// keeping one backup so a damaged file never loses the whole configuration.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public JsonSettingsStore(string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaraDiff");
        Root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        _path = Path.Combine(root, "settings.json");
        _backupPath = Path.Combine(root, "settings.backup.json");
    }

    public string Root { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in new[] { _path, backupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var stream = File.OpenRead(path);
                await using (stream.ConfigureAwait(false))
                {
                        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken).ConfigureAwait(false);
                        if (settings is not null) return AppSettings.EnsureUsable(settings);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Fall through to the backup, then to defaults.
            }
        }
        return AppSettings.EnsureUsable(null);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = _path + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, _backupPath, true);
            else File.Move(temporary, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        // Settings are a convenience; a failure to persist them must not break the session.
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            _gate.Release();
        }
    }
}