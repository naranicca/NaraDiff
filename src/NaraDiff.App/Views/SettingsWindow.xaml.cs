using System.Reflection;
using System.Windows;
using System.Windows.Media;
using NaraDiff.App.Services;
using NaraDiff.Core.Settings;
using NaraDiff.Core.Text;

namespace NaraDiff.App.Views;

/// <summary>
/// Application wide settings. Appearance changes are applied live so the effect is visible behind
/// the dialog; pressing Cancel restores the values that were active when the dialog opened.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly (string Label, SaveLineEndingMode Mode)[] LineEndingModes =
    [
        ("Keep as loaded", SaveLineEndingMode.Preserve),
        ("Always LF", SaveLineEndingMode.Lf),
        ("Always CRLF", SaveLineEndingMode.CrLf),
        ("Always CR", SaveLineEndingMode.Cr)
    ];

    private readonly AppSettings _settings;
    private readonly AppSettings _snapshot;
    private bool _ready;

    public SettingsWindow(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _snapshot = Copy(settings);
        InitializeComponent();
        ThemeBox.Items.Add("Dark");
        ThemeBox.Items.Add("Light");
        ThemeBox.SelectedIndex = settings.Theme == ThemeKind.Light ? 1 : 0;
        foreach (var family in Fonts.SystemFontFamilies.Select(item => item.Source).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) FontBox.Items.Add(family);
        FontBox.Text = settings.EditorFontFamily;
        foreach (var size in new[] { 10, 11, 12, 13, 14, 16, 18, 20 }) FontSizeBox.Items.Add(size);
        FontSizeBox.SelectedItem = (int)Math.Round(settings.EditorFontSize);
        if (FontSizeBox.SelectedItem is null) FontSizeBox.SelectedItem = 13;
        AccessibleBox.IsChecked = settings.ColorBlindPalette;
        LineNumbersBox.IsChecked = settings.ShowLineNumbers;
        WhitespaceBox.IsChecked = settings.ShowWhitespace;
        WordWrapBox.IsChecked = settings.WordWrap;
        ConnectorsBox.IsChecked = settings.ShowConnectors;
        SyncScrollBox.IsChecked = settings.SynchronizeScrolling;
        foreach (var choice in EncodingCatalog.All) EncodingBox.Items.Add(choice);
        EncodingBox.SelectedItem = EncodingCatalog.All.FirstOrDefault(item => item.Id == settings.DefaultEncodingId) ?? EncodingCatalog.Utf8;
        foreach (var (label, _) in LineEndingModes) LineEndingBox.Items.Add(label);
        LineEndingBox.SelectedIndex = Math.Max(0, Array.FindIndex(LineEndingModes, entry => entry.Mode == settings.SaveLineEnding));
        foreach (var delay in new[] { 0, 100, 250, 400, 800, 1500 }) DebounceBox.Items.Add(delay);
        DebounceBox.SelectedItem = settings.DiffDebounceMilliseconds;
        if (DebounceBox.SelectedItem is null) DebounceBox.SelectedItem = 250;
        WatchBox.IsChecked = settings.WatchFilesForChanges;
        VersionText.Text = $"NaraDiff {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";
        _ready = true;
    }

    /// <summary>Raised whenever a live previewable setting changed.</summary>
    public event EventHandler? SettingsApplied;

    private static AppSettings Copy(AppSettings settings) => new()
    {
        Theme = settings.Theme,
        ColorBlindPalette = settings.ColorBlindPalette,
        EditorFontFamily = settings.EditorFontFamily,
        EditorFontSize = settings.EditorFontSize,
        ShowLineNumbers = settings.ShowLineNumbers,
        ShowWhitespace = settings.ShowWhitespace,
        WordWrap = settings.WordWrap,
        ShowConnectors = settings.ShowConnectors,
        SynchronizeScrolling = settings.SynchronizeScrolling
    };

    private void Appearance_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.Theme = ThemeBox.SelectedIndex == 1 ? ThemeKind.Light : ThemeKind.Dark;
        _settings.ColorBlindPalette = AccessibleBox.IsChecked == true;
        _settings.EditorFontFamily = string.IsNullOrWhiteSpace(FontBox. Text) ? "Consolas" : FontBox.Text;
        _settings.EditorFontSize = FontSizeBox.SelectedItem is int size ? size : 13;
        _settings.ShowLineNumbers = LineNumbersBox.IsChecked == true;
        _settings.ShowWhitespace = WhitespaceBox.IsChecked == true;
        _settings.WordWrap = WordWrapBox.IsChecked == true;
        _settings.ShowConnectors = ConnectorsBox.IsChecked == true;
        _settings.SynchronizeScrolling = SyncScrollBox.IsChecked == true;
        ThemeService.Apply(_settings.Theme, _settings.ColorBlindPalette);
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Appearance_Changed(sender, e);
        _settings.DefaultEncodingId = (EncodingBox.SelectedItem as EncodingChoice)?.Id ?? EncodingCatalog.Utf8Id;
        _settings.SaveLineEnding = LineEndingModes[Math.Max(0, LineEndingBox.SelectedIndex)].Mode;
        _settings.DiffDebounceMilliseconds = DebounceBox.SelectedItem is int delay ? delay : 250;
        _settings.WatchFilesForChanges = WatchBox.IsChecked == true;
        SettingsApplied?.Invoke(this, EventArgs.Empty);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = _snapshot.Theme;
        _settings.ColorBlindPalette = _snapshot.ColorBlindPalette;
        _settings.EditorFontFamily = _snapshot.EditorFontFamily;
        _settings.EditorFontSize = _snapshot.EditorFontSize;
        _settings.ShowLineNumbers = _snapshot.ShowLineNumbers;
        _settings.ShowWhitespace = _snapshot.ShowWhitespace;
        _settings.WordWrap = _snapshot.WordWrap;
        _settings.ShowConnectors = _snapshot.ShowConnectors;
        _settings.SynchronizeScrolling = _snapshot.SynchronizeScrolling;
        ThemeService.Apply(_settings.Theme, _settings.ColorBlindPalette);
        SettingsApplied?.Invoke(this, EventArgs.Empty);
        DialogResult = false;
    }
}
