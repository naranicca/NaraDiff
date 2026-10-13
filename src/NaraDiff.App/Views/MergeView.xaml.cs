using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using NaraDiff.App.Controls;
using NaraDiff.App.Services;
using NaraDiff.Core.Diff;
using NaraDiff.Core.Merge;
using NaraDiff.Core.Services;
using NaraDiff.Core.Settings;
using NaraDiff.Core.Text;
using NaraDiff.Infrastructure.IO;
using NaraDiff.Infrastructure.Logging;

namespace NaraDiff.App.Views;

/// <summary>
/// The three way merge: base in the middle, the two variants beside it, and the merged result
/// underneath. Regions that only one side changed merge automatically; the rest are conflicts that
/// have to be resolved with the buttons, the gutter arrows or by editing the result directly.
/// </summary>
public partial class MergeView : UserControl, IComparisonView, IDisposable
{
    private readonly ITextFileService _files = new TextFileService();
    private readonly FileLogger _logger;
    private AppSettings _settings;
    private DiffOptions _options;
    private MergeResult? _merge;
    private MergedDocument? _document;
    private CancellationTokenSource? _work;
    private DiffTextEditor? _scrollLeader;
    private int _programmaticScrolls;
    private bool _realignPending;
    private bool _realignForced;
    private bool _appliedWordWrap;
    private MergeNavigation? _pendingNavigation;
    private readonly PaneFollower _baseFollower = new();
    private readonly PaneFollower _leftFollower = new();
    private readonly PaneFollower _rightFollower = new();
    private readonly Dictionary<DiffTextEditor, double> _lastOffsets = [];
    private bool _loading;
    private bool _resultEdited;
    private bool _disposed;

