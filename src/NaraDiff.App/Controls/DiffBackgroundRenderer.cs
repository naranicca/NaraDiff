using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace NaraDiff.App.Controls;

/// <summary>
/// Paints changed line backgrounds, the coloured edge bar of each hunk, the inline word ranges, the
/// search matches and the current line. Only the visible lines are drawn, so the cost does not grow
/// with the size of the file.
/// </summary>
public sealed class DiffBackgroundRenderer : IBackgroundRenderer
{
    private static readonly Pen NoPen = CreatePen(Brushes.Transparent, 0);

    public DiffDecorationSet Decorations { get; set; } = DiffDecorationSet.Empty;

    public DiffDecorationSet SearchMatches { get; set; } = DiffDecorationSet.Empty;

    public Brush? CurrentLineBrush { get; set; }

    public bool ShowCurrentLine { get; set; } = true;

    public int CurrentLine { get; set; } = -1;

    public KnownLayer Layer => KnownLayer.Background;
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(textView);
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (textView.VisualLinesValid is false || textView.Document is null) return;
        var width = Math.Max(textView.ActualWidth, 0);
        var scroll = textView.ScrollOffset.Y;
        VisualLine? lastVisualLine = null;
        foreach (var visualLine in textView.VisualLines)
        {
            var documentLine = visualLine.FirstDocumentLine;
            var lineIndex = documentLine.LineNumber - 1;
            var top = visualLine.VisualTop - scroll;
            var height = visualLine.Height;
            if (ShowCurrentLine && lineIndex == CurrentLine && CurrentLineBrush is not null)
                drawingContext.DrawRectangle(CurrentLineBrush, null, new Rect(0, top, width, height));
            if (Decorations.TryGet(lineIndex, out var decoration) && decoration is not null)
            {
                drawingContext.DrawRectangle(decoration.Fill, null, new Rect(0, top, width, height));
                if (decoration.EdgeStroke is not null)
                {
                    var pen = new Pen(decoration.EdgeStroke, 1.0) { LineJoin = PenLineJoin.Round };
                    pen.Freeze();
                    if (decoration.IsBlockStart) drawingContext.DrawLine(pen, new Point(0, top), new Point(width, top));
                    if (decoration.IsBlockEnd) drawingContext.DrawLine(pen, new Point(0, top + height - 1), new Point(width, top + height - 1));
                }
                DrawInline(textView, drawingContext, documentLine, decoration);
            }
            if (SearchMatches.TryGet(lineIndex, out var match) && match is not null) DrawInline(textView, drawingContext, documentLine, match);
            if (Decorations.TryGetBoundaryMarker(lineIndex, out var markerBrush) && markerBrush is not null) DrawBoundaryMarker(drawingContext, markerBrush, top, width);
            lastVisualLine = visualLine;
        }
        if (lastVisualLine is not null &&
            Decorations.TryGetBoundaryMarker(textView.Document.LineCount, out var endMarkerBrush) && endMarkerBrush is not null)
            DrawBoundaryMarker(drawingContext, endMarkerBrush, lastVisualLine.VisualTop - scroll + lastVisualLine.Height, width);
    }

    private static void DrawBoundaryMarker(DrawingContext drawingContext, Brush brush, double boundaryY, double width)
    {
        const double thickness = 2;
        drawingContext.DrawRectangle(brush, null, new Rect(0, Math.Round(boundaryY - thickness / 2), width, thickness));
    }

    private static void DrawInline(TextView textView, DrawingContext drawingContext, DocumentLine documentLine, LineDecoration decoration)
    {
        if (decoration.Inline is null || decoration.InlineBrush is null) return;
        foreach (var span in decoration.Inline)
        {
            var start = documentLine.Offset + Math.Max(0, span.Start);
            var end = Math.Min(documentLine.EndOffset, documentLine.Offset + span.End);
            if (end <= start) continue;
            var segment = new TextSegment { StartOffset = start, EndOffset = end };
            foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            drawingContext.DrawRoundedRectangle(decoration.InlineBrush, NoPen, rectangle, 2, 2);
        }
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}