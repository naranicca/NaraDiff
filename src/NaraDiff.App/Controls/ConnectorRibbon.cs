using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NaraDiff.App.Services;
using NaraDiff.Core.Rendering;

namespace NaraDiff.App.Controls;

/// <summary>One correspondence between a left line range and a right line range.</summary>
public sealed class ConnectorLink
{
    public required int LeftStart { get; init; }

    public required int LeftCount { get; init; }

    public required int RightStart { get; init; }

    public required int RightCount { get; init; }

    public required Brush Fill { get; init; }

    public required Brush Stroke { get; init; }

    /// <summary>Show the button that copies the left content over the right content.</summary>
    public bool AllowToRight { get; init; }

    public bool AllowToLeft { get; init; }

    /// <summary>Marks unresolved conflicts with a warning glyph instead of an arrow.</summary>
    public bool IsConflict { get; init; }

    public string? Tooltip { get; init; }

    public object? Tag { get; init; }
}

public enum ConnectorDirection
{
    ToRight,
    ToLeft
}

public sealed class ConnectorActionEventArgs(ConnectorLink link, ConnectorDirection direction) : EventArgs
{
    public ConnectorLink Link { get; } = link;

    public ConnectorDirection Direction { get; } = direction;
}

/// <summary>
/// The gutter between two editors. Every change is drawn as a Bezier ribbon from the block in the
/// left editor to the matching block in the right editor, so it stays obvious which change belongs
/// to which. The ribbon carries the copy buttons for its hunk.
/// </summary>
public sealed class ConnectorRibbon : FrameworkElement
{
    private const double ButtonSize = 17;
    private const double ButtonGap = 3;

    private readonly List<(Rect Bounds, ConnectorLink Link, ConnectorDirection Direction)> _buttons = [];
    private readonly List<(Geometry Shape, ConnectorLink Link)> _shapes = [];
    private ConnectorLink? _hoveredLink;
    private int _hoveredButton = -1;

