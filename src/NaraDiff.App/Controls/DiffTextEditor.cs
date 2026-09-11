using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using NaraDiff.App.Services;
using NaraDiff.Core.Settings;
using NaraDiff.Core.Text;

namespace NaraDiff.App.Controls;

/// <summary>
/// The editor used in every pane: an AvalonEdit text editor with the diff renderer attached, plus
/// the helpers the connector ribbon, the overview ruler and the merge commands need.
/// </summary>
public sealed class DiffTextEditor : TextEditor
{
    private readonly DiffBackgroundRenderer _renderer = new();

    private readonly DiffLineNumberMargin _lineNumberMargin;
    private StylusDevice? _panningStylus;
    private Point _stylusPanStart;
    private Vector _stylusScrollStart;
    private Point _lastStylusPanPoint;
    private TimeSpan _lastStylusPanTime;
    private Vector _stylusVelocity;
    private bool _isStylusPanning;
    private readonly InertialPan _stylusInertia;

    public DiffTextEditor()
    {
        _stylusInertia = new InertialPan(delta =>
        {
            var offset = TextArea.TextView.ScrollOffset;
            ScrollToVerticalOffset(Math.Max(0, offset.Y + delta.Y));
            ScrollToHorizontalOffset(Math.Max(0, offset.X + delta.X));
        });
        ShowLineNumbers = true;
        Options.HighlightCurrentLine = false;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.AllowScrollBelowDocument = true;
        Options.ConvertTabsToSpaces = false;
        Options.EnableRectangularSelection = true;
        Options.CutCopyWholeLine = false;
        HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(2, 2, 0, 2);
        var separator = TextArea.LeftMargins.OfType<System.Windows.Shapes.Line>().ToList();
        foreach (var line in separator) TextArea.LeftMargins.Remove(line);
        _lineNumberMargin = new DiffLineNumberMargin(_renderer) { Margin = new Thickness(0, 0, 4, 0) };
        var existingLineNumberMargin = TextArea.LeftMargins.OfType<LineNumberMargin>().FirstOrDefault();
        if (existingLineNumberMargin is null) TextArea.LeftMargins.Insert(0, _lineNumberMargin);
        else TextArea.LeftMargins[TextArea.LeftMargins.IndexOf(existingLineNumberMargin)] = _lineNumberMargin;
        TextArea.TextView.BackgroundRenderers.Add(_renderer);
        TextArea.Caret.PositionChanged += (_, _) =>
        {
            _renderer.CurrentLine = TextArea.Caret.Line - 1;
            Redraw();
            CaretLineChanged?.Invoke(this, EventArgs.Empty);
        };
        TextArea.TextView.ScrollOffsetChanged += (_, _) => ViewChanged?.Invoke(this, EventArgs.Empty);
        SizeChanged += (_, _) => ViewChanged?.Invoke(this, EventArgs.Empty);
        PreviewStylusDown += OnStylusPanDown;
        PreviewStylusMove += OnStylusPanMove;
        PreviewStylusUp += OnStylusPanUp;
        LostStylusCapture += (_, _) => EndStylusPan();
    }

    /// <summary>
    /// Moves this editor's vertical scroll bar from its usual right edge to the left edge, so the
    /// right edge stays free for the connector ribbon and the diff block backgrounds are not clipped
    /// underneath the scroll bar. Mirrors the editor's own layout (which swaps the scroll bar and the
    /// text area to the other side of that layout) and then mirrors the text area back, so the text,
    /// the line numbers and the diff backgrounds keep reading left to right.
    /// </summary>
    public void PlaceScrollBarOnTheLeft()
    {
        Padding = new Thickness(Padding.Right, Padding.Top, Padding.Left, Padding.Bottom);
        FlowDirection = FlowDirection.RightToLeft;
        TextArea.FlowDirection = FlowDirection.LeftToRight;
    }
    