    public MergeView(AppSettings settings, FileLogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
        _options = settings.DiffOptions.Sanitized();
        InitializeComponent();

        LeftHeader.BrowseRequested += (_, _) => Browse(MergePane.Left);
        BaseHeader.BrowseRequested += (_, _) => Browse(MergePane.Base);
        RightHeader.BrowseRequested += (_, _) => Browse(MergePane.Right);
        LeftHeader.ReloadRequested += async (_, _) => await ReloadAsync(MergePane.Left);
        BaseHeader.ReloadRequested += async (_, _) => await ReloadAsync(MergePane.Base);
        RightHeader.ReloadRequested += async (_, _) => await ReloadAsync(MergePane.Right);
        LeftHeader.PathCommitted += async (_, path) => await LoadAsync(MergePane.Left, path);
        BaseHeader.PathCommitted += async (_, path) => await LoadAsync(MergePane.Base, path);
        RightHeader.PathCommitted += async (_, path) => await LoadAsync(MergePane.Right, path);
        LeftHeader.SaveRequested += async (_, _) => await SaveAsync();
        BaseHeader.SaveRequested += async (_, _) => await SaveAsync();
        RightHeader.SaveRequested += async (_, _) => await SaveAsync();

        LeftConnector.LeftEditor = LeftEditor;
        LeftConnector.RightEditor = BaseEditor;
        RightConnector.LeftEditor = BaseEditor;
        RightConnector.RightEditor = RightEditor;
        LeftConnector.Action += (_, e) => ResolveFromConnector(e, MergePane.Left);
        RightConnector.Action += (_, e) => ResolveFromConnector(e, MergePane.Right);
        LeftConnector.LinkActivated += (_, link) => { if (link.Tag is MergeRegion region) GoToRegion(region); };
        RightConnector.LinkActivated += (_, link) => { if (link.Tag is MergeRegion region) GoToRegion(region); };
        Ruler.Editor = BaseEditor;
        Ruler.LineRequested += (_, line) => ScrollAllToBaseLine(line);

        foreach (var editor in new[] { LeftEditor, BaseEditor, RightEditor, ResultEditor })
        {
            var pane = editor;
            editor.ViewChanged += Editor_ViewChanged;
            editor.TextArea.TextView.VisualLinesChanged += (_, _) => ScheduleRealign();
            editor.CaretLineChanged += (_, _) => UpdateFooter();
            // Whoever the user touches last leads the synchronised scrolling, and touching a pane
            // ends any jump that was still being re-applied while word-wrapped lines got measured.
            editor.PreviewMouseWheel += (_, _) => TakeLead(pane);
            editor.PreviewMouseDown += (_, _) => TakeLead(pane);
            editor.PreviewKeyDown += (_, _) => TakeLead(pane);
            editor.GotKeyboardFocus += (_, _) => TakeLead(pane);
        }
        foreach (var target in new[] { MergePane.Left, MergePane.Base, MergePane.Right })
        {
            var droppedOn = target;
            var editor = EditorFor(target);
            editor.AllowDrop = true;
            editor.PreviewDragEnter += (_, e) => SetFileDropEffect(e);
            editor.PreviewDragOver += (_, e) => SetFileDropEffect(e);
            editor.PreviewDrop += (_, e) => HandleFileDrop(droppedOn, e);
        }
        LeftEditor.IsReadOnly = true;
        BaseEditor.IsReadOnly = true;
        RightEditor.IsReadOnly = true;
        ResultEditor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _resultEdited = true;
            UpdateFooter();
        };
        ThemeService.Changed += OnThemeChanged;
        ApplySettings(settings);
    }

    public event EventHandler? TitleChanged;

    public event EventHandler? StatusChanged;

    public string Title
    {
        get
        {
            var name = Path.GetFileName(BaseEditor.FilePath) ?? Path.GetFileName(OutputPathBox.Text);
            return string.IsNullOrEmpty(name) ? "Merge" : $"Merge {name}";
        }
    }

    public string StatusText { get; private set; } = "No merge yet";

    public bool HasUnsavedChanges => ResultEditor.IsModified || _resultEdited;

    public bool CanApplyChanges => true;

    public async Task OpenAsync(string? basePath, string? leftPath, string? rightPath, string? outputPath = null)
    {
        if (!string.IsNullOrWhiteSpace(basePath)) await LoadAsync(MergePane.Base, basePath!);
        if (!string.IsNullOrWhiteSpace(leftPath)) await LoadAsync(MergePane.Left, leftPath!);
        if (!string.IsNullOrWhiteSpace(rightPath)) await LoadAsync(MergePane.Right, rightPath!);
        OutputPathBox.Text = string.IsNullOrWhiteSpace(outputPath) ? leftPath ?? string.Empty : outputPath!;
        await MergeAsync();
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _options = settings.DiffOptions.Sanitized();
        foreach (var editor in new[] { LeftEditor, BaseEditor, RightEditor, ResultEditor }) editor.ApplyAppearance(settings);
        LeftConnector.ShowRibbons = settings.ShowConnectors;
        RightConnector.ShowRibbons = settings.ShowConnectors;
        UpdateVisuals();
        if (settings.WordWrap != _appliedWordWrap) ScheduleRealign(force: true);
        _appliedWordWrap = settings.WordWrap;
    }

    public void ApplyDiffOptions(DiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Sanitized();
        _ = MergeAsync();
    }

    public Task RefreshAsync() => ReloadAllAsync();

    public async Task<bool> SaveAsync()
    {
        var path = OutputPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            var dialog = new SaveFileDialog { Title = "Save the merged result" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
            path = dialog.FileName;
            OutputPathBox.Text = path;
        }
        if (_merge is not null && _merge.UnresolvedConflictCount > 0 &&
            MessageBox.Show(Window.GetWindow(this),
                $"{_merge.UnresolvedConflictCount} conflicts are still unresolved and will be written with the base version. Save anyway?",
                "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return false;
        var lines = ResultEditor.GetLines();
        var result = await _files.SaveAsync(new FileSaveRequest
        {
            Path = path,
            Lines = lines,
            Encoding = ResultEditor.EncodingChoice,
            LineEnding = LineEndingKind.None,
            CreateBackup = false
        });
        if (!result.Succeeded)
        {
            MessageBox.Show(Window.GetWindow(this), result.Error ?? "The merged file could not be saved.", "NaraDiff", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        ResultEditor.IsModified = false;
        _resultEdited = false;
        SetNotice($"Saved {Path.GetFileName(result.Path)} ({result.Length:N0} bytes).");
        UpdateFooter();
        TitleChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void NextChange() => NavigateConflict(1, onlyConflicts: false);

    public void PreviousChange() => NavigateConflict(-1, onlyConflicts: false);

    public void ApplyToRight() => Resolve(CurrentRegion(), MergeResolution.Right);

    public void ApplyToLeft() => Resolve(CurrentRegion(), MergeResolution.Left);

    public void ApplyAllToRight() => ResolveAll(MergeResolution.Right);

    public void ApplyAllToLeft() => ResolveAll(MergeResolution.Left);

    /// <summary>Toolbar and shortcut entry points used by the main window.</summary>
    public void NextConflict() => NavigateConflict(1, onlyConflicts: true);

    public void PreviousConflict() => NavigateConflict(-1, onlyConflicts: true);

    public void TakeLeft() => Resolve(CurrentRegion(), MergeResolution.Left);

    public void TakeRight() => Resolve(CurrentRegion(), MergeResolution.Right);

    public void TakeBase() => Resolve(CurrentRegion(), MergeResolution.Base);

    public void TakeBoth() => Resolve(CurrentRegion(), MergeResolution.LeftThenRight);

    public void FocusSearch() => ResultEditor.Focus();

    public void Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeService.Changed -= OnThemeChanged;
        _work?.Cancel();
        _work?.Dispose();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        foreach (var editor in new[] { LeftEditor, BaseEditor, RightEditor, ResultEditor }) editor.ApplyAppearance(_settings);
        UpdateVisuals();
    }

    // ---------- loading ----------

    private DiffTextEditor EditorFor(MergePane pane) => pane switch
    {
        MergePane.Left => LeftEditor,
        MergePane.Base => BaseEditor,
        MergePane.Right => RightEditor,
        _ => ResultEditor
    };

    private FilePaneHeader HeaderFor(MergePane pane) => pane switch
    {
        MergePane.Left => LeftHeader,
        MergePane.Base => BaseHeader,
        _ => RightHeader
    };

    private void Browse(MergePane pane)
    {
        var dialog = new OpenFileDialog { Title = $"Select the {pane.ToString().ToLowerInvariant()} file", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _ = LoadAsync(pane, dialog.FileName);
    }

    private static void SetFileDropEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void HandleFileDrop(MergePane pane, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        var files = paths.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            SetNotice("Folders can't be dropped here; use folder compare to compare directories.");
            return;
        }
        _ = DropFileAsync(pane, files);
    }

    private async Task DropFileAsync(MergePane pane, string[] files)
    {
        if (files.Length == 1)
        {
            await LoadAsync(pane, files[0]);
        }
        await LoadAsync(MergePane.Base, files[0]);
        await LoadAsync(MergePane.Left, files[1]);
        if (files.Length >= 3) await LoadAsync(MergePane.Right, files[2]);
    }

    private async Task LoadAsync(MergePane pane, string path, EncodingChoice? encoding = null)
    {
        var editor = EditorFor(pane);
        var header = HeaderFor(pane);
        var result = await _files.LoadAsync(path, encoding);
        if (result.Failed)
        {
            header.PathText = path;
            header.SetState(result.Error ?? "The file could not be loaded.", false, false);
            SetNotice($"{Path.GetFileName(path)}: {result.Error}");
            return;
        }
        if (result.Content.IsBinary)
        {
            SetNotice($"{Path.GetFileName(path)} is binary and cannot take part in a three way merge.");
            return;
        }
        ForgetAlignment();
        _loading = true;
        editor.FilePath = result.Path;
        editor.EncodingChoice = result.Content.Encoding;
        editor.LineEnding = result.Content.LineEnding;
        editor.SetContent(result.Content.Text);
        editor.IsReadOnly = true;
        header.PathText = result.Path;
        header.SetEncoding(result.Content.Encoding);
        header.SetState($"{result.Content.Lines.Count:N0} lines, {LineEndings.DisplayName(result.Content.LineEnding)}", true, false);
        header.CanSave = false;
        _settings.RememberFile(result.Path);
        _loading = false;
        if (pane == MergePane.Left && string.IsNullOrWhiteSpace(OutputPathBox.Text)) OutputPathBox.Text = result.Path;
        if (pane == MergePane.Base) ResultEditor.EncodingChoice = result.Content.Encoding;
        TitleChanged?.Invoke(this, EventArgs.Empty);
        await MergeAsync();
    }

    private async Task ReloadAsync(MergePane pane)
    {
        var editor = EditorFor(pane);
        if (string.IsNullOrEmpty(editor.FilePath)) return;
        await LoadAsync(pane, editor.FilePath!, editor.EncodingChoice);
    }

    private async Task ReloadAllAsync()
    {
        foreach (var pane in new[] { MergePane.Base, MergePane.Left, MergePane.Right }) await ReloadAsync(pane);
        await MergeAsync();
    }

    // ---------- merging ----------

    private async Task MergeAsync()
    {
        if (_disposed) return;
        if (BaseEditor.Document is null || LeftEditor.Document is null || RightEditor.Document is null) return;
        if (string.IsNullOrEmpty(BaseEditor.FilePath) && string.IsNullOrEmpty(LeftEditor.FilePath) && string.IsNullOrEmpty(RightEditor.FilePath)) return;
        _work?.Cancel();
        _work?.Dispose();
        var source = new CancellationTokenSource();
        _work = source;
        try
        {
            var result = await ThreeWayMerger.MergeAsync(BaseEditor.GetLines(), LeftEditor.GetLines(), RightEditor.GetLines(), _options, source.Token);
            if (source.IsCancellationRequested) return;
            _merge = result;
            ForgetAlignment();
            _resultEdited = false;
            RegenerateResult();
            UpdateVisuals();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("merge", ex);
            SetNotice($"The merge failed: {ex.Message}");
        }
    }

    private void RegenerateResult()
    {
        if (_merge is null) return;
        var fallback = BaseEditor.LineEnding is LineEndingKind.None or LineEndingKind.Mixed ? LineEndingKind.Lf : BaseEditor.LineEnding;
        _document = _merge.Build(fallback);
        ForgetAlignment();
        var offset = ResultEditor.TextArea.TextView.ScrollOffset.Y;
        _loading = true;
        ResultEditor.SetContent(LineEndings.Join(_document.Lines));
        ResultEditor.ScrollToVerticalOffset(offset);
        _loading = false;
        _resultEdited = false;
    }

    private void UpdateVisuals()
    {
        if (_merge is null) return;
        var palette = ThemeService.Palette;
        LeftEditor.Decorations = DiffDecorationSet.FromMerge(_merge, MergePane.Left, palette);
        BaseEditor.Decorations = DiffDecorationSet.FromMerge(_merge, MergePane.Base, palette);
        RightEditor.Decorations = DiffDecorationSet.FromMerge(_merge, MergePane.Right, palette);
        var leftLinks = new List<ConnectorLink>();
        var rightLinks = new List<ConnectorLink>();
        var marks = new List<OverviewMark>();
        foreach (var region in _merge.Regions)
        {
            if (region.Kind == MergeRegionKind.Unchanged) continue;
            var unresolved = region.IsConflict && !region.IsResolved;
            var fill = unresolved ? palette.ConflictRibbon : palette.RibbonFor(DiffBlockKind.Modify);
            var stroke = unresolved ? palette.ConflictStroke : palette.StrokeFor(region.Kind);
            leftLinks.Add(new ConnectorLink
            {
                LeftStart = region.LeftStart,
                LeftCount = region.LeftCount,
                RightStart = region.BaseStart,
                RightCount = region.BaseCount,
                Fill = fill,
                Stroke = stroke,
                AllowToRight = region.LeftCount > 0 || region.IsConflict,
                IsConflict = unresolved,
                Tooltip = Describe(region, "left"),
                Tag = region
            });
            rightLinks.Add(new ConnectorLink
            {
                LeftStart = region.BaseStart,
                LeftCount = region.BaseCount,
                RightStart = region.RightStart,
                RightCount = region.RightCount,
                Fill = fill,
                Stroke = stroke,
                AllowToLeft = region.RightCount > 0 || region.IsConflict,
                IsConflict = unresolved,
                Tooltip = Describe(region, "right"),
                Tag = region
            });
            marks.Add(new OverviewMark
            {
                Start = region.BaseStart,
                Count = Math.Max(1, region.BaseCount),
                Brush = stroke,
                Emphasis = unresolved
            });
        }
        LeftConnector.SetLinks(leftLinks);
        RightConnector.SetLinks(rightLinks);
        Ruler.SetMarks(marks);
        UpdateFooter();
    }

    private static string Describe(MergeRegion region, string side)
    {
        var kind = region.Kind switch
        {
            MergeRegionKind.LeftChange => "Changed on the left only, merged automatically",
            MergeRegionKind.RightChange => "Changed on the right only, merged automatically",
            MergeRegionKind.SameChange => "Changed identically on both sides",
            MergeRegionKind.Conflict => region.IsResolved ? $"Conflict resolved with {region.Resolution}" : "Conflict: both sides changed these lines",
            _ => "Unchanged"
        };
        var action = side == "left" ? "Click the arrow to take the left version." : "Click the arrow to take the right version.";
        return $"{kind}\nBase lines {region.BaseStart + 1}-{Math.Max(region.BaseStart + 1, region.BaseEnd)}\n{action}";
    }

    // ---------- resolution ----------

    private MergeRegion? CurrentRegion()
    {
        if (_merge is null || _merge.Regions.Count == 0) return null;
        var baseLine = CurrentBaseLine();
        return _merge.Regions.FirstOrDefault(region => region.Kind != MergeRegionKind.Unchanged && baseLine >= region.BaseStart && baseLine < Math.Max(region.BaseStart + 1, region.BaseEnd))
               ?? _merge.Regions.FirstOrDefault(region => region.Kind != MergeRegionKind.Unchanged && region.BaseStart >= baseLine)
               ?? _merge.Regions.LastOrDefault(region => region.Kind != MergeRegionKind.Unchanged);
    }

    private int CurrentBaseLine()
    {
        if (_merge is null) return 0;
        if (LeftEditor.TextArea.IsKeyboardFocusWithin) return (int)Math.Round(_merge.LeftDiff.MapRightToLeft(LeftEditor.CaretLineIndex));
        if (RightEditor.TextArea.IsKeyboardFocusWithin) return (int)Math.Round(_merge.RightDiff.MapRightToLeft(RightEditor.CaretLineIndex));
        if (ResultEditor.TextArea.IsKeyboardFocusWithin) return ResultLineToBase(ResultEditor.CaretLineIndex);
        return BaseEditor.CaretLineIndex;
    }

    private int ResultLineToBase(int resultLine)
    {
        if (_merge is null || _document is null) return resultLine;
        foreach (var region in _merge.Regions)
        {
            if (!_document.RegionRanges.TryGetValue(region.Index, out var range)) continue;
            if (resultLine >= range.Start && resultLine < range.Start + Math.Max(1, range.Count)) return region.BaseStart;
        }
        return resultLine;
    }

    private void ResolveFromConnector(ConnectorActionEventArgs e, MergePane pane)
    {
        if (e.Link.Tag is not MergeRegion region) return;
        Resolve(region, pane == MergePane.Left ? MergeResolution.Left : MergeResolution.Right);
    }

    private void Resolve(MergeRegion? region, MergeResolution resolution)
    {
        if (_merge is null || region is null) return;
        if (region.Kind == MergeRegionKind.Unchanged) return;
        if (!ConfirmResultOverwrite()) return;
        region.Resolution = resolution;
        RegenerateResult();
        UpdateVisuals();
        GoToRegion(region);
    }

    private void ResolveAll(MergeResolution resolution)
    {
        if (_merge is null) return;
        var conflicts = _merge.Conflicts.ToList();
        if (conflicts.Count == 0)
        {
            SetNotice("There are no conflicts to resolve.");
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this), $"Resolve {conflicts.Count} conflicts with the {resolution.ToString().ToLowerInvariant()} version?",
                "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (!ConfirmResultOverwrite()) return;
        foreach (var region in conflicts) region.Resolution = resolution;
        RegenerateResult();
        UpdateVisuals();
    }

    private bool ConfirmResultOverwrite()
    {
        if (!_resultEdited) return true;
        return MessageBox.Show(Window.GetWindow(this),
            "The merged result was edited by hand. Applying a resolution rebuilds it and discards those edits. Continue?",
            "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private void NavigateConflict(int direction, bool onlyConflicts)
    {
        if (_merge is null) return;
        var candidates = _merge.Regions
            .Where(region => onlyConflicts ? region.IsConflict : region.Kind != MergeRegionKind.Unchanged)
            .OrderBy(region => region.BaseStart)
            .ToList();
        if (candidates.Count == 0)
        {
            SetNotice(onlyConflicts ? "There are no conflicts." : "There are no differences between the three files.");
            return;
        }
        var baseLine = CurrentBaseLine();
        var region = direction > 0
            ? candidates.FirstOrDefault(item => item.BaseStart > baseLine) ?? candidates[0]
            : candidates.LastOrDefault(item => item.BaseStart < baseLine) ?? candidates[^1];
        GoToRegion(region);
    }

    private void GoToRegion(MergeRegion region)
    {
        BeginProgrammaticScroll();
        BaseEditor.MoveCaretToLine(region.BaseStart);
        Navigate(new MergeNavigation(region.BaseStart, region.LeftStart, region.RightStart));
        if (_document is not null && _document.RegionRanges.TryGetValue(region.Index, out var range)) ResultEditor.ScrollToLineIndex(range.Start);
        RefreshConnectors();
        UpdateFooter();
    }

    // ---------- scrolling ----------

    private void Editor_ViewChanged(object? sender, EventArgs e)
    {
        if (sender is DiffTextEditor source)
        {
            var offset = source.TextArea.TextView.ScrollOffset.Y;
            var previous = _lastOffsets.TryGetValue(source, out var seen) ? seen : offset;
            _lastOffsets[source] = offset;
            var scrolled = Math.Abs(offset - previous) >= 1;
            if (scrolled && _programmaticScrolls == 0) _pendingNavigation = null;
            if (scrolled && _merge is not null && CanSynchronizeFrom(source)) Synchronize(source, previous, offset);
        }
        RefreshConnectors();
    }

    /// <summary>
    /// Only the pane the user is working in drives the other three, and the scrolls that causes are
    /// ignored when their events arrive one layout pass later. Otherwise the panes push each other,
    /// because mapping a position back through a one sided change does not return where it started.
    /// </summary>
    private bool CanSynchronizeFrom(DiffTextEditor source) =>
        _settings.SynchronizeScrolling
        && _programmaticScrolls == 0
        && (_scrollLeader is null || ReferenceEquals(_scrollLeader, source));

    private void Synchronize(DiffTextEditor source, double previousOffset, double offset)
    {
        if (_merge is null || source.LineHeight <= 0) return;
        foreach (var follower in UpperPanes)
        {
            if (ReferenceEquals(follower, source) || follower.LineHeight <= 0) continue;
            var target = FollowerFor(follower).Step(source, follower, previousOffset, offset, sourceOffset => CanonicalOffset(source, follower, sourceOffset));
            ScrollPane(follower, target);
        }
    }

    private DiffTextEditor[] UpperPanes => [BaseEditor, LeftEditor, RightEditor];

    private PaneFollower FollowerFor(DiffTextEditor pane) =>
        ReferenceEquals(pane, BaseEditor) ? _baseFollower : ReferenceEquals(pane, LeftEditor) ? _leftFollower : _rightFollower;

    private double CanonicalOffset(DiffTextEditor source, DiffTextEditor target, double sourceOffset)
    {
        var merge = _merge!;
        var fraction = source.GetSyncFraction(sourceOffset);
        var sourceLine = source.GetLineAtViewportFraction(sourceOffset, fraction);
        var bias = fraction;
        double baseLine;
        if (ReferenceEquals(source, BaseEditor)) baseLine = sourceLine;
        else if (ReferenceEquals(source, LeftEditor)) baseLine = merge.LeftDiff.MapRightToLeft(sourceLine, fraction);
        else if (ReferenceEquals(source, RightEditor)) baseLine = merge.RightDiff.MapRightToLeft(sourceLine, fraction);
        else (baseLine, bias) = ResultToBase(sourceLine, fraction);
        var line = ReferenceEquals(target, BaseEditor) ? baseLine
            : ReferenceEquals(target, LeftEditor) ? merge.LeftDiff.MapLeftToRight(baseLine, bias)
            : merge.RightDiff.MapLeftToRight(baseLine, bias);
        var levelY = fraction * (ReferenceEquals(source, ResultEditor) ? BaseEditor.ScrollViewportHeight : source.ScrollViewportHeight);
        return target.GetOffsetPlacingLineAt(line, levelY);
    }

    private (double Line, double Bias) ResultToBase(double resultLine, double fraction)
    {
        if (_merge is null || _document is null) return (resultLine, fraction);
        const double tolerance = 1e-9;
        double resultEnd = 0, baseEnd = 0;
        foreach (var region in _merge.Regions)
        {
            if (!_document.RegionRanges.TryGetValue(region.Index, out var range)) continue;
            if (range.Count == 0)
            {
                if (Math.Abs(resultLine - range.Start) < tolerance) return (region.BaseStart + fraction * region.BaseCount, fraction);
            }
            else if (resultLine < range.Start + range.Count)
            {
                var through = Math.Clamp((resultLine - range.Start) / range.Count, 0, 1);
                return region.BaseCount > 0 ? (region.BaseStart + through * region.BaseCount, fraction) : (region.BaseStart, through);
            }
            resultEnd = range.Start + range.Count;
            baseEnd = region.BaseStart + region.BaseCount;
        }
        return (baseEnd + (resultLine - resultEnd), fraction);
    }

    private void ScrollPane(DiffTextEditor pane, double offset)
    {
        if (pane.LineHeight <= 0 || Math.Abs(pane.TextArea.TextView.ScrollOffset.Y - offset) < 1) return;
        BeginProgrammaticScroll();
        pane.ScrollToVerticalOffset(offset);
    }

    private void TakeLead(DiffTextEditor pane)
    {
        _scrollLeader = pane;
        _pendingNavigation = null;
        foreach (var other in new[] { LeftEditor, BaseEditor, RightEditor, ResultEditor })
            if (!ReferenceEquals(other, pane)) other.StopInertia();
    }

    private void ForgetAlignment()
    {
        _pendingNavigation = null;
        _baseFollower.Reset();
        _leftFollower.Reset();
        _rightFollower.Reset();
    }

    private sealed record MergeNavigation(double BaseLine, double LeftLine, double RightLine);

    private void Navigate(MergeNavigation navigation)
    {
        if (_merge is null) return;
        foreach (var pane in new[] { LeftEditor, BaseEditor, RightEditor, ResultEditor }) pane.StopInertia();
        ForgetAlignment();
        _scrollLeader = BaseEditor;
        _pendingNavigation = _settings.WordWrap ? navigation : null;
        var baseOffset = BaseEditor.GetCenteredOffset(navigation.BaseLine);
        var levelY = BaseEditor.GetDocumentYOfLine(navigation.BaseLine) - baseOffset;
        ScrollPane(BaseEditor, baseOffset);
        ScrollPane(LeftEditor, NavigationOffset(LeftEditor, navigation.LeftLine, baseOffset, levelY));
        ScrollPane(RightEditor, NavigationOffset(RightEditor, navigation.RightLine, baseOffset, levelY));
        RefreshConnectors();
    }

    private double NavigationOffset(DiffTextEditor side, double line, double baseOffset, double levelY)
    {
        var offset = CanonicalOffset(BaseEditor, side, baseOffset);
        var shownAt = side.GetDocumentYOfLine(line) - offset;
        return shownAt < 0 || shownAt > side.ScrollViewportHeight - side.LineHeight ? side.GetOffsetPlacingLineAt(line, levelY) : offset;
    }

    private void ScheduleRealign(bool force = false)
    {
        _realignForced |= force;
        if ((!_settings.WordWrap && !_realignForced) || _realignPending || _merge is null) return;
        _realignPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Realign));
    }

    private void Realign()
    {
        _realignPending = false;
        if (_disposed || _merge is null)
        {
            _realignForced = false;
            return;
        }
        if (_programmaticScrolls != 0)
        {
            ScheduleRealign();
            return;
        }
        _realignForced = false;
        if (_pendingNavigation is { } navigation)
        {
            Navigate(navigation);
            return;
        }
        if (_scrollLeader is not { } leader || !CanSynchronizeFrom(leader)) return;
        var offset = leader.TextArea.TextView.ScrollOffset.Y;
        foreach (var follower in UpperPanes)
        {
            if (ReferenceEquals(follower, leader) || !FollowerFor(follower).IsTracking(leader, follower)) continue;
            ScrollPane(follower, FollowerFor(follower).Step(leader, follower, offset, offset, sourceOffset => CanonicalOffset(leader, follower, sourceOffset)));
        }
    }

    /// <summary>Marks scrolls started from code so their delayed events do not synchronise again.</summary>
    private void BeginProgrammaticScroll()
    {
        _programmaticScrolls++;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _programmaticScrolls = Math.Max(0, _programmaticScrolls - 1)));
    }

    private void ScrollAllToBaseLine(int baseLine)
    {
        if (_merge is null) return;
        var line = baseLine + 0.5;
        Navigate(new MergeNavigation(line, _merge.LeftDiff.MapLeftToRight(line, 0.5), _merge.RightDiff.MapLeftToRight(line, 0.5)));
    }

    private void RefreshConnectors()
    {
        LeftConnector.Refresh();
        RightConnector.Refresh();
        Ruler.Refresh();
    }

    // ---------- status ----------

    private void UpdateFooter()
    {
        if (_merge is null)
        {
            FooterText.Text = "Load a base file and the two variants to start the merge.";
            return;
        }
        var conflicts = _merge.ConflictCount;
        var unresolved = _merge.UnresolvedConflictCount;
        ConflictChipText.Text = conflicts == 0 ? "no conflicts" : $"{unresolved} of {conflicts} conflicts open";
        StatusText = $"{_merge.AutomaticMergeCount} automatic merges, {conflicts} conflicts, {unresolved} unresolved" +
                     (_resultEdited ? ", result edited by hand" : string.Empty);
        FooterText.Text = $"base line {CurrentBaseLine() + 1} of {Math.Max(1, BaseEditor.DocumentLineCount)}  ·  result {Math.Max(1, ResultEditor.DocumentLineCount)} lines" +
                          (_resultEdited ? "  ·  edited by hand" : string.Empty);
        FooterRightText.Text = $"{_merge.Regions.Count(region => region.Kind != MergeRegionKind.Unchanged)} regions  ·  {_options.Describe()}";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetNotice(string text)
    {
        NoticeText.Text = text;
        NoticeBar.Visibility = Visibility.Visible;
    }

    private void NoticeDismiss_Click(object sender, RoutedEventArgs e) => NoticeBar.Visibility = Visibility.Collapsed;

    private void PreviousConflict_Click(object sender, RoutedEventArgs e) => NavigateConflict(-1, onlyConflicts: true);

    private void NextConflict_Click(object sender, RoutedEventArgs e) => NavigateConflict(1, onlyConflicts: true);

    private void TakeLeft_Click(object sender, RoutedEventArgs e) => Resolve(CurrentRegion(), MergeResolution.Left);

    private void TakeRight_Click(object sender, RoutedEventArgs e) => Resolve(CurrentRegion(), MergeResolution.Right);

    private void TakeBoth_Click(object sender, RoutedEventArgs e) => Resolve(CurrentRegion(), MergeResolution.LeftThenRight);

    private void TakeBothReversed_Click(object sender, RoutedEventArgs e) => Resolve(CurrentRegion(), MergeResolution.RightThenLeft);

    private void TakeBase_Click(object sender, RoutedEventArgs e) => Resolve(CurrentRegion(), MergeResolution.Base);

    private void AllLeft_Click(object sender, RoutedEventArgs e) => ResolveAll(MergeResolution.Left);

    private void AllRight_Click(object sender, RoutedEventArgs e) => ResolveAll(MergeResolution.Right);

    private void Regenerate_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmResultOverwrite()) return;
        RegenerateResult();
        UpdateVisuals();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Choose where the merged file is written" };
        if (!string.IsNullOrWhiteSpace(OutputPathBox.Text)) dialog.FileName = OutputPathBox.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) OutputPathBox.Text = dialog.FileName;
    }

    private async void SaveResult_Click(object sender, RoutedEventArgs e) => await SaveAsync();
}
