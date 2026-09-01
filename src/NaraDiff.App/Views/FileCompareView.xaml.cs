using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using NaraDiff.App.Controls;
using NaraDiff.App.Services;
using NaraDiff.Core.Diff;
using NaraDiff.Core.Services;
using NaraDiff.Core.Settings;
using NaraDiff.Core.Text;
using NaraDiff.Infrastructure.IO;
using NaraDiff.Infrastructure.Logging;

namespace NaraDiff.App.Views;

/// <summary>
/// The two file comparison: two editors, the curved connectors between them, the overview ruler and
/// the hunk commands. Comparisons run in the background and are debounced while typing.
/// </summary>
public partial class FileCompareView : UserControl, IComparisonView, IDisposable
{
    private readonly ITextFileService _files = new TextFileService();
    private readonly FileChangeWatcher _watcher = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly FileLogger _logger;
    private readonly List<(int Line, TextSpan Span)> _leftMatches = [];
    private readonly List<(int Line, TextSpan Span)> _rightMatches = [];

    private AppSettings _settings;
    private DiffOptions _options;
    private DiffResult _diff = DiffResult.Empty;
    private CancellationTokenSource? _comparison;
    private byte[]? _leftBytes;
    private byte[]? _rightBytes;
    private DiffTextEditor? _scrollLeader;
    private int _programmaticScrolls;
    private bool _loading;
    private bool _disposed;
    private string? _pendingReloadPath;
    private int _matchIndex = -1;