    private void OnStylusPanDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice.Type != TabletDeviceType.Stylus) return;
        _stylusInertia.Stop();
        _panningStylus = e.StylusDevice;
        _stylusPanStart = e.GetPosition(this);
        _lastStylusPanPoint = _stylusPanStart;
        _lastStylusPanTime = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        _stylusVelocity = new Vector();
        _stylusScrollStart = TextArea.TextView.ScrollOffset;
        _isStylusPanning = false;
    }

    private void OnStylusPanMove(object sender, StylusEventArgs e)
    {
        if (!ReferenceEquals(e.StylusDevice, _panningStylus)) return;
        var delta = e.GetPosition(this) - _stylusPanStart;
        if (!_isStylusPanning && delta.Length < 3) return;
        _isStylusPanning = true;
        if (!IsStylusCaptured) CaptureStylus();
        ScrollToVerticalOffset(Math.Max(0, _stylusScrollStart.Y - delta.Y));
        ScrollToHorizontalOffset(Math.Max(0, _stylusScrollStart.X - delta.X));
        var now = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        var elapsedMilliseconds = (now - _lastStylusPanTime).TotalMilliseconds;
        if (elapsedMilliseconds > 0)
        {
            var movement = e.GetPosition(this) - _lastStylusPanPoint;
            var instantaneousVelocity = new Vector(-movement.X / elapsedMilliseconds, -movement.Y / elapsedMilliseconds);
            _stylusVelocity = _stylusVelocity * 0.35 + instantaneousVelocity * 0.65;
        }
        _lastStylusPanPoint = e.GetPosition(this);
        _lastStylusPanTime = now;
        e.Handled = true;
    }

    private void OnStylusPanUp(object sender, StylusEventArgs e)
    {
        if (!ReferenceEquals(e.StylusDevice, _panningStylus)) return;
        var wasPanning = _isStylusPanning;
        var velocity = _stylusVelocity;
        EndStylusPan();
        if (wasPanning)
        {
            _stylusInertia.Start(velocity);
            e.Handled = true;
        }
    }

    private void EndStylusPan()
    {
        if (IsStylusCaptured) ReleaseStylusCapture();
        _panningStylus = null;
        _isStylusPanning = false;
    }

    /// <summary>Raised when the visible region changed and the connectors must be recalculated.</summary>
    public event EventHandler? ViewChanged;

    public event EventHandler? CaretLineChanged;

    /// <summary>Where the loaded content came from; used in the header and for saving.</summary>
    public string? FilePath { get; set; }

    public EncodingChoice EncodingChoice { get; set; } = EncodingCatalog.Utf8;

    public LineEndingKind LineEnding { get; set; } = LineEndingKind.Lf;

    public bool IsBinaryContent { get; set; }

    public DiffDecorationSet Decorations
    {
        get => _renderer.Decorations;
        set
        {
            _renderer.Decorations = value;
            Redraw();
        }
    }

    public DiffDecorationSet SearchMatches
    {
        get => _renderer.SearchMatches;
        set
        {
            _renderer.SearchMatches = value;
            Redraw();
        }
    }

    public int CaretLineIndex => Math.Max(0, TextArea.Caret.Line - 1);

    public int DocumentLineCount => Document?.LineCount ?? 0;

    /// <summary>The document as loss free lines, ready for the diff engine.</summary>
    public IReadOnlyList<TextLine> GetLines() => LineEndings.Split(Text);

    public void ApplyAppearance(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FontFamily = new FontFamily(settings.EditorFontFamily);
        FontSize = settings.EditorFontSize;
        ShowLineNumbers = settings.ShowLineNumbers;
        WordWrap = settings.WordWrap;
        Options.ShowSpaces = settings.ShowWhitespace;
        Options.ShowTabs = settings.ShowWhitespace;
        Options.ShowEndOfLine = settings.ShowWhitespace;
        Options.IndentationSize = Math.Max(1, settings.DiffOptions.TabWidth);
        Background = ThemeService.Brush("EditorBackground");
        Foreground = ThemeService.Brush("EditorForeground");
        LineNumbersForeground = ThemeService.Brush("EditorLineNumber");
        TextArea.SelectionBrush = ThemeService.Brush("EditorSelection");
        TextArea.SelectionBorder = null;
        TextArea.SelectionForeground = null;
        TextArea.Caret.CaretBrush = ThemeService.Brush("EditorForeground");
        _renderer.CurrentLineBrush = ThemeService.Brush("EditorCurrentLine");
        Redraw();
    }

    public void Redraw()
    {
        TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
        _lineNumberMargin.InvalidateVisual();
    }

    /// <summary>Replaces the whole document without adding an undo step for the load.</summary>
    public void SetContent(string text)
    {
        var document = new TextDocument(text);
        document.UndoStack.ClearAll();
        Document = document;
        IsModified = false;
    }

    /// <summary>Top of a line in the coordinate system of the text view, adjusted for scrolling.</summary>
    public double GetLineTop(int lineIndex)
    {
        var view = TextArea.TextView;
        var document = Document;
        if (document is null) return 0;
        var clamped = Math.Clamp(lineIndex, 0, Math.Max(0, document.LineCount - 1));
        try
        {
            var top = view.GetVisualTopByDocumentLine(clamped + 1) - view.ScrollOffset.Y;
            if (lineIndex >= document.LineCount) top += view.DefaultLineHeight;
            return top;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>Bottom of a line range, so a block of zero lines collapses to a single position.</summary>
    public double GetLineBottom(int lineIndex) => GetLineTop(lineIndex + 1);

    public double LineHeight => TextArea.TextView.DefaultLineHeight;

    public int FirstVisibleLine
    {
        get
        {
            var height = TextArea.TextView.DefaultLineHeight;
            return height <= 0 ? 0 : (int)(TextArea.TextView.ScrollOffset.Y / height);
        }
    }

    public int VisibleLineCount
    {
        get
        {
            var height = TextArea.TextView.DefaultLineHeight;
            return height <= 0 ? 1 : Math.Max(1, (int)(TextArea.TextView.ActualHeight / height));
        }
    }

    public void ScrollToLineIndex(int lineIndex, bool center = true)
    {
        var count = Math.Max(1, DocumentLineCount);
        var line = Math.Clamp(lineIndex + 1, 1, count);
        if (center) ScrollTo(line, 0);
        else ScrollToVerticalOffset(Math.Max(0, (line - 1) * LineHeight));
    }

    public void MoveCaretToLine(int lineIndex)
    {
        var count = Math.Max(1, DocumentLineCount);
        var line = Math.Clamp(lineIndex + 1, 1, count);
        TextArea.Caret.Line = line;
        TextArea.Caret.Column = 1;
        TextArea.Caret.BringCaretToView();
    }
}
