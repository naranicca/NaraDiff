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

/// <summary>A file pair to open, with the names its two sides go by when their paths do not say it.</summary>
/// <param name="GitRoot">The repository the pair comes from in version-control mode, which lets the comparison stage the file.</param>
public sealed record FilePairRequest(string Left, string Right, string? LeftLabel = null, string? RightLabel = null, string? GitRoot = null);

/// <summary>One row of the folder comparison tree.</summary>
public sealed class FolderRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isSelected;

    /// <summary>
    /// Bound two way to the tree item, so the expansion survives the tree being rebuilt (after a stage,
    /// a filter change or a refresh) and is kept for rows whose container has been virtualised away.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>Bound two way to the tree item, so the selection survives the tree being rebuilt.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

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
    private GitRepository? _repository;
    /// <summary>The revision shown on the left in version-control mode.</summary>
    private string _revision = "HEAD";
    private bool _gitBusy;
    /// <summary>The <c>git status</c> of the working tree against HEAD, read with every refresh.</summary>
    private IReadOnlyList<GitStatusEntry> _gitStatus = [];
    private readonly bool _versionControl;
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

    public FolderCompareView(AppSettings settings, FileLogger logger, bool versionControl = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
        _versionControl = versionControl;
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
        var boxes = versionControl ? [(LeftPathBox, true)] : new[] { (LeftPathBox, true), (RightPathBox, false) };
        foreach (var (box, left) in boxes)
        {
            var isLeft = left;
            box.AllowDrop = true;
            box.PreviewDragEnter += (_, e) => SetFolderDropEffect(e);
            box.PreviewDragOver += (_, e) => SetFolderDropEffect(e);
            box.PreviewDrop += async (_, e) => await HandleFolderDrop(isLeft, e);
        }
        if (versionControl) ShowOneFolderOnly();
        LeftPathBox.TextChanged += (_, _) => ShowHint(PendingHint());
        RightPathBox.TextChanged += (_, _) => ShowHint(PendingHint());
        ShowHint(PendingHint());
    }

    private void ShowHint(string text)
    {
        var empty = Tree.ItemsSource is not IEnumerable<FolderRow> rows || !rows.Any();
        EmptyHint.Text = text;
        EmptyHint.Visibility = text.Length > 0 && empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool ReadyToCompare() => _versionControl
        ? Directory.Exists(LeftPathBox.Text.Trim())
        : Directory.Exists(LeftPathBox.Text.Trim()) && Directory.Exists(RightPathBox.Text.Trim());

    private string PendingHint()
    {
        var left = Directory.Exists(LeftPathBox.Text.Trim());
        if (_versionControl) return left ? "Press Enter to show this repository's changes." : "Select a Git repository.";
        var right = Directory.Exists(RightPathBox.Text.Trim());
        return left && right ? "Press Compare to compare these folders."
            : left || right ? "Select the other folder, then press Compare."
            : "Select two folders and press Compare.";
    }

    private void ShowOneFolderOnly()
    {
        LeftLabel.Text = "Repository";
        LeftPathBox.ToolTip = "The Git repository whose changes are shown";
        RightLabel.Visibility = Visibility.Collapsed;
        RightPathBox.Visibility = Visibility.Collapsed;
        RightBrowseButton.Visibility = Visibility.Collapsed;
        RightPathColumn.Width = new GridLength(0);
        CompareActions.Visibility = Visibility.Collapsed;
        FolderOptionsBar.Visibility = Visibility.Collapsed;
        GitBar.Visibility = Visibility.Visible;
        LeftSizeHeader.Text = "Base size";
        LeftModifiedHeader.Text = "Base modified";
        RightSizeHeader.Text = "Working size";
        RightModifiedHeader.Text = "Working modified";
        Tree.ContextMenu = (ContextMenu)Resources["GitMenu"];
        Tree.ContextMenuOpening += Tree_GitMenuOpening;
        Tree.PreviewMouseRightButtonDown += Tree_PreviewMouseRightButtonDown;
        UpdateGitButtons();
    }

    /// <summary>Right-clicking a row selects it first, so the menu acts on the row under the mouse.</summary>
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not TreeViewItem) source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void Tree_GitMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_repository is null || _gitBusy || _result is null)
        {
            e.Handled = true;
            return;
        }
        var files = TargetFiles(out var wholeTree);
        var status = StatusOf(files);
        var target = wholeTree ? "all changes"
            : Tree.SelectedItem is FolderRow { IsDirectory: true } folder ? $"“{folder.Name}/” ({files.Count:N0} files)"
            : $"“{(Tree.SelectedItem as FolderRow)?.Name}”";
        foreach (var item in Tree.ContextMenu!.Items.OfType<MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "Open":
                    item.IsEnabled = Tree.SelectedItem is FolderRow { IsDirectory: false };
                    break;
                case "Stage":
                    item.Header = $"Stage {target}";
                    item.IsEnabled = status.Any(entry => entry.HasUnstagedChanges);
                    break;
                case "Unstage":
                    item.Header = $"Unstage {target}";
                    item.IsEnabled = status.Any(entry => entry.IsStaged);
                    break;
                case "Commit":
                    // With nothing selected the commit window ticks its default set, so no target is named.
                    item.Header = wholeTree ? "Commit…" : $"Commit {target}…";
                    item.IsEnabled = status.Count > 0;
                    break;
                case "Revert":
                    item.Header = $"Revert {target}…";
                    item.IsEnabled = files.Count > 0;
                    break;
            }
        }
    }

    /// <summary>The <c>git status</c> entries behind the given tree files, matching renames by either name.</summary>
    private List<GitStatusEntry> StatusOf(IEnumerable<FolderEntry> files)
    {
        var paths = files.Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);
        return _gitStatus.Where(entry => paths.Contains(entry.Path) || (entry.OriginalPath is not null && paths.Contains(entry.OriginalPath))).ToList();
    }

    private void OpenMenu_Click(object sender, RoutedEventArgs e) => OpenSelected();

    /// <summary>Re-reads what is staged after another view changed the index of <paramref name="root"/>.</summary>
    public async Task RefreshGitStatusAsync(string root)
    {
        if (_repository is null || _gitBusy || !string.Equals(_repository.Root, root, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _gitStatus = await _repository.StatusAsync();
            RebuildTree();
        }
        catch (Exception ex)
        {
            _logger.Error("git-status", ex);
        }
    }

    private async void GitStage_Click(object sender, RoutedEventArgs e) =>
        await ChangeIndexAsync(stage: true);

    private async void GitUnstage_Click(object sender, RoutedEventArgs e) =>
        await ChangeIndexAsync(stage: false);

    private async Task ChangeIndexAsync(bool stage)
    {
        if (_repository is null || _gitBusy) return;
        var repository = _repository;
        var status = StatusOf(TargetFiles(out _));
        var count = status.Count(entry => stage ? entry.HasUnstagedChanges : entry.IsStaged);
        if (count == 0) return;
        SetGitBusy(true);
        try
        {
            if (stage) await repository.StageAsync(status);
            else await repository.UnstageAsync(status);
            _gitStatus = await repository.StatusAsync();
            RebuildTree();
            SetFooter(stage ? $"Staged {count:N0} file(s)." : $"Unstaged {count:N0} file(s).");
        }
        catch (Exception ex)
        {
            _logger.Error(stage ? "git-stage" : "git-unstage", ex);
            ShowGitError(stage ? "The files could not be staged." : "The files could not be unstaged.", ex);
        }
        finally
        {
            SetGitBusy(false);
        }
    }

    public event EventHandler? TitleChanged;

    public event EventHandler? StatusChanged;

    /// <summary>Raised when the user opens a file pair from the tree.</summary>
    public event EventHandler<FilePairRequest>? FileComparisonRequested;

    public string Title
    {
        get
        {
            var left = SafeName(LeftPathBox.Text);
            var right = SafeName(RightPathBox.Text);
            if (_versionControl) return left.Length == 0 ? "Version control" : $"Git: {left}";
            if (left.Length == 0 && right.Length == 0) return "Folders";
            if (left.Length == 0 || right.Length == 0) return left.Length == 0 ? right : left;
            return $"{left} — {right}";
        }
    }

    public string StatusText { get; private set; } = "No folder comparison yet";

    public bool HasUnsavedChanges => false;

    public bool CanApplyChanges => false;

    public async Task OpenAsync(string? leftPath, string? rightPath)
    {
        if (!string.IsNullOrWhiteSpace(leftPath)) LeftPathBox.Text = leftPath;
        if (!string.IsNullOrWhiteSpace(rightPath) && !_versionControl) RightPathBox.Text = rightPath;
        if (ReadyToCompare()) await CompareAsync();
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

    private async Task CompareWorkingTreeAsync(string folder)
    {
        if (_gitBusy) return;
        _gitComparison?.Dispose();
        _gitComparison = null;
        if (!Directory.Exists(folder))
        {
            _repository = null;
            UpdateGitButtons();
            Report("Select the folder of a Git repository.");
            return;
        }
        Report($"Reading the changes of {SafeName(folder)}...");
        SetGitBusy(true);
        try
        {
            var repository = await GitRepository.OpenAsync(folder);
            if (repository is null)
            {
                _repository = null;
                _gitStatus = [];
                _result = null;
                Tree.ItemsSource = null;
                Report($"{SafeName(folder)} is not a Git repository.");
                return;
            }
            if (_repository is null || !string.Equals(_repository.Root, repository.Root, StringComparison.OrdinalIgnoreCase)) _revision = "HEAD";
            _repository = repository;
            await LoadRevisionsAsync();
            _gitComparison = await GitWorktreeComparer.CreateAsync(repository, _revision, CancellationToken.None);
            _gitStatus = await repository.StatusAsync();
        }
        catch (Exception ex)
        {
            // Git may be missing from the machine, the repository may be unreadable, or the revision may be gone.
            _logger.Error("git-compare", ex);
            Report($"The changes of {SafeName(folder)} could not be read: {ex.Message}");
            return;
        }
        finally
        {
            SetGitBusy(false);
        }
        _settings.RememberFolder(folder);
        _result = _gitComparison.Result;
        RebuildTree();
        SetFooter($"Git changes: {RevisionLabel()} — working tree. {StatusText}");
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fills the revision picker, keeping the current choice when it still exists.</summary>
    private async Task LoadRevisionsAsync()
    {
        if (_repository is null) return;
        var revisions = await _repository.ListRevisionsAsync(100);
        _suppressEvents = true;
        try
        {
            RevisionBox.ItemsSource = revisions;
            var selected = revisions.FirstOrDefault(revision => revision.Revision == _revision);
            if (selected is null)
            {
                _revision = "HEAD";
                selected = revisions[0];
            }
            RevisionBox.SelectedItem = selected;
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    /// <summary>A short name for the left revision: a branch or tag name, or an abbreviated hash.</summary>
    private string RevisionLabel()
    {
        foreach (var prefix in new[] { "refs/heads/", "refs/remotes/", "refs/tags/" })
        {
            if (_revision.StartsWith(prefix, StringComparison.Ordinal)) return _revision[prefix.Length..];
        }
        return _revision.Length == 40 ? _revision[..8] : _revision;
    }

    private void SetGitBusy(bool busy)
    {
        _gitBusy = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateGitButtons();
    }

    private void UpdateGitButtons()
    {
        var ready = !_gitBusy && _repository is not null;
        GitCommitButton.IsEnabled = ready;
        PushButton.IsEnabled = ready;
        RevertButton.IsEnabled = ready;
        RevisionBox.IsEnabled = ready;
    }

    private async void Revision_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || RevisionBox.SelectedItem is not GitRevision revision || revision.Revision == _revision) return;
        _revision = revision.Revision;
        await CompareWorkingTreeAsync(LeftPathBox.Text.Trim());
    }

    /// <summary>The changed files of the selected tree row, or of the whole tree when nothing is selected.</summary>
    private List<FolderEntry> TargetFiles(out bool wholeTree)
    {
        wholeTree = Tree.SelectedItem is not FolderRow;
        var root = Tree.SelectedItem is FolderRow row ? row.Entry : _result?.Root;
        if (root is null) return [];
        var files = root.IsDirectory ? root.Descendants().Where(entry => !entry.IsDirectory).ToList() : [root];
        return files.Where(entry => entry.HasDifference).ToList();
    }

    private async void GitCommit_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _gitBusy) return;
        var repository = _repository;
        IReadOnlyList<GitStatusEntry> changes;
        string? branch;
        try
        {
            changes = await repository.StatusAsync();
            branch = await repository.CurrentBranchAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("git-status", ex);
            SetFooter($"The changes could not be read: {ex.Message}");
            return;
        }
        if (changes.Count == 0)
        {
            SetFooter("There is nothing to commit: the working tree matches HEAD.");
            return;
        }
        // By default every change Git already tracks is ticked: modified, deleted and renamed files and
        // everything staged. Untracked files are left unticked so stray files are not committed by accident.
        // Commit from the context menu of a particular file or folder ticks just that target instead.
        var fromMenuTarget = sender is MenuItem && Tree.SelectedItem is FolderRow;
        var preselectedPaths = fromMenuTarget
            ? StatusOf(TargetFiles(out _)).Select(entry => entry.Path).ToHashSet()
            : changes.Where(entry => !entry.IsUntracked).Select(entry => entry.Path).ToHashSet();
        var window = new CommitWindow(branch, changes, preselectedPaths, false) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        var selected = window.SelectedChanges;
        SetGitBusy(true);
        try
        {
            SetFooter($"Committing {selected.Count:N0} file(s)...");
            await repository.CommitAsync(selected, window.Message);
            var summary = $"Committed {selected.Count:N0} file(s).";
            if (window.PushAfterCommit)
            {
                SetFooter(summary + " Pushing...");
                await repository.PushAsync();
                summary += " Pushed.";
            }
            SetGitBusy(false);
            await CompareWorkingTreeAsync(repository.Root);
            SetFooter(summary);
        }
        catch (Exception ex)
        {
            _logger.Error("git-commit", ex);
            SetGitBusy(false);
            ShowGitError("The commit could not be completed.", ex);
            await CompareWorkingTreeAsync(repository.Root);
        }
    }

    private async void GitPush_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _gitBusy) return;
        SetGitBusy(true);
        SetFooter("Pushing...");
        try
        {
            var output = await _repository.PushAsync();
            var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            SetFooter(string.IsNullOrEmpty(last) ? "Pushed." : $"Pushed. {last}");
            await LoadRevisionsAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("git-push", ex);
            SetFooter("The push failed.");
            ShowGitError("The push failed.", ex);
        }
        finally
        {
            SetGitBusy(false);
        }
    }

    private async void GitRevert_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _gitBusy || _result is null) return;
        var repository = _repository;
        var files = TargetFiles(out var wholeTree);
        if (files.Count == 0)
        {
            SetFooter("There are no changes to revert.");
            return;
        }
        var restore = files.Where(entry => entry.Status != FolderEntryStatus.RightOnly).Select(entry => entry.RelativePath).ToList();
        var remove = files.Where(entry => entry.Status == FolderEntryStatus.RightOnly).Select(entry => entry.RelativePath).ToList();
        var listing = string.Join("\n", files.Take(12).Select(entry => $"  {entry.RelativePath}  ({StatusLabel(entry.Status)})"));
        if (files.Count > 12) listing += $"\n  … and {files.Count - 12:N0} more";
        var scope = wholeTree ? "all changes" : $"the changes of {files.Count:N0} file(s)";
        var message = $"Revert {scope} to {RevisionLabel()}?\n\n{listing}\n\n" +
                      (remove.Count > 0 ? $"{remove.Count:N0} file(s) that do not exist in {RevisionLabel()} will be moved to the Recycle Bin.\n" : string.Empty) +
                      "Changes to the other files are discarded and cannot be undone.";
        if (MessageBox.Show(Window.GetWindow(this), message, "Revert changes", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        SetGitBusy(true);
        try
        {
            await repository.RevertAsync(_revision, restore, remove);
            SetGitBusy(false);
            await CompareWorkingTreeAsync(repository.Root);
            SetFooter($"Reverted {files.Count:N0} file(s) to {RevisionLabel()}.");
        }
        catch (Exception ex)
        {
            _logger.Error("git-revert", ex);
            SetGitBusy(false);
            ShowGitError("The changes could not be reverted.", ex);
            await CompareWorkingTreeAsync(repository.Root);
        }
    }

    private void ShowGitError(string summary, Exception ex) =>
        MessageBox.Show(Window.GetWindow(this), $"{summary}\n\n{ex.Message}", "NaraDiff", MessageBoxButton.OK, MessageBoxImage.Error);

    private async Task CompareAsync()
    {
        var left = LeftPathBox.Text.Trim();
        var right = RightPathBox.Text.Trim();
        if (_versionControl)
        {
            await CompareWorkingTreeAsync(left);
            return;
        }
        if (!Directory.Exists(left) || !Directory.Exists(right))
        {
            Report("Both folders must exist before they can be compared.");
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
        Report($"Comparing {SafeName(left)} and {SafeName(right)}...");
        var progress = new Progress<string>(path => SetFooter($"Scanning {path}"));
        try
        {
            _result = await DirectoryComparer.CompareAsync(left, right, options, _diffOptions, progress, source.Token);
            RebuildTree();
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Report("The comparison was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("folder-compare", ex);
            Report($"The folders could not be compared: {ex.Message}");
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
        // The folder options bar is hidden for a repository, so its exclusions must not hide changes there:
        // Revert and Commit act on what the tree shows, and an invisible file would be acted on unseen.
        var exclusions = _versionControl
            ? new GlobMatcher([], true)
            : new GlobMatcher(CurrentOptions().ExcludePatterns, CurrentOptions().CaseSensitiveNames);
        foreach (var child in _result.Root.Children)
        {
            var row = BuildRow(child, onlyDifferences, 0, exclusions);
            if (row is not null) rows.Add(row);
        }
        // Carry the folded folders and the selection over to the new rows by path.
        var previous = Flatten(Tree.ItemsSource as IEnumerable<FolderRow> ?? []).ToList();
        var collapsed = previous.Where(row => row.IsDirectory && !row.IsExpanded).Select(row => row.Entry.RelativePath).ToHashSet(StringComparer.Ordinal);
        var selected = previous.FirstOrDefault(row => row.IsSelected)?.Entry.RelativePath;
        foreach (var row in Flatten(rows))
        {
            if (collapsed.Contains(row.Entry.RelativePath)) row.IsExpanded = false;
            if (selected is not null && row.Entry.RelativePath == selected) row.IsSelected = true;
        }
        Tree.ItemsSource = rows;
        ShowHint(rows.Count == 0
            ? _result.AreIdentical ? (_versionControl ? "This repository has no changes." : "The folders are identical.") : "No entry matches the current filter."
            : string.Empty);
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
            StatusText = StageSuffix(entry) is { Length: > 0 } stage ? $"{StatusLabel(entry.Status)}, {stage}" : StatusLabel(entry.Status),
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

    /// <summary>"staged" or "partly staged" for a file of a repository; empty otherwise.</summary>
    private string StageSuffix(FolderEntry entry)
    {
        if (!_versionControl || entry.IsDirectory || _gitStatus.Count == 0) return string.Empty;
        var status = _gitStatus.FirstOrDefault(item => item.Path == entry.RelativePath || item.OriginalPath == entry.RelativePath);
        return status?.StageText ?? string.Empty;
    }

    private string StatusLabel(FolderEntryStatus status) => status switch
    {
        FolderEntryStatus.Same => "identical",
        FolderEntryStatus.Modified => "modified",
        FolderEntryStatus.LeftOnly => _versionControl ? "deleted" : "left only",
        FolderEntryStatus.RightOnly => _versionControl ? "added" : "right only",
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

    private void Report(string text)
    {
        SetFooter(text);
        ShowHint(text);
    }

    private void SetFooter(string text)
    {
        FooterText.Text = text;
        StatusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Path_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await CompareAsync();
    }

    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(LeftPathBox);

    private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(RightPathBox);

    private void Browse(TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = _versionControl ? "Select a Git repository" : "Select a folder" };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        target.Text = dialog.FolderName;
        if (ReadyToCompare()) _ = CompareAsync();
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

    /// <summary>Folds or unfolds every folder; the rows carry the state, so virtualised items follow too.</summary>
    private void SetExpansion(bool expanded)
    {
        foreach (var row in Flatten(Tree.ItemsSource as IEnumerable<FolderRow> ?? []))
        {
            if (row.IsDirectory) row.IsExpanded = expanded;
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
            // Unfold only the folders on the way to the row; the others keep the state the user left them in.
            if (item is not FolderRow folder || !Flatten(folder.Children).Contains(row)) continue;
            folder.IsExpanded = true;
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
        FileComparisonRequested?.Invoke(this, _versionControl
            ? new FilePairRequest(left, right, RevisionLabel(), "working tree", _repository?.Root)
            : new FilePairRequest(left, right));
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