    public FileCompareView(AppSettings settings, FileLogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
        _options = settings.DiffOptions.Sanitized();
        InitializeComponent();
        LeftEditor.PlaceScrollBarOnTheLeft();

        LeftHeader.BrowseRequested += (_, _) => Browse(true);
        RightHeader.BrowseRequested += (_, _) => Browse(false);
        LeftHeader.ReloadRequested += async (_, _) => await ReloadAsync(true);
        RightHeader.ReloadRequested += async (_, _) => await ReloadAsync(false);
        LeftHeader.SaveRequested += async (_, _) => await SaveSideAsync(true);
        RightHeader.SaveRequested += async (_, _) => await SaveSideAsync(false);
        LeftHeader.PathCommitted += async (_, path) => await LoadSideAsync(true, path);
        RightHeader.PathCommitted += async (_, path) => await LoadSideAsync(false, path);
        LeftHeader.EncodingChanged += async (_, choice) => await ReinterpretAsync(true, choice);
        RightHeader.EncodingChanged += async (_, choice) => await ReinterpretAsync(false, choice);
        LeftHeader.LineEndingChanged += (_, _) => UpdateHeaders();
        RightHeader.LineEndingChanged += (_, _) => UpdateHeaders();

        Connector.LeftEditor = LeftEditor;
        Connector.RightEditor = RightEditor;
        Connector.Action += Connector_Action;
        Connector.LinkActivated += (_, link) => { if (link.Tag is DiffBlock block) GoToBlock(block); };
        Ruler.Editor = RightEditor;
        Ruler.LineRequested += (_, line) => ScrollBothToRightLine(line);

        foreach (var editor in new[] { LeftEditor, RightEditor })
        {
            var pane = editor;
            var isLeftPane = ReferenceEquals(pane, LeftEditor);
            editor.ViewChanged += Editor_ViewChanged;
            editor.TextChanged += Editor_TextChanged;
            editor.CaretLineChanged += (_, _) => UpdateFooter();
            // Whoever the user touches last leads the synchronised scrolling.
            editor.PreviewMouseWheel += (_, _) => _scrollLeader = pane;
            editor.PreviewMouseDown += (_, _) => _scrollLeader = pane;
            editor.PreviewKeyDown += (_, _) => _scrollLeader = pane;
            editor.GotKeyboardFocus += (_, _) => _scrollLeader = pane;
            editor.AllowDrop = true;
            editor.PreviewDragEnter += (_, e) => SetFileDropEffect(e);
            editor.PreviewDragOver += (_, e) => SetFileDropEffect(e);
            editor.PreviewDrop += (_, e) => HandleFileDrop(isLeftPane, e);
        }
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await CompareAsync(); };
        _watcher.FileChanged += (_, path) => Dispatcher.BeginInvoke(() => OnDiskChange(path));
        ThemeService.Changed += OnThemeChanged;
        ApplySettings(settings);
        UpdateHeaders();
        UpdateFooter();
    }

    public event EventHandler? TitleChanged;

    public event EventHandler? StatusChanged;

    public string Title
    {
        get
        {
            var left = System.IO.Path.GetFileName(LeftEditor.FilePath) ?? "untitled";
            var right = System.IO.Path.GetFileName(RightEditor.FilePath) ?? "untitled";
            return $"{left} — {right}";
        }
    }

    public string StatusText { get; private set; } = "No comparison yet";

    public bool HasUnsavedChanges => LeftEditor.IsModified || RightEditor.IsModified;

    public bool CanApplyChanges => true;

    public DiffResult CurrentDiff => _diff;

    private bool IsBinaryMode => LeftEditor.IsBinaryContent || RightEditor.IsBinaryContent;

    private DiffTextEditor ActiveEditor => RightEditor.TextArea.IsKeyboardFocusWithin ? RightEditor : LeftEditor;

    /// <summary>Loads both sides and runs the first comparison.</summary>
    public async Task OpenAsync(string? leftPath, string? rightPath)
    {
        if (!string.IsNullOrWhiteSpace(leftPath)) await LoadSideAsync(true, leftPath!);
        if (!string.IsNullOrWhiteSpace(rightPath)) await LoadSideAsync(false, rightPath!);
        await CompareAsync();
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _options = settings.DiffOptions.Sanitized();
        LeftEditor.ApplyAppearance(settings);
        RightEditor.ApplyAppearance(settings);
        Connector.ShowRibbons = settings.ShowConnectors;
        _debounce.Interval = TimeSpan.FromMilliseconds(Math.Max(1, settings.DiffDebounceMilliseconds));
        _watcher.IsEnabled = settings.WatchFilesForChanges;
        UpdateVisuals();
    }

    public void ApplyDiffOptions(DiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Sanitized();
        ScheduleCompare(immediate: true);
    }

    public Task RefreshAsync() => ReloadBothAsync();

    public async Task<bool> SaveAsync()
    {
        var saved = true;
        if (LeftEditor.IsModified) saved &= await SaveSideAsync(true);
        if (RightEditor.IsModified) saved &= await SaveSideAsync(false);
        return saved;
    }

    public void NextChange() => Navigate(true);

    public void PreviousChange() => Navigate(false);

    public void ApplyToRight() => ApplyBlocks(BlocksForCommand(), ConnectorDirection.ToRight);

    public void ApplyToLeft() => ApplyBlocks(BlocksForCommand(), ConnectorDirection.ToLeft);

    public void ApplyAllToRight() => ApplyAll(ConnectorDirection.ToRight);

    public void ApplyAllToLeft() => ApplyAll(ConnectorDirection.ToLeft);

    public void FocusSearch()
    {
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeService.Changed -= OnThemeChanged;
        _debounce.Stop();
        _comparison?.Cancel();
        _comparison?.Dispose();
        _watcher.Dispose();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        LeftEditor.ApplyAppearance(_settings);
        RightEditor.ApplyAppearance(_settings);
        UpdateVisuals();
    }

    // ---------- loading and saving ----------

    private void Browse(bool left)
    {
        var dialog = new OpenFileDialog
        {
            Title = left ? "Select the left file" : "Select the right file",
            CheckFileExists = true
        };
        var current = left ? LeftEditor.FilePath : RightEditor.FilePath;
        if (!string.IsNullOrEmpty(current)) dialog.InitialDirectory = System.IO.Path.GetDirectoryName(current);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _ = LoadSideAsync(left, dialog.FileName);
    }

    private static void SetFileDropEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void HandleFileDrop(bool droppedOnLeft, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        var files = paths.Where(System.IO.File.Exists).ToArray();
        if (files.Length == 0)
        {
            ShowNotice("Folders can't be dropped here; use folder comparison to compare directories.", null);
            return;
        }
        if (files.Length >= 2) _ = OpenAsync(files[0], files[1]);
        else _ = LoadSideAsync(droppedOnLeft, files[0]);
    }

    private async Task LoadSideAsync(bool left, string path, EncodingChoice? encoding = null)
    {
        var header = left ? LeftHeader : RightHeader;
        var editor = left ? LeftEditor : RightEditor;
        try
        {
            var result = await _files.LoadAsync(path, encoding);
            if (result.Failed)
            {
                header.PathText = path;
                header.SetState(result.Error ?? "The file could not be loaded.", false, false);
                ShowNotice($"{System.IO.Path.GetFileName(path)}: {result.Error}", null);
                return;
            }
            _loading = true;
            editor.FilePath = result.Path;
            editor.EncodingChoice = result.Content.Encoding;
            editor.LineEnding = result.Content.LineEnding;
            editor.IsBinaryContent = result.Content.IsBinary;
            editor.IsReadOnly = result.IsReadOnly || result.Content. IsBinary;
            if (left) _leftBytes = result.Content.Bytes; else _rightBytes = result.Content.Bytes;
            editor.SetContent(result.Content.IsBinary ? string.Empty : result.Content.Text);
            header.PathText = result.Path;
            header.SetEncoding(result.Content.Encoding);
            _settings.RememberFile(result.Path);
            _loading = false;
            UpdateWatcher();
            UpdateHeaders();
            TitleChanged?.Invoke(this, EventArgs.Empty);
            await CompareAsync();
        }
        catch (Exception ex)
        {
            _loading = false;
            _logger.Error("load", ex);
            ShowNotice($"{System.IO.Path.GetFileName(path)}: {TextFileService.Describe(ex)}", null);
        }
    }

    private async Task ReinterpretAsync(bool left, EncodingChoice choice)
    {
        var editor = left ? LeftEditor : RightEditor;
        if (string.IsNullOrEmpty(editor.FilePath)) return;
        if (editor.IsModified &&
            MessageBox.Show(Window.GetWindow(this), "Reading the file again with another encoding discards the current edits. Continue?",
                "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            (left ? LeftHeader : RightHeader).SetEncoding(editor.EncodingChoice);
            return;
        }
        await LoadSideAsync(left, editor.FilePath!, choice);
    }

    private async Task ReloadAsync(bool left)
    {
        var editor = left ? LeftEditor : RightEditor;
        if (string.IsNullOrEmpty(editor.FilePath)) return;
        await LoadSideAsync(left, editor.FilePath!, editor.EncodingChoice);
    }

    private async Task ReloadBothAsync()
    {
        if (!string.IsNullOrEmpty(LeftEditor.FilePath)) await ReloadAsync(true);
        if (!string.IsNullOrEmpty(RightEditor.FilePath)) await ReloadAsync(false);
        await CompareAsync();
    }

    private async Task<bool> SaveSideAsync(bool left)
    {
        var editor = left ? LeftEditor : RightEditor;
        var header = left ? LeftHeader : RightHeader;
        if (editor.IsBinaryContent)
        {
            ShowNotice("Binary files are compared read-only and cannot be saved from NaraDiff.", null);
            return false;
        }
        var path = editor.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            var dialog = new SaveFileDialog { Title = left ? "Save the left file" : "Save the right file" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
            path = dialog.FileName;
        }
        var lines = editor.GetLines();
        var mode = header.SelectedLineEndingMode;
        var target = mode switch
        {
            SaveLineEndingMode.Lf => LineEndingKind.Lf,
            SaveLineEndingMode.CrLf => LineEndingKind.CrLf,
            SaveLineEndingMode.Cr => LineEndingKind.Cr,
            _ => LineEndingKind.None
        };
        var encoding = header.SelectedEncoding;
        var conversions = new List<string>();
        var detected = LineEndings.Detect(lines);
        if (target != LineEndingKind.None && target != detected)
            conversions.Add($"line endings {LineEndings.DisplayName(detected)} to {LineEndings.DisplayName(target)}");
        if (encoding.Id != editor.EncodingChoice.Id)
            conversions.Add($"encoding {editor.EncodingChoice.DisplayName} to {encoding.DisplayName}");
        if (conversions.Count > 0 &&
            MessageBox.Show(Window.GetWindow(this), $"Saving {System.IO.Path.GetFileName(path)} converts {string.Join(" and ", conversions)}.\n\nContinue?",
                "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return false;
        var result = await _files.SaveAsync(new FileSaveRequest
        {
            Path = path!,
            Lines = lines,
            Encoding = encoding,
            LineEnding = target,
            CreateBackup = false
        });
        if (!result.Succeeded)
        {
            MessageBox.Show(Window.GetWindow(this), result.Error ?? "The file could not be saved.", "NaraDiff", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        editor.FilePath = result.Path;
        editor.EncodingChoice = encoding;
        editor.LineEnding = target == LineEndingKind.None ? detected : target;
        editor.IsModified = false;
        header.PathText = result.Path;
        UpdateWatcher();
        UpdateHeaders();
        TitleChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void UpdateWatcher() => _watcher.Watch([LeftEditor.FilePath, RightEditor.FilePath]);

    private void OnDiskChange(string path)
    {
        if (_disposed) return;
        var isLeft = string.Equals(path, LeftEditor.FilePath, StringComparison.OrdinalIgnoreCase);
        var isRight = string.Equals(path, RightEditor.FilePath, StringComparison.OrdinalIgnoreCase);
        if (!isLeft && !isRight) return;
        var editor = isLeft ? LeftEditor : RightEditor;
        var name = System.IO.Path.GetFileName(path);
        if (editor.IsModified)
        {
            _pendingReloadPath = path;
            ShowNotice($"{name} changed on disk, and this side has unsaved edits.", "Reload anyway");
            return;
        }
        _ = LoadSideAsync(isLeft, path, editor.EncodingChoice);
        ShowNotice($"{name} changed on disk and was reloaded.", null);
    }

    // ---------- comparison ----------

    private void ScheduleCompare(bool immediate = false)
    {
        if (_loading) return;
        _debounce.Stop();
        if (immediate || _settings.DiffDebounceMilliseconds <= 0)
        {
            _ = CompareAsync();
            return;
        }
        _debounce.Start();
    }

    private async Task CompareAsync()
    {
        if (_disposed) return;
        _comparison?.Cancel();
        _comparison?.Dispose();
        var source = new CancellationTokenSource();
        _comparison = source;
        if (IsBinaryMode)
        {
            ShowBinaryComparison();
            return;
        }
        var left = LeftEditor.GetLines();
        var right = RightEditor.GetLines();
        var heavy = left.Count + right.Count > 120_000;
        if (heavy) ShowBusy(true, "Comparing large files...");
        try
        {
            var result = await DiffEngine.CompareAsync(left, right, _options, source.Token);
            if (source.IsCancellationRequested) return;
            _diff = result;
            UpdateVisuals();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("compare", ex);
            ShowNotice($"The comparison failed: {ex.Message}", null);
        }
        finally
        {
            if (heavy) ShowBusy(false, string.Empty);
        }
    }

    private void UpdateVisuals()
    {
        if (IsBinaryMode) return;
        var palette = ThemeService.Palette;
        LeftEditor.Decorations = DiffDecorationSet.FromDiff(_diff, true, palette);
        RightEditor.Decorations = DiffDecorationSet.FromDiff(_diff, false, palette);
        var links = new List<ConnectorLink>(_diff.Blocks.Count);
        var marks = new List<OverviewMark>(_diff.Blocks.Count);
        foreach (var block in _diff.Blocks)
        {
            links.Add(new ConnectorLink
            {
                LeftStart = block.LeftStart,
                LeftCount = block.LeftCount,
                RightStart = block.RightStart,
                RightCount = block.RightCount,
                Fill = palette.RibbonFor(block.Kind, block.IsMoved),
                Stroke = palette.StrokeFor(block.Kind, block.IsMoved),
                AllowToRight = !RightEditor.IsReadOnly,
                AllowToLeft = !LeftEditor.IsReadOnly,
                Tooltip = DescribeBlock(block),
                Tag = block
            });
            marks.Add(new OverviewMark
            {
                Start = block.RightCount > 0 ? block.RightStart : Math.Max(0, block.RightStart - 1),
                Count = Math.Max(1, block.RightCount),
                Brush = palette.StrokeFor(block.Kind, block.IsMoved)
            });
        }
        Connector.SetLinks(links);
        Ruler.SetMarks(marks);
        UpdateFooter();
        UpdateHeaders();
    }

    private static string DescribeBlock(DiffBlock block)
    {
        var kind = block.IsMoved ? "Moved" : block.Kind switch
        {
            DiffBlockKind.Insert => "Added on the right",
            DiffBlockKind.Delete => "Removed on the right",
            _ => "Changed"
        };
        return $"{kind}: left lines {block.LeftStart + 1}-{Math.Max(block.LeftStart + 1, block.LeftEnd)}, right lines {block.RightStart + 1}-{Math.Max(block.RightStart + 1, block.RightEnd)}";
    }

    /// <summary>Shows two hex dumps when at least one side is not text.</summary>
    private void ShowBinaryComparison()
    {
        var left = _leftBytes ?? [];
        var right = _rightBytes ?? [];
        var summary = Core.Text.BinaryComparer.Compare(left, right);
        var leftRows = Core.Text.BinaryComparer.BuildRows(left, right);
        var rightRows = Core.Text.BinaryComparer.BuildRows(right, left);
        _loading = true;
        LeftEditor.IsReadOnly = true;
        RightEditor.IsReadOnly = true;
        LeftEditor.SetContent(BuildHexText(leftRows));
        RightEditor.SetContent(BuildHexText(rightRows));
        LeftEditor.Decorations = BuildHexDecorations(leftRows);
        RightEditor.Decorations = BuildHexDecorations(rightRows);
        _loading = false;
        Connector.SetLinks([]);
        Ruler.SetMarks([.. rightRows.Select((row, index) => (row, index)).Where(entry => entry.row.HasDifference)
            .Select(entry => new OverviewMark { Start = entry.index, Count = 1, Brush = ThemeService.Palette.ModifyStroke })]);
        StatusText = summary.Identical
            ? $"Binary files are identical ({summary.LeftLength:N0} bytes)"
            : $"Binary files differ: {summary.DifferentByteCount:N0} bytes, first difference at offset 0x{summary.FirstDifferenceOffset:X}";
        FooterText.Text = StatusText;
        FooterRightText.Text = $"SHA-256 {summary.LeftHash} / {summary.RightHash}";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildHexText(IReadOnlyList<Core.Text.HexRow> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows) builder.Append(row.OffsetText).Append("  ").Append(row.HexText.PadRight(49)).Append(" |").Append(row.AsciiText).Append("|\n");
        return builder.ToString();
    }

    private static DiffDecorationSet BuildHexDecorations(IReadOnlyList<Core.Text.HexRow> rows)
    {
        var set = new DiffDecorationSet();
        var palette = ThemeService.Palette;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].HasDifference) continue;
            set.Set(i, new LineDecoration { Fill = palette.ModifyFill, EdgeStroke = palette.ModifyStroke, IsBlockStart = true, IsBlockEnd = true });
        }
        return set;
    }

    // ---------- navigation and hunk commands ----------

    private DiffBlock? CurrentBlock()
    {
        if (_diff.Blocks.Count == 0) return null;
        var active = ActiveEditor;
        var line = active == LeftEditor ? active.CaretLineIndex : (int)Math.Round(_diff.MapRightToLeft(active.CaretLineIndex));
        return _diff.BlockAtLeftLine(line) ?? _diff.BlockAtOrAfterLeftLine(line) ?? _diff.Blocks[^1];
    }

    private void Navigate(bool forward)
    {
        if (_diff.Blocks.Count == 0) return;
        var active = ActiveEditor;
        var line = active == LeftEditor ? active.CaretLineIndex : (int)Math.Round(_diff.MapRightToLeft(active.CaretLineIndex));
        var block = forward
            ? _diff.NextBlockAfterLeftLine(line) ?? _diff.Blocks[0]
            : _diff.PreviousBlockBeforeLeftLine(line) ?? _diff.Blocks[^1];
        GoToBlock(block);
    }

    private void GoToBlock(DiffBlock block)
    {
        BeginProgrammaticScroll();
        LeftEditor.ScrollToLineIndex(block.LeftStart);
        RightEditor.ScrollToLineIndex(block.RightStart);
        if (ActiveEditor == RightEditor) RightEditor.MoveCaretToLine(block.RightStart);
        else LeftEditor.MoveCaretToLine(block.LeftStart);
        Connector.Refresh();
        Ruler.Refresh();
        UpdateFooter();
    }

    private void Connector_Action(object? sender, ConnectorActionEventArgs e)
    {
        if (e.Link.Tag is DiffBlock block) ApplyBlocks([block], e.Direction);
    }

    /// <summary>
    /// The blocks a copy command works on: every change that the selection touches, or the change at
    /// the caret when nothing is selected.
    /// </summary>
    private List<DiffBlock> BlocksForCommand()
    {
        var editor = ActiveEditor;
        var document = editor.Document;
        if (document is not null && editor.SelectionLength > 0)
        {
            var isLeft = ReferenceEquals(editor, LeftEditor);
            var first = document.GetLineByOffset(Math.Clamp(editor.SelectionStart, 0, document.TextLength)).LineNumber - 1;
            var last = document.GetLineByOffset(Math.Clamp(editor.SelectionStart + editor.SelectionLength, 0, document.TextLength)).LineNumber - 1;
            var hits = _diff.Blocks
                .Where(block => Overlaps(isLeft ? block.LeftStart : block.RightStart, isLeft ? block.LeftCount : block.RightCount, first, last))
                .ToList();
            if (hits.Count > 0) return hits;
        }
        var single = CurrentBlock();
        return single is null ? [] : [single];
    }

    private static bool Overlaps(int start, int count, int first, int last) =>
        count == 0 ? start >= first && start <= last + 1 : start <= last && start + count - 1 >= first;

    private void ApplyBlocks(List<DiffBlock> blocks, ConnectorDirection direction)
    {
        if (blocks.Count == 0 || IsBinaryMode) return;
        var target = direction == ConnectorDirection.ToRight ? RightEditor : LeftEditor;
        if (target.IsReadOnly)
        {
            ShowNotice($"The {(direction == ConnectorDirection.ToRight ? "right" : "left")} file is read-only.", null);
            return;
        }
        var document = target.Document;
        if (document is null) return;
        using (document.RunUpdate())
        {
            foreach (var block in blocks.OrderByDescending(block => direction == ConnectorDirection.ToRight ? block.RightStart : block.LeftStart))
            {
                if (direction == ConnectorDirection.ToRight)
                    DocumentEditing.ReplaceLines(document, block.RightStart, block.RightCount,
                        DocumentEditing.ReadLines(_diff.LeftLines, block.LeftStart, block.LeftCount), EffectiveLineEnding(RightEditor));
                else
                    DocumentEditing.ReplaceLines(document, block.LeftStart, block.LeftCount,
                        DocumentEditing.ReadLines(_diff.RightLines, block.RightStart, block.RightCount), EffectiveLineEnding(LeftEditor));
            }
        }
        if (blocks.Count > 1) ShowNotice($"{blocks.Count} changes were copied to the {(direction == ConnectorDirection.ToRight ? "right" : "left")} file.", null);
        ScheduleCompare(immediate: true);
    }

    private void ApplyAll(ConnectorDirection direction)
    {
        if (IsBinaryMode || _diff.Blocks.Count == 0) return;
        var target = direction == ConnectorDirection.ToRight ? RightEditor : LeftEditor;
        if (target.IsReadOnly)
        {
            ShowNotice($"The {(direction == ConnectorDirection.ToRight ? "right" : "left")} file is read-only.", null);
            return;
        }
        var document = target.Document;
        if (document is null) return;
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"Copy all {_diff.Blocks.Count} changes to the {(direction == ConnectorDirection.ToRight ? "right" : "left")} file?",
            "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        using (document.RunUpdate())
        {
            foreach (var block in _diff.Blocks.OrderByDescending(item => direction == ConnectorDirection.ToRight ? item.RightStart : item.LeftStart))
            {
                if (direction == ConnectorDirection.ToRight)
                    DocumentEditing.ReplaceLines(document, block.RightStart, block.RightCount,
                        DocumentEditing.ReadLines(_diff.LeftLines, block.LeftStart, block.LeftCount), EffectiveLineEnding(RightEditor));
                else
                    DocumentEditing.ReplaceLines(document, block.LeftStart, block.LeftCount,
                        DocumentEditing.ReadLines(_diff.RightLines, block.RightStart, block.RightCount), EffectiveLineEnding(LeftEditor));
            }
        }
        ScheduleCompare(immediate: true);
    }

    private LineEndingKind EffectiveLineEnding(DiffTextEditor editor) =>
        editor.LineEnding is LineEndingKind.None or LineEndingKind.Mixed ? LineEndingKind.Lf : editor.LineEnding;

    // ---------- scrolling and status ----------

    private void Editor_ViewChanged(object? sender, EventArgs e)
    {
        if (sender is DiffTextEditor source && CanSynchronizeFrom(source)) Synchronize(source);
        Connector.Refresh();
        Ruler.Refresh();
    }

    /// <summary>
    /// Only the pane the user is working in drives the synchronisation, and the scroll it causes on
    /// the other pane is ignored when it comes back. Without both rules the two panes push each other:
    /// a scroll request reaches the text view one layout pass later, and mapping a position back
    /// through a one sided change does not return the position it came from, so the panes drift.
    /// </summary>
    private bool CanSynchronizeFrom(DiffTextEditor source) =>
        _settings.SynchronizeScrolling
        && !IsBinaryMode
        && _programmaticScrolls == 0
        && (_scrollLeader is null || ReferenceEquals(_scrollLeader, source));

    private void Synchronize(DiffTextEditor source)
    {
        var other = ReferenceEquals(source, LeftEditor) ? RightEditor : LeftEditor;
        var height = source.LineHeight;
        if (height <= 0 || other.LineHeight <= 0) return;
        var sourceOffset = source.TextArea.TextView.ScrollOffset;
        var otherOffset = other.TextArea.TextView.ScrollOffset;
        var sourceLine = sourceOffset.Y / height;
        var targetLine = ReferenceEquals(source, LeftEditor) ? _diff.MapLeftToRight(sourceLine) : _diff.MapRightToLeft(sourceLine);
        var targetY = Math.Max(0, targetLine * other.LineHeight);
        var moveVertically = Math.Abs(otherOffset.Y - targetY) >= 1;
        var moveHorizontally = Math.Abs(otherOffset.X - sourceOffset.X) >= 1;
        if (!moveVertically && !moveHorizontally) return;
        BeginProgrammaticScroll();
        if (moveVertically) other.ScrollToVerticalOffset(targetY);
        if (moveHorizontally) other.ScrollToHorizontalOffset(sourceOffset.X);
    }

    /// <summary>
    /// Marks the scrolls started from code so the events they raise, which arrive after the next
    /// layout pass, do not start another synchronisation.
    /// </summary>
    private void BeginProgrammaticScroll()
    {
        _programmaticScrolls++;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _programmaticScrolls = Math.Max(0, _programmaticScrolls - 1)));
    }

    private void ScrollBothToRightLine(int rightLine)
    {
        BeginProgrammaticScroll();
        RightEditor.ScrollToLineIndex(rightLine);
        LeftEditor.ScrollToLineIndex((int)Math.Round(_diff.MapRightToLeft(rightLine)));
        Connector.Refresh();
        Ruler.Refresh();
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        UpdateHeaders();
        ScheduleCompare();
    }

    private void UpdateHeaders()
    {
        LeftHeader.SetState(Describe(LeftEditor), LeftEditor.IsReadOnly, LeftEditor.IsModified);
        RightHeader.SetState(Describe(RightEditor), RightEditor.IsReadOnly, RightEditor.IsModified);
        LeftHeader.CanSave = !LeftEditor.IsReadOnly;
        RightHeader.CanSave = !RightEditor.IsReadOnly;
    }

    private static string Describe(DiffTextEditor editor)
    {
        if (string.IsNullOrEmpty(editor.FilePath)) return "no file";
        var lines = Math.Max(0, editor.DocumentLineCount);
        var eol = LineEndings.DisplayName(editor.LineEnding);
        return editor.IsBinaryContent ? "binary, shown as hex" : $"{lines:N0} lines, {eol}";
    }

    private void UpdateFooter()
    {
        if (IsBinaryMode) return;
        var statistics = _diff.Statistics;
        StatusText = _diff.AreIdentical
            ? "The files are identical with the current options"
            : $"{statistics.BlockCount} changes: {statistics.Inserted} added, {statistics.Deleted} removed, {statistics.Modified} modified, {statistics.Moved} moved " +
              $"({statistics.ChangedLeftLines} left and {statistics.ChangedRightLines} right lines affected)";
        var caret = ActiveEditor;
        var side = caret == LeftEditor ? "left" : "right";
        var block = CurrentBlock();
        var position = block is null || _diff.Blocks.Count == 0
            ? "no changes"
            : $"change {block.Index + 1} of {_diff.Blocks.Count}";
        FooterText.Text = $"{position}  ·  {side} line {caret.CaretLineIndex + 1} of {Math.Max(1, caret.DocumentLineCount)}";
        FooterRightText.Text = $"{LeftEditor.EncodingChoice.DisplayName} {LineEndings.DisplayName(LeftEditor.LineEnding)}  ⇄  {RightEditor.EncodingChoice.DisplayName} {LineEndings.DisplayName(RightEditor.LineEnding)}  ·  {_options.Describe()}";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowBusy(bool visible, string text)
    {
        BusyOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) BusyText.Text = text;
    }

    private void ShowNotice(string text, string? actionLabel)
    {
        NoticeText.Text = text;
        NoticeActionButton.Visibility = actionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        if (actionLabel is not null) NoticeActionButton.Content = actionLabel;
        NoticeBar.Visibility = Visibility.Visible;
    }

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        NoticeBar.Visibility = Visibility.Collapsed;
        if (_pendingReloadPath is null) return;
        var isLeft = string.Equals(_pendingReloadPath, LeftEditor.FilePath, StringComparison.OrdinalIgnoreCase);
        var path = _pendingReloadPath;
        _pendingReloadPath = null;
        _ = LoadSideAsync(isLeft, path, (isLeft ? LeftEditor : RightEditor).EncodingChoice);
    }

    private void NoticeDismiss_Click(object sender, RoutedEventArgs e)
    {
        NoticeBar.Visibility = Visibility.Collapsed;
        _pendingReloadPath = null;
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (HasUnsavedChanges &&
            MessageBox.Show(Window.GetWindow(this), "Swapping the sides reloads both files and discards unsaved edits. Continue?",
                "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        var left = LeftEditor.FilePath;
        var right = RightEditor.FilePath;
        _ = OpenAsync(right, left);
    }

    // ---------- search ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RunSearch();

    private void SearchOption_Changed(object sender, RoutedEventArgs e) => RunSearch();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchClose_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            MoveToMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
    }

    private void SearchNext_Click(object sender, RoutedEventArgs e) => MoveToMatch(1);

    private void SearchPrevious_Click(object sender, RoutedEventArgs e) => MoveToMatch(-1);

    private void SearchClose_Click(object sender, RoutedEventArgs e)
    {
        SearchBar.Visibility = Visibility.Collapsed;
        _leftMatches.Clear();
        _rightMatches.Clear();
        LeftEditor.SearchMatches = DiffDecorationSet.Empty;
        RightEditor.SearchMatches = DiffDecorationSet.Empty;
        ActiveEditor.Focus();
    }

    private void RunSearch()
    {
        _leftMatches.Clear();
        _rightMatches.Clear();
        _matchIndex = -1;
        var pattern = SearchBox.Text;
        if (string.IsNullOrEmpty(pattern))
        {
            SearchCountText.Text = string.Empty;
            LeftEditor.SearchMatches = DiffDecorationSet.Empty;
            RightEditor.SearchMatches = DiffDecorationSet.Empty;
            return;
        }
        Regex regex;
        try
        {
            var body = SearchRegexToggle.IsChecked == true ? pattern : Regex.Escape(pattern);
            var options = RegexOptions.CultureInvariant | (SearchCaseToggle.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase);
            regex = new Regex(body, options);
        }
        catch (ArgumentException ex)
        {
            SearchCountText.Text = ex.Message;
            return;
        }
        Collect(LeftEditor, regex, _leftMatches);
        Collect(RightEditor, regex, _rightMatches);
        var brush = ThemeService.Brush("EditorSearchMatch");
        LeftEditor.SearchMatches = DiffDecorationSet.FromMatches(_leftMatches, brush);
        RightEditor.SearchMatches = DiffDecorationSet.FromMatches(_rightMatches, brush);
        SearchCountText.Text = $"{_leftMatches.Count} left / {_rightMatches.Count} right";
    }

    private static void Collect(DiffTextEditor editor, Regex regex, List<(int Line, TextSpan Span)> target)
    {
        var document = editor.Document;
        if (document is null) return;
        for (var number = 1; number <= document.LineCount; number++)
        {
            var line = document.GetLineByNumber(number);
            var text = document.GetText(line.Offset, line.Length);
            foreach (var match in regex.Matches(text).Cast<Match>())
            {
                if (match.Length == 0) continue;
                target.Add((number - 1, new TextSpan(match.Index, match.Length)));
                if (target.Count > 20000) return;
            }
        }
    }

    private void MoveToMatch(int direction)
    {
        var editor = ActiveEditor;
        var matches = editor == LeftEditor ? _leftMatches : _rightMatches;
        if (matches.Count == 0) return;
        _matchIndex = _matchIndex < 0
            ? matches.FindIndex(match => match.Line >= editor.CaretLineIndex)
            : _matchIndex + direction;
        if (_matchIndex < 0) _matchIndex = matches.Count - 1;
        if (_matchIndex >= matches.Count) _matchIndex = 0;
        var (line, span) = matches[_matchIndex];
        editor.ScrollToLineIndex(line);
        editor.MoveCaretToLine(line);
        var documentLine = editor.Document?.GetLineByNumber(Math.Min(line + 1, Math.Max(1, editor.DocumentLineCount)));
        if (documentLine is not null) editor.Select(documentLine.Offset + span.Start, span.Length);
        SearchCountText.Text = $"{_matchIndex + 1} of {matches.Count} in the {(editor == LeftEditor ? "left" : "right")} file";
    }
}
