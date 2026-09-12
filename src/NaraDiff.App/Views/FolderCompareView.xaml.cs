using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NaraDiff.App.Controls;
using NaraDiff.App.Services;
using NaraDiff.Core.Diff;
using NaraDiff.Core.Folders;
using NaraDiff.Core.Settings;
using NaraDiff.Infrastructure.Logging;

namespace NaraDiff.App.Views;

/// <summary>One row of the folder comparison tree.</summary>
public sealed class FolderRow
{
    public required FolderEntry Entry { get; init; }

    public required string Name { get; init; }

    public required string StatusText { get; init; }

    /// <summary>File extension, or folder for directories.</summary>
    public required string TypeText { get; init; }

    /// <summary>Glyph selected from the entry kind and its file extension.</summary>
    public required string IconGlyph { get; init; }

    public bool IsDirectory { get; init; }

    public required Brush StatusFill { get; init; }

    public required Brush StatusStroke { get; init; }

    public string LeftSize { get; init; } = string.Empty;

    public string RightSize { get; init; } = string.Empty;

    public string LeftModified { get; init; } = string.Empty;

    public string RightModified { get; init; } = string.Empty;

    public string Tooltip { get; init; } = string.Empty;

    /// <summary>Indentation of this row; the tree draws every row across the full width.</summary>
    public Thickness Indent { get; init; }

    public Visibility ExpanderVisibility { get; init; } = Visibility.Hidden;

    public List<FolderRow> Children { get; } = [];
}

/// <summary>
/// Recursive folder comparison with a status tree. Comparisons run in the background and can be
/// cancelled; synchronisation always goes through a preview that the user has to confirm.
/// </summary>
public partial class FolderCompareView : UserControl, IComparisonView, IDisposable
{
    private static readonly (string Label, FolderContentMode Mode)[] ContentModes =
    [
        ("Size and time (fast)", FolderContentMode.SizeAndTime),
        ("Size only", FolderContentMode.SizeOnly),
        ("Full content (binary)", FolderContentMode.BinaryContent),
        ("Text aware (diff options)", FolderContentMode.TextAware)
    ];

    private readonly FileLogger _logger;
    private AppSettings _settings;
    private DiffOptions _diffOptions;
    private FolderComparisonResult? _result;
    private CancellationTokenSource? _comparison;
    private GitWorktreeComparer? _gitComparison;
    private bool _suppressEvents;
    private bool _disposed;
    private StylusDevice? _panningStylus;
    private Point _stylusPanStart;
    private Point _stylusScrollStart;
    private Point _lastStylusPanPoint;
    private TimeSpan _lastStylusPanTime;
    private Vector _stylusVelocity;
    private bool _isStylusPanning;
    private readonly InertialPan _stylusInertia;

