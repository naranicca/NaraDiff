using System.DirectoryServices.ActiveDirectory;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace NaraDiff.App.Controls;

/// <summary>
/// Paints the changed-line background below the line numbers, bridging a connector that terminates
/// at the editor edge to the normal diff background in the text view.
/// </summary>
internal sealed class DiffLineNumberMargin(DiffBackgroundRenderer renderer) : LineNumberMargin
{
    protected override void OnRender(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView?.VisualLinesValid == true && textView.Document is not null)
        {
            // The right margin is deliberate breathing room between the line number and text.
            // Paint through it so a changed block remains visually continuous.
            var width = Math.Max(ActualWidth + Margin.Right, 0);
            var scroll = textView.ScrollOffset.Y;
            VisualLine? lastVisualLine = null;
            foreach (var visualLine in textView.VisualLines)
            {
                var lineIndex = visualLine.FirstDocumentLine.LineNumber - 1;
                var topRaw = visualLine.VisualTop - scroll;
                var top = Math.Round(topRaw, MidpointRounding.AwayFromZero);
                if (renderer.Decorations.TryGet(lineIndex, out var decoration) && decoration is not null)
                {
                    var height = Math.Round(topRaw + visualLine.Height, MidpointRounding.AwayFromZero) - top;
                    drawingContext.DrawRectangle(decoration.Fill, null, new Rect(0, top, width, height));
                    if (decoration.EdgeStroke is not null)
                    {
                        var pen = new Pen(decoration.EdgeStroke, 1.0);
                        pen.Freeze();
                        if (decoration.IsBlockStart)
                            drawingContext.DrawLine(pen, new Point(0, DiffBackgroundRenderer.SnapRowAfterBoundary(topRaw)), new Point(width, DiffBackgroundRenderer.SnapRowAfterBoundary(topRaw)));
                        if (decoration.IsBlockEnd)
                            drawingContext.DrawLine(pen, new Point(0, DiffBackgroundRenderer.SnapRowBeforeBoundary(topRaw + visualLine.Height)), new Point(width, DiffBackgroundRenderer.SnapRowBeforeBoundary(topRaw + visualLine.Height)));
                    }
                }
                if (renderer.Decorations.TryGetBoundaryMarker(lineIndex, out var markerBrush) && markerBrush is not null)
                    DrawBoundaryMarker(drawingContext, markerBrush, top, width);
                lastVisualLine = visualLine;
            }
            if (lastVisualLine is not null && renderer.Decorations.TryGetBoundaryMarker(textView.Document.LineCount, out var endMarkerBrush) && endMarkerBrush is not null)
                DrawBoundaryMarker(drawingContext, endMarkerBrush, DiffBackgroundRenderer.SnapRowAfterBoundary(lastVisualLine.VisualTop - scroll), width);
        }
        base.OnRender(drawingContext);
    }

    private static void DrawBoundaryMarker(DrawingContext drawingContext, Brush brush, double y, double width)
    {
        drawingContext.DrawRectangle(brush, null, new Rect(0, y, width, 2));
    }
}
