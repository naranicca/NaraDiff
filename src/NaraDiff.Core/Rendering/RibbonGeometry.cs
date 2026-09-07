namespace NaraDiff.Core.Rendering;

public readonly record struct RibbonPoint(double X, double Y);

/// <summary>One cubic Bezier segment.</summary>
public readonly record struct RibbonCurve(RibbonPoint Start, RibbonPoint Control1, RibbonPoint Control2, RibbonPoint End);

/// <summary>
/// The closed shape that links a changed block in the left editor with its counterpart in the right
/// editor: a curve along the top, a straight edge down the right side, a curve back along the
/// bottom, and a straight edge up the left side.
/// </summary>
public readonly record struct Ribbon(RibbonCurve Top, RibbonCurve Bottom)
{
    public RibbonPoint LeftTop => Top.Start;

    public RibbonPoint RightTop => Top.End;

    public RibbonPoint RightBottom => Bottom.Start;

    public RibbonPoint LeftBottom => Bottom.End;

    /// <summary>Vertical centre of the shape, used to place the hunk action buttons.</summary>
    public double CenterY => (LeftTop.Y + LeftBottom.Y + RightTop.Y + RightBottom.Y) / 4;
}

public static class RibbonGeometry
{
    /// <summary>Fraction of the width at which the control points sit; 0.5 gives a symmetric S curve.</summary>
    public const double DefaultCurvature = 0.5;

    /// <summary>Height used for a block that has no lines on one side, so the ribbon stays visible.</summary>
    public const double MinimumThickness = 1.0;

    /// <summary>
    /// Builds the ribbon between a left range and a right range. Ranges of different heights are
    /// connected smoothly because the top and bottom curves are computed independently.
    /// </summary>
    public static Ribbon Build(double leftTop, double leftBottom, double rightTop, double rightBottom, double width, double curvature = DefaultCurvature)
    {
        if (double.IsNaN(width) || width < 0) width = 0;
        curvature = Math.Clamp(curvature, 0, 1);
        (leftTop, leftBottom) = Inflate(leftTop, leftBottom);
        (rightTop, rightBottom) = Inflate(rightTop, rightBottom);
        var nearControl = width * curvature;
        var farControl = width * (1 - curvature);
        var top = new RibbonCurve(
            new RibbonPoint(0, leftTop),
            new RibbonPoint(nearControl, leftTop),
            new RibbonPoint(farControl, rightTop),
            new RibbonPoint(width, rightTop));
        var bottom = new RibbonCurve(
            new RibbonPoint(width, rightBottom),
            new RibbonPoint(farControl, rightBottom),
            new RibbonPoint(nearControl, leftBottom),
            new RibbonPoint(0, leftBottom));
        return new Ribbon(top, bottom);
    }

    /// <summary>Gives an empty range a small visible height, centred on its position.</summary>
    private static (double Top, double Bottom) Inflate(double top, double bottom)
    {
        if (bottom < top) (top, bottom) = (bottom, top);
        var height = bottom - top;
        if (height >= MinimumThickness) return (top, bottom);
        var padding = 0;//(MinimumThickness - height) / 2;
        return (Math.Round(top - padding), Math.Round(bottom + padding));
    }

    /// <summary>Samples a point on a cubic Bezier curve; used for hit testing and for tests.</summary>
    public static RibbonPoint Evaluate(RibbonCurve curve, double t)
    {
        t = Math.Clamp(t, 0, 1);
        var u = 1 - t;
        var x = u * u * u * curve.Start.X + 3 * u * u * t * curve.Control1.X + 3 * u * t * t * curve.Control2.X + t * t * t * curve.End.X;
        var y = u * u * u * curve.Start.Y + 3 * u * u * t * curve.Control1.Y + 3 * u * t * t * curve.Control2.Y + t * t * t * curve.End.Y;
        return new RibbonPoint(x, y);
    }

    /// <summary>
    /// Maps a line number to the Y position of an overview ruler, and back, so that clicking the
    /// ruler scrolls to the right place.
    /// </summary>
    public static double LineToRulerY(int line, int lineCount, double height) =>
        lineCount <= 1 ? 0 : Math.Clamp(line / (double)Math.Max(1, lineCount - 1) * height, 0, height);

    public static int RulerYToLine(double y, int lineCount, double height)
    {
        if (height <= 0 || lineCount <= 1) return 0;
        var ratio = Math.Clamp(y / height, 0, 1);
        return (int)Math.Round(ratio * (lineCount - 1));
    }
}