    public FolderCompareView(AppSettings settings, FileLogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
        _diffOptions = settings.DiffOptions.Sanitized();
        InitializeComponent();
        _stylusInertia = new InertialPan(delta =>
        {
            var scrollViewer = FindDescendant<ScrollViewer>(Tree);
            if (scrollViewer is null) return;
            scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset + delta.Y));
            scrollViewer.ScrollToHorizontalOffset(Math.Max(0, scrollViewer.HorizontalOffset + delta.X));
        });
        _suppressEvents = true;
        foreach (var (label, _) in ContentModes) ContentModeBox.Items.Add(label);
        var options = settings.FolderOptions;
        ContentModeBox.SelectedIndex = Math.Max(0, Array.FindIndex(ContentModes, entry => entry.Mode == options.ContentMode));
        RecursiveBox.IsChecked = options.Recursive;
        HiddenBox.IsChecked = options.IncludeHidden;
        CaseBox.IsChecked = options.CaseSensitiveNames;
        ExcludeBox.Text = string.Join(";", options.ExcludePatterns);
        _suppressEvents = false;
        ExcludeBox.TextChanged += (_, _) => { if (!_suppressEvents && _result is not null) RebuildTree(); };
        Tree.PreviewStylusDown += Tree_StylusPanDown;
        Tree.PreviewStylusMove += Tree_StylusPanMove;
        Tree.PreviewStylusUp += Tree_StylusPanUp;
        Tree.LostStylusCapture += (_, _) => EndTreeStylusPan();
        foreach (var (box, left) in new[] { (LeftPathBox, true), (RightPathBox, false) })
        {
            var isLeft = left;
            box.AllowDrop = true;
            box.PreviewDragEnter += (_, e) => SetFolderDropEffect(e);
            box.PreviewDragOver += (_, e) => SetFolderDropEffect(e);
            box.PreviewDrop += async (_, e) => await HandleFolderDrop(isLeft, e);
        }
    }

    public event EventHandler? TitleChanged;

    public event EventHandler? StatusChanged;

    /// <summary>Raised when the user opens a file pair from the tree.</summary>
    public event EventHandler<(string Left, string Right)>? FileComparisonRequested;

    public string Title
    {
        get
        {
            var left = SafeName(LeftPathBox.Text);
            var right = SafeName(RightPathBox.Text);
            return left.Length == 0 && right.Length == 0 ? "Folders" : $"{left} — {right}";
        }
    }

    public string StatusText { get; private set; } = "No folder comparison yet";

    public bool HasUnsavedChanges => false;

    public bool CanApplyChanges => false;

    public async Task OpenAsync(string? leftPath, string? rightPath)
    {
        if (!string.IsNullOrWhiteSpace(leftPath)) LeftPathBox.Text = leftPath;
        if (!string.IsNullOrWhiteSpace(rightPath)) RightPathBox.Text = rightPath;
        if (Directory.Exists(LeftPathBox.Text) || Directory.Exists(RightPathBox.Text)) await CompareAsync();
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _diffOptions = settings.DiffOptions.Sanitized();
        RebuildTree();
    }

    public void ApplyDiffOptions(DiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _diffOptions = options.Sanitized();
        if (CurrentOptions().ContentMode == FolderContentMode.TextAware && _result is not null) _ = CompareAsync();
    }

    public Task RefreshAsync() => CompareAsync();

    public Task<bool> SaveAsync() => Task.FromResult(true);

    public void NextChange() => MoveSelection(1);

    public void PreviousChange() => MoveSelection(-1);

    public void ApplyToRight() => SetFooter("Use Synchronise to copy files between the folders.");

    public void ApplyToLeft() => ApplyToRight();

    public void ApplyAllToRight() => ApplyToRight();

    public void ApplyAllToLeft() => ApplyToRight();

    public void FocusSearch() => Tree.Focus();

    public void Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stylusInertia.Stop();
        _comparison?.Cancel();
        _comparison?.Dispose();
        _gitComparison?.Dispose();
    }

    private void Tree_StylusPanDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice.Type != TabletDeviceType.Stylus) return;
        var scrollViewer = FindDescendant<ScrollViewer>(Tree);
        if (scrollViewer is null) return;
        _stylusInertia.Stop();
        _panningStylus = e.StylusDevice;
        _stylusPanStart = e.GetPosition(Tree);
        _lastStylusPanPoint = _stylusPanStart;
        _lastStylusPanTime = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        _stylusVelocity = new Vector();
        _stylusScrollStart = new Point(scrollViewer.HorizontalOffset, scrollViewer.VerticalOffset);
        _isStylusPanning = false;
    }

    private void Tree_StylusPanMove(object sender, StylusEventArgs e)
    {
        if (!ReferenceEquals(e.StylusDevice, _panningStylus)) return;
        var delta = e.GetPosition(Tree) - _stylusPanStart;
        if (!_isStylusPanning && delta.Length < 3) return;
        var scrollViewer = FindDescendant<ScrollViewer>(Tree);
        if (scrollViewer is null) return;
        var position = e.GetPosition(Tree);
        var now = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        var elapsedMilliseconds = (now - _lastStylusPanTime).TotalMilliseconds;
        if (elapsedMilliseconds > 0)
        {
            var movement = position - _lastStylusPanPoint;
            var instantaneousVelocity = new Vector(-movement.X / elapsedMilliseconds, -movement.Y / elapsedMilliseconds);
            _stylusVelocity = _stylusVelocity * 0.35 + instantaneousVelocity * 0.65;
        }
        _lastStylusPanPoint = position;
        _lastStylusPanTime = now;
        _isStylusPanning = true;
        if (!Tree.IsStylusCaptured) Tree.CaptureStylus();
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, _stylusScrollStart.Y - delta.Y));
        scrollViewer.ScrollToHorizontalOffset(Math.Max(0, _stylusScrollStart.X - delta.X));
        e.Handled = true;
    }

    private void Tree_StylusPanUp(object sender, StylusEventArgs e)
    {
        if (!ReferenceEquals(e.StylusDevice, _panningStylus)) return;
        var wasPanning = _isStylusPanning;
        var velocity = _stylusVelocity;
        EndTreeStylusPan();
        if (!wasPanning) return;
        _stylusInertia.Start(velocity);
        e.Handled = true;
    }

    private void EndTreeStylusPan()
    {
        if (Tree.IsStylusCaptured) Tree.ReleaseStylusCapture();
        _panningStylus = null;
        _isStylusPanning = false;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private FolderCompareOptions CurrentOptions() => new()
    {
        Recursive = RecursiveBox.IsChecked == true,
        IncludeHidden = HiddenBox.IsChecked == true,
        CaseSensitiveNames = CaseBox.IsChecked == true,
        ContentMode = ContentModes[Math.Max(0, ContentModeBox.SelectedIndex)].Mode,
        ExcludePatterns = [.. ExcludeBox.Text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        TimeToleranceSeconds = _settings.FolderOptions.TimeToleranceSeconds,
        MaxContentCompareBytes = _settings.FolderOptions.MaxContentCompareBytes
    };

    private async Task CompareAsync()
    {
        var left = LeftPathBox.Text.Trim();
        var right = RightPathBox.Text.Trim();
        var singleFolder = Directory.Exists(left) && string.IsNullOrWhiteSpace(right)
            ? left
            : Directory.Exists(right) && string.IsNullOrWhiteSpace(left) ? right : null;
        if (singleFolder is not null)
        {
            _gitComparison?.Dispose();
            _gitComparison = await GitWorktreeComparer.CreateAsync(singleFolder, CancellationToken.None);
            if (_gitComparison is null)
            {
                SetFooter("Enter both folders, or select a Git repository to view its working-tree changes.");
                return;
            }
            _result = _gitComparison.Result;
            RebuildTree();
            SetFooter("Git changes: HEAD — working tree");
            TitleChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!Directory.Exists(left) || !Directory.Exists(right))
        {
            SetFooter("Both folders must exist before they can be compared.");
            return;
        }
        _gitComparison?.Dispose();
        _gitComparison = null;
        _comparison?.Cancel();
        _comparison?.Dispose();
        var source = new CancellationTokenSource();
        _comparison = source;
        var options = CurrentOptions();
        _settings.FolderOptions = options;
        _settings.RememberFolder(left);
        _settings.RememberFolder(right);
        CompareButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.Visibility = Visibility.Visible;
        var progress = new Progress<string>(path => SetFooter($"Scanning {path}"));
        try
        {
            _result = await DirectoryComparer.CompareAsync(left, right, options, _diffOptions, progress, source.Token);
            RebuildTree();
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            SetFooter("The comparison was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("folder-compare", ex);
            SetFooter($"The folders could not be compared: {ex.Message}");
        }
        finally
        {
            CompareButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void RebuildTree()
    {
        if (_result is null) return;
        var onlyDifferences = OnlyDifferencesBox.IsChecked == true;
        var rows = new List<FolderRow>();
        var exclusions = new GlobMatcher(CurrentOptions().ExcludePatterns, CurrentOptions().CaseSensitiveNames);
        foreach (var child in _result.Root.Children)
        {
            var row = BuildRow(child, onlyDifferences, 0, exclusions);
            if (row is not null) rows.Add(row);
        }
        Tree.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = rows.Count == 0
            ? _result.AreIdentical ? "The folders are identical." : "No entry matches the current filter."
            : string.Empty;
        var statistics = _result.Statistics;
        StatusText = $"{statistics.Files:N0} files, {statistics.Directories:N0} folders: {statistics.Modified:N0} modified, {statistics.LeftOnly:N0} only left, {statistics.RightOnly:N0} only right, {statistics.Same:N0} identical" +
                     (statistics.Errors > 0 ? $", {statistics.Errors:N0} errors" : string.Empty);
        SetFooter(StatusText);
    }

    private FolderRow? BuildRow(FolderEntry entry, bool onlyDifferences, int depth, GlobMatcher exclusions)
    {
        if (exclusions.IsExcluded(entry.Name, entry.RelativePath)) return null;
        var children = new List<FolderRow>();
        foreach (var child in entry.Children)
        {
            var row = BuildRow(child, onlyDifferences, depth + 1, exclusions);
            if (row is not null) children.Add(row);
        }
        if ((onlyDifferences && !entry.HasDifference && children.Count == 0) ||
            (entry.IsDirectory && entry.Children.Count > 0 && children.Count == 0)) return null;
        var palette = ThemeService.Palette;
        var result = new FolderRow
        {
            Entry = entry,
            Name = entry.Name,
            StatusText = StatusLabel(entry.Status),
            TypeText = entry.IsDirectory ? "folder" : entry.Extension.TrimStart('.'),
            IconGlyph = IconFor(entry),
            IsDirectory = entry.IsDirectory,
            StatusFill = entry.Status == FolderEntryStatus.Same ? Brushes.Transparent : palette.FillFor(entry.Status),
            StatusStroke = entry.Status == FolderEntryStatus.Same ? ThemeService.Brush("TextDisabled") : palette.StrokeFor(entry.Status),
            Indent = new Thickness(depth * 16, 0, 0, 0),
            ExpanderVisibility = children.Count > 0 ? Visibility.Visible : Visibility.Hidden,
            LeftSize = entry.Left is null || entry.IsDirectory ? string.Empty : $"{entry.LeftLength:N0}",
            RightSize = entry.Right is null || entry.IsDirectory ? string.Empty : $"{entry.RightLength:N0}",
            LeftModified = entry.Left is null ? string.Empty : entry.Left.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            RightModified = entry.Right is null ? string.Empty : entry.Right.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            Tooltip = entry.Message is null ? entry.RelativePath : $"{entry.RelativePath}\n{entry.Message}"
        };
        result.Children.AddRange(children);
        return result;
    }

    private static string IconFor(FolderEntry entry)
    {
        if (entry.IsDirectory) return "\uE8B7";
        return entry.Extension.ToLowerInvariant() switch
        {
            ".cs" or ".csproj" or ".sln" or ".json" or ".xml" or ".xaml" or ".html" or ".css" or ".js" or ".ts" or ".py" or ".cpp" or ".h" => "\uE943",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" => "\uEB9F",
            ".mp3" or ".wav" or ".flac" or ".mp4" or ".mkv" or ".avi" or ".mov" => "\uE8B1",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "\uF012",
            ".txt" or ".md" or ".log" or ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => "\uE8A5",
            _ => "\uE8A5"
        };
    }

    private static string StatusLabel(FolderEntryStatus status) => status switch
    {
        FolderEntryStatus.Same => "identical",
        FolderEntryStatus.Modified => "modified",
        FolderEntryStatus.LeftOnly => "left only",
        FolderEntryStatus.RightOnly => "right only",
        FolderEntryStatus.TypeConflict => "type clash",
        _ => "error"
    };

    private static string SafeName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length == 0 ? trimmed : name;
    }

    private void SetFooter(string text)
    {
        FooterText.Text = text;
        StatusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(LeftPathBox);

    private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(RightPathBox);

    private void Browse(TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder" };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        target.Text = dialog.FolderName;
        if (ReferenceEquals(target, LeftPathBox) && string.IsNullOrWhiteSpace(RightPathBox.Text)) _ = CompareAsync();
        if (ReferenceEquals(target, RightPathBox) && string.IsNullOrWhiteSpace(LeftPathBox.Text)) _ = CompareAsync();
    }

    private static void SetFolderDropEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async Task HandleFolderDrop(bool left, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        var folder = paths.FirstOrDefault(Directory.Exists) ?? Path.GetDirectoryName(paths.FirstOrDefault(File.Exists));
        if (folder is null) return;
        await OpenAsync(left ? folder : null, left ? null : folder);
    }

    private async void Compare_Click(object sender, RoutedEventArgs e) => await CompareAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _comparison?.Cancel();

    private async void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (_result is not null) await CompareAsync();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => RebuildTree();

    private async void Exclude_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_result is not null) await CompareAsync();
    }

    private void Expand_Click(object sender, RoutedEventArgs e) => SetExpansion(true);

    private void Collapse_Click(object sender, RoutedEventArgs e) => SetExpansion(false);

    private void SetExpansion(bool expanded)
    {
        foreach (var item in Tree.Items)
        {
            if (Tree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container) SetExpansionRecursive(container, expanded);
        }
    }

    private static void SetExpansionRecursive(TreeViewItem item, bool expanded)
    {
        item.IsExpanded = expanded;
        item.UpdateLayout();
        foreach (var child in item.Items)
        {
            if (item.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem container) SetExpansionRecursive(container, expanded);
        }
    }

    private void MoveSelection(int direction)
    {
        var flat = Flatten(Tree.ItemsSource as IEnumerable<FolderRow> ?? []).Where(row => row.Entry.HasDifference && !row.Entry.IsDirectory).ToList();
        if (flat.Count == 0) return;
        var current = Tree.SelectedItem as FolderRow;
        var index = current is null ? -1 : flat.IndexOf(current);
        index += direction;
        if (index < 0) index = flat.Count - 1;
        if (index >= flat.Count) index = 0;
        SelectRow(flat[index]);
    }

    private static IEnumerable<FolderRow> Flatten(IEnumerable<FolderRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            foreach (var child in Flatten(row.Children)) yield return child;
        }
    }

    private void SelectRow(FolderRow row)
    {
        var container = FindContainer(Tree, row);
        if (container is null) return;
        container.IsSelected = true;
        container.BringIntoView();
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, FolderRow row)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            if (ReferenceEquals(item, row)) return container;
            container.IsExpanded = true;
            container.UpdateLayout();
            var nested = FindContainer(container, row);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenSelected();
        e.Handled = true;
    }

    /// <summary>Opens the selected pair in a file comparison tab.</summary>
    private void OpenSelected()
    {
        if (Tree.SelectedItem is not FolderRow row || _result is null || row.Entry.IsDirectory) return;
        var entry = row.Entry;
        if (entry.Status == FolderEntryStatus.TypeConflict)
        {
            MessageBox.Show(Window.GetWindow(this), "One side is a file and the other a folder, so they cannot be compared as text.",
                "NaraDiff", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var left = entry.Left?.FullPath ?? Path.Combine(_result.LeftPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var right = entry.Right?.FullPath ?? Path.Combine(_result.RightPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        FileComparisonRequested?.Invoke(this, (left, right));
    }

    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            SetFooter("Compare the folders before synchronising them.");
            return;
        }
        var window = new SyncPreviewWindow(_result, _settings.SyncOptions, _logger) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        _settings.SyncOptions = window.ResultOptions;
        SetFooter(window.Summary);
        _ = CompareAsync();
    }
}
