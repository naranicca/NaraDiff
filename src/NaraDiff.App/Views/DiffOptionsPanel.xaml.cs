using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NaraDiff.Core.Diff;
using NaraDiff.Core.Settings;

namespace NaraDiff.App.Views;

/// <summary>
/// The options flyout. Every change is applied immediately, so the comparison in the active tab
/// updates while the panel is open. Presets are stored with the application settings.
/// </summary>
public partial class DiffOptionsPanel : UserControl
{
    private readonly AppSettings _settings;
    private bool _suppress;

    public DiffOptionsPanel(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        InitializeComponent();
        foreach (var kind in Enum.GetValues<DiffAlgorithmKind>()) AlgorithmBox.Items.Add(kind.ToString());
        foreach (var mode in Enum.GetValues<InlineDiffMode>()) InlineBox.Items.Add(mode.ToString());
        foreach (var width in new[] { 1, 2, 3, 4, 6, 8, 12, 16 }) TabWidthBox.Items.Add(width);
        ReloadPresets();
        Load(settings.DiffOptions);
    }

    /// <summary>Raised whenever the options change; the host recompares the active tab.</summary>
    public event EventHandler<DiffOptions>? OptionsChanged;

    public event EventHandler? CloseRequested;

    public DiffOptions Current { get; private set; } = DiffOptions.Default;

    public void Load(DiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _suppress = true;
        Current = options.Clone();
        AlgorithmBox.SelectedItem = Current.Algorithm.ToString();
        InlineBox.SelectedItem = Current.InlineMode.ToString();
        TabWidthBox.SelectedItem = ClosestTabWidth(Current.TabWidth);
        MovesBox.IsChecked = Current.DetectMoves;
        LeadingBox.IsChecked = Current.IgnoreLeadingWhitespace;
        TrailingBox.IsChecked = Current.IgnoreTrailingWhitespace;
        RunsBox.IsChecked = Current.IgnoreWhitespaceRuns;
        AllWhitespaceBox.IsChecked = Current.IgnoreAllWhitespace;
        TabsBox.IsChecked = Current.TreatTabsAsSpaces;
        CaseBox.IsChecked = Current.IgnoreCase;
        BlankLinesBox.IsChecked = Current.IgnoreBlankLines;
        LineEndingsBox.IsChecked = Current.IgnoreLineEndings;
        PatternBox.Text = string.Join(Environment.NewLine, Current.IgnoredLinePatterns);
        PrefixBox.Text = string.Join(" ", Current.IgnoredLinePrefixes);
        _suppress = false;
        UpdateEnabledState();
    }

    private static int ClosestTabWidth(int width) => new[] { 1, 2, 3, 4, 6, 8, 12, 16 }.OrderBy(value => Math.Abs(value - width)).First();

    private void ReloadPresets()
    {
        _suppress = true;
        PresetBox.Items.Clear();
        PresetBox.Items.Add("(current)");
        foreach (var preset in _settings.Presets) PresetBox.Items.Add(preset.Name);
        PresetBox.SelectedIndex = 0;
        _suppress = false;
    }

    private void UpdateEnabledState()
    {
        var all = AllWhitespaceBox.IsChecked == true;
        LeadingBox.IsEnabled = !all;
        TrailingBox.IsEnabled = !all;
        RunsBox.IsEnabled = !all;
        TabWidthBox.IsEnabled = TabsBox.IsChecked == true;
    }

    private void Option_Changed(object sender, RoutedEventArgs e) => Commit();

    private void Rules_Committed(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    private void Commit()
    {
        if (_suppress) return;
        var options = new DiffOptions
        {
            Name = Current.Name,
            Algorithm = Enum.TryParse<DiffAlgorithmKind>(AlgorithmBox.SelectedItem as string, out var algorithm) ? algorithm : DiffAlgorithmKind.Histogram,
            InlineMode = Enum.TryParse<InlineDiffMode>(InlineBox.SelectedItem as string, out var inline) ? inline : InlineDiffMode.Word,
            TabWidth = TabWidthBox.SelectedItem is int width ? width : 4,
            DetectMoves = MovesBox.IsChecked == true,
            IgnoreLeadingWhitespace = LeadingBox.IsChecked == true,
            IgnoreTrailingWhitespace = TrailingBox.IsChecked == true,
            IgnoreWhitespaceRuns = RunsBox.IsChecked == true,
            IgnoreAllWhitespace = AllWhitespaceBox.IsChecked == true,
            TreatTabsAsSpaces = TabsBox.IsChecked == true,
            IgnoreCase = CaseBox.IsChecked == true,
            IgnoreBlankLines = BlankLinesBox.IsChecked == true,
            IgnoreLineEndings = LineEndingsBox.IsChecked == true,
            IgnoredLinePatterns = [.. PatternBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            IgnoredLinePrefixes = [.. PrefixBox.Text.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
        };
        var invalid = options.IgnoredLinePatterns.Where(pattern => !DiffOptions.IsValidPattern(pattern)).ToList();
        PatternError.Visibility = invalid.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PatternError.Text = invalid.Count == 0 ? string.Empty : $"Ignored, not a valid expression: {string.Join(", ", invalid)}";
        Current = options.Sanitized();
        _settings.DiffOptions = Current;
        UpdateEnabledState();
        OptionsChanged?.Invoke(this, Current);
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || PresetBox.SelectedIndex <= 0) return;
        var preset = _settings.FindPreset(PresetBox.SelectedItem as string ?? string.Empty);
        if (preset is null) return;
        Load(preset);
        Commit();
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "Type a name for the preset first.", "NaraDiff", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var preset = Current.Clone();
        preset.Name = name;
        _settings.SavePreset(preset);
        ReloadPresets();
        PresetBox.SelectedItem = name;
        PresetNameBox.Text = string.Empty;
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedIndex <= 0) return;
        var name = PresetBox.SelectedItem as string ?? string.Empty;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete the preset \"{name}\"?", "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _settings.RemovePreset(name);
        ReloadPresets();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Load(DiffOptions.Default);
        Commit();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