    public ConnectorRibbon()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
        Focusable = false;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 250);
        System.Windows.Automation.AutomationProperties.SetName(this, "Change connectors");
        ThemeService.Changed += (_, _) => InvalidateVisual();
    }

    /// <summary>Raised when one of the hunk copy buttons is clicked.</summary>
    public event EventHandler<ConnectorActionEventArgs>? Action;

    /// <summary>Raised when a ribbon itself is clicked, which scrolls both editors to that change.</summary>
    public event EventHandler<ConnectorLink>? LinkActivated;

    public DiffTextEditor? LeftEditor { get; set; }

    public DiffTextEditor? RightEditor { get; set; }

    public IReadOnlyList<ConnectorLink> Links { get; private set; } = [];

    public bool ShowButtons { get; set; } = true;

    public bool ShowRibbons { get; set; } = true;

    public void SetLinks(IReadOnlyList<ConnectorLink> links)
    {
        Links = links ?? [];
        InvalidateVisual();
    }

    /// <summary>Recalculates the geometry; called on every scroll, edit and resize.</summary>
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        _buttons.Clear();
        _shapes.Clear();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        drawingContext.DrawRectangle(ThemeService.Brush("GutterBackground"), null, new Rect(0, 0, width, height));
        var edge = ThemeService.Brush("Border");
        drawingContext.DrawRectangle(edge, null, new Rect(0, 0, 1, height));
        drawingContext.DrawRectangle(edge, null, new Rect(width - 1, 0, 1, height));
        if (LeftEditor is null || RightEditor is null || Links.Count == 0) return;
        foreach (var link in Links)
        {
            var leftTop = ToLocal(LeftEditor, LeftEditor.GetLineTop(link.LeftStart));
            var leftBottom = ToLocal(LeftEditor, LeftEditor.GetLineBottom(link.LeftStart + Math.Max(0, link.LeftCount) - 1));
            if (link.LeftCount == 0) leftBottom = leftTop;
            var rightTop = ToLocal(RightEditor, RightEditor.GetLineTop(link.RightStart));
            var rightBottom = ToLocal(RightEditor, RightEditor.GetLineBottom(link.RightStart + Math.Max(0, link.RightCount) - 1));
            if (link.RightCount == 0) rightBottom = rightTop;
            var lowest = Math.Max(Math.Max(leftTop, leftBottom), Math.Max(rightTop, rightBottom));
            var highest = Math.Min(Math.Min(leftTop, leftBottom), Math.Min(rightTop, rightBottom));
            if (lowest < -40 || highest > height + 40) continue;
            var ribbon = RibbonGeometry.Build(leftTop, leftBottom, rightTop, rightBottom, width);
            var shape = BuildGeometry(ribbon);
            _shapes.Add((shape, link));
            if (ShowRibbons)
            {
                var hovered = ReferenceEquals(link, _hoveredLink);
                var pen = new Pen(link.Stroke, hovered ? 1.6 : 1.0) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                drawingContext.DrawGeometry(link.Fill, pen, shape);
            }
            if (link.IsConflict) DrawConflictMarker(drawingContext, link, ribbon.CenterY);
            if (ShowButtons) DrawButtons(drawingContext, link, ribbon.CenterY, width);
        }
    }

    private void DrawButtons(DrawingContext drawingContext, ConnectorLink link, double centerY, double width)
    {
        var actions = new List<ConnectorDirection>();
        if (link.AllowToRight) actions.Add(ConnectorDirection.ToRight);
        if (link.AllowToLeft) actions.Add(ConnectorDirection.ToLeft);
        if (actions.Count == 0) return;
        var total = actions.Count * ButtonSize + (actions.Count - 1) * ButtonGap;
        var x = (width - total) / 2;
        var y = centerY - ButtonSize / 2;
        foreach (var direction in actions)
        {
            var bounds = new Rect(x, y, ButtonSize, ButtonSize);
            var index = _buttons.Count;
            _buttons.Add((bounds, link, direction));
            var hovered = _hoveredButton == index;
            var background = hovered ? ThemeService.Brush("Accent") : ThemeService.Brush("SurfaceRaised");
            var foreground = hovered ? ThemeService.Brush("AccentText") : link.Stroke;
            var borderPen = new Pen(hovered ? ThemeService.Brush("Accent") : link.Stroke, 1);
            borderPen.Freeze();
            drawingContext.DrawRoundedRectangle(background, borderPen, bounds, 4, 4);
            var glyphPen = new Pen(foreground, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            glyphPen.Freeze();
            DrawArrow(drawingContext, bounds, direction, glyphPen);
            x += ButtonSize + ButtonGap;
        }
    }

    private static void DrawArrow(DrawingContext drawingContext, Rect bounds, ConnectorDirection direction, Pen pen)
    {
        var centerY = bounds.Y + bounds.Height / 2;
        var left = bounds.X + 4.5;
        var right = bounds.Right - 4.5;
        if (direction == ConnectorDirection.ToRight)
        {
            drawingContext.DrawLine(pen, new Point(left, centerY), new Point(right, centerY));
            drawingContext.DrawLine(pen, new Point(right - 3.5, centerY - 3.5), new Point(right, centerY));
            drawingContext.DrawLine(pen, new Point(right - 3.5, centerY + 3.5), new Point(right, centerY));
        }
        else
        {
            drawingContext.DrawLine(pen, new Point(right, centerY), new Point(left, centerY));
            drawingContext.DrawLine(pen, new Point(left + 3.5, centerY - 3.5), new Point(left, centerY));
            drawingContext.DrawLine(pen, new Point(left + 3.5, centerY + 3.5), new Point(left, centerY));
        }
    }

    /// <summary>Draws the warning triangle that marks an unresolved conflict.</summary>
    private static void DrawConflictMarker(DrawingContext drawingContext, ConnectorLink link, double centerY)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(8, centerY - 7), true, true);
            context.LineTo(new Point(14, centerY + 4), true, false);
            context.LineTo(new Point(2, centerY + 4), true, false);
        }
        geometry.Freeze();
        var pen = new Pen(link.Stroke, 1);
        pen.Freeze();
        drawingContext.DrawGeometry(link.Fill, pen, geometry);
        drawingContext.DrawRectangle(link.Stroke, null, new Rect(7.25, centerY - 4, 1.5, 4));
        drawingContext.DrawRectangle(link.Stroke, null, new Rect(7.25, centerY + 1, 1.5, 1.5));
    }

    private static Geometry BuildGeometry(Ribbon ribbon)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToPoint(ribbon.Top.Start), true, true);
            context.BezierTo(ToPoint(ribbon.Top.Control1), ToPoint(ribbon.Top.Control2), ToPoint(ribbon.Top.End), true, false);
            context.LineTo(ToPoint(ribbon.Bottom.Start), true, false);
            context.BezierTo(ToPoint(ribbon.Bottom.Control1), ToPoint(ribbon.Bottom.Control2), ToPoint(ribbon.Bottom.End), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point ToPoint(RibbonPoint point) => new(point.X, point.Y);

    /// <summary>Converts a Y position of an editor text view into this element's coordinates.</summary>
    private double ToLocal(DiffTextEditor editor, double y)
    {
        try
        {
            var view = editor.TextArea.TextView;
            if (!view.IsVisible || !IsVisible) return y;
            return view.TransformToVisual(this).Transform(new Point(0, y)).Y;
        }
        catch (InvalidOperationException)
        {
            return y;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        var button = _buttons.FindIndex(entry => entry.Bounds.Contains(position));
        var link = button >= 0 ? _buttons[button].Link : HitTestRibbon(position);
        if (button == _hoveredButton && ReferenceEquals(link, _hoveredLink)) return;
        _hoveredButton = button;
        _hoveredLink = link;
        Cursor = button >= 0 || link is not null ? Cursors.Hand : Cursors.Arrow;
        ToolTip = button >= 0
            ? _buttons[button].Direction == ConnectorDirection.ToRight ? "Copy this change to the right (Alt+Right)" : "Copy this change to the left (Alt+Left)"
            : link?.Tooltip;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredButton = -1;
        _hoveredLink = null;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var position = e.GetPosition(this);
        var button = _buttons.FindIndex(entry => entry.Bounds.Contains(position));
        if (button >= 0)
        {
            var (_, link, direction) = _buttons[button];
            Action?.Invoke(this, new ConnectorActionEventArgs(link, direction));
            e.Handled = true;
            return;
        }
        var hit = HitTestRibbon(position);
        if (hit is null) return;
        LinkActivated?.Invoke(this, hit);
        e.Handled = true;
    }

    private ConnectorLink? HitTestRibbon(Point position)
    {
        for (var i = _shapes.Count - 1; i >= 0; i--)
            if (_shapes[i].Shape.FillContains(position)) return _shapes[i].Link;
        return null;
    }
}
