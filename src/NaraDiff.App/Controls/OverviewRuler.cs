using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NaraDiff.App.Services;

namespace NaraDiff.App.Controls;

/// <summary>One entry of the overview ruler.</summary>
public sealed class OverviewMark
{
    public required int Start { get; init; }

    public required int Count { get; init; }

    public required Brush Brush { get; init; }

    /// <summary>Conflicts are drawn across the full width so they stand out.</summary>
    public bool Emphasis { get; init; }
}

/// <summary>
/// A miniature of the whole file next to the right editor: every change is a coloured tick, the
/// current viewport is a translucent box, and clicking or dragging scrolls to that position.
/// </summary>
public sealed class OverviewRuler : FrameworkElement
{
    private bool _dragging;

    public OverviewRuler()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Focusable = false;
        Cursor = Cursors.Hand;
        ToolTip = "Overview: click to jump to a change";
        System.Windows.Automation.AutomationProperties.SetName(this, "Change overview");
        ThemeService.Changed += (_, _) => InvalidateVisual();
    }

    /// <summary>Raised with the zero based line the user picked.</summary>
    public event EventHandler<int>? LineRequested;

    public DiffTextEditor? Editor { get; set; }

    public IReadOnlyList<OverviewMark> Marks { get; private set; } = [];

    public void SetMarks(IReadOnlyList<OverviewMark> marks)
    {
        Marks = marks ?? [];
        InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        drawingContext.DrawRectangle(ThemeService.Brush("RulerBackground"), null, new Rect(0, 0, width, height));
        drawingContext.DrawRectangle(ThemeService.Brush("Border"), null, new Rect(0, 0, 1, height));
        var lineCount = Math.Max(1, Editor?.DocumentLineCount ?? 1);
        foreach (var mark in Marks)
        {
            var top = (double)mark.Start / lineCount * height;
            var markHeight = Math.Max(2.5, (double)Math.Max(1, mark.Count) / lineCount * height);
            var rect = mark.Emphasis
                ? new Rect(1, top, width - 2, markHeight)
                : new Rect(2.5, top, width - 5, markHeight);
            drawingContext.DrawRectangle(mark.Brush, null, rect);
        }
        if (Editor is null) return;
        var first = Editor.FirstVisibleLine;
        var visible = Editor.VisibleLineCount;
        var viewportTop = (double)first / lineCount * height;
        var viewportHeight = Math.Max(6, (double)visible / lineCount * height);
        var pen = new Pen(ThemeService.Brush("RulerViewportBorder"), 1);
        pen.Freeze();
        drawingContext.DrawRectangle(ThemeService.Brush("RulerViewport"), pen, new Rect(0.5, viewportTop, width - 1, viewportHeight));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        dragging = true;
        CaptureMouse();
        RequestLine(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) RequestLine(e.GetPosition(this).Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        dragging = false;
        ReleaseMouseCapture();
    }

    private void RequestLine(double y)
    {
        var lineCount = Math.Max(1, Editor?.DocumentLineCount ?? 1);
        var ratio = Math.Clamp(y / Math.Max(1, ActualHeight), 0, 1);
        LineRequested?.Invoke(this, (int)(ratio * lineCount));
    }
}