using NaraDiff.Core.Diff;
using NaraDiff.Core.Folders;
using NaraDiff.Core.Text;

namespace NaraDiff.Core.Settings;

public enum ThemeKind
{
    Dark,
    Light
}

/// <summary>What happens to line terminators when a file is saved.</summary>
public enum SaveLineEndingMode
{
    /// <summary>Keep every terminator exactly as loaded.</summary>
    Preserve,
    Lf,
    CrLf,
    Cr
}

/// <summary>Everything that is remembered between sessions.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public ThemeKind Theme { get; set; } = ThemeKind.Dark;

    /// <summary>Uses a blue and amber palette instead of green and red.</summary>
    public bool ColorBlindPalette { get; set; }

    public string EditorFontFamily { get; set; } = "Consolas";

    public double EditorFontSize { get; set; } = 13;

    public bool ShowLineNumbers { get; set; } = true;

    public bool ShowWhitespace { get; set; }

    public bool WordWrap { get; set; }

    public bool ShowConnectors { get; set; } = true;

    public bool SynchronizeScrolling { get; set; } = true;

    /// <summary>Delay before a comparison is recalculated while typing.</summary>
    public int DiffDebounceMilliseconds { get; set; } = 250;

    public bool WatchFilesForChanges { get; set; } = true;

    public SaveLineEndingMode SaveLineEnding { get; set; } = SaveLineEndingMode.Preserve;

    public string DefaultEncodingId { get; set; } = EncodingCatalog.Utf8Id;

    public DiffOptions DiffOptions { get; set; } = new();

    public FolderCompareOptions FolderOptions { get; set; } = new();

    public SyncOptions SyncOptions { get; set; } = new();

    /// <summary>Saved diff option presets, addressed by their name.</summary>
    public List<DiffOptions> Presets { get; set; } = [];

    public List<string> RecentFiles { get; set; } = [];

    public List<string> RecentFolders { get; set; } = [];

    public double WindowWidth { get; set; } = 1360;

    public double WindowHeight { get; set; } = 860;

    public bool WindowMaximized { get; set; }

    public void RememberFile(string path) => Remember(RecentFiles, path);

    public void RememberFolder(string path) => Remember(RecentFolders, path);

    private static void Remember(List<string> list, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        list.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > 16) list.RemoveRange(16, list.Count - 16);
    }

    /// <summary>Stores a preset under its name, replacing an existing one.</summary>
    public void SavePreset(DiffOptions preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var copy = preset.Sanitized();
        if (string.IsNullOrWhiteSpace(copy.Name)) copy.Name = "Preset";
        Presets.RemoveAll(item => string.Equals(item.Name, copy.Name, StringComparison.OrdinalIgnoreCase));
        Presets.Add(copy);
        Presets.Sort(static (first, second) => string.Compare(first.Name, second.Name, StringComparison.OrdinalIgnoreCase));
    }

    public bool RemovePreset(string name) => Presets.RemoveAll(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

    public DiffOptions? FindPreset(string name) => Presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fills in missing values after loading a settings file written by an older build.</summary>
    public static AppSettings EnsureUsable(AppSettings? settings)
    {
        settings ??= new AppSettings();
        settings.DiffOptions = (settings.DiffOptions ?? new DiffOptions()).Sanitized();
        settings.FolderOptions ??= new FolderCompareOptions();
        settings.SyncOptions ??= new SyncOptions();
        settings.Presets ??= [];
        settings.RecentFiles ??= [];
        settings.RecentFolders ??= [];
        settings.EditorFontSize = Math.Clamp(settings.EditorFontSize, 8, 48);
        settings.DiffDebounceMilliseconds = Math.Clamp(settings.DiffDebounceMilliseconds, 0, 5000);
        if (string.IsNullOrWhiteSpace(settings.EditorFontFamily)) settings.EditorFontFamily = "Consolas";
        if (settings.Presets.Count == 0) settings.Presets.AddRange(BuiltInPresets());
        return settings;
    }

    /// <summary>Presets that ship with the application.</summary>
    public static IEnumerable<DiffOptions> BuiltInPresets()
    {
        yield return new DiffOptions { Name = "Exact" , IgnoreLineEndings = false };
        yield return new DiffOptions { Name = "Ignore whitespace", IgnoreAllWhitespace = true, TreatTabsAsSpaces = true };
        yield return new DiffOptions { Name = "Source code", IgnoreLeadingWhitespace = true, IgnoreTrailingWhitespace = true, TreatTabsAsSpaces = true, IgnoreBlankLines = true };
        yield return new DiffOptions
        {
            Name = "Ignore comments",
            IgnoreLeadingWhitespace = true,
            IgnoreTrailingWhitespace = true,
            IgnoreBlankLines = true,
            IgnoredLinePrefixes = ["//", "#", "--"]
        };
    }
}
