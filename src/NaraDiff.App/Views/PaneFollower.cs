using NaraDiff.App.Controls;

namespace NaraDiff.App.Views;

internal sealed class PaneFollower
{
    private DiffTextEditor? _leader;
    private double _anchorOffset;
    private double _anchorEdge;
    private double _correction;
    private double _lastRequested = double.NaN;

    /// <summary>Forgets the current relation, so the next step starts again from wherever the follower is.</summary>
    public void Reset() => _lastRequested = double.NaN;

    /// <summary>True while the follower is still where the last step from <paramref name="leader"/> put it.</summary>
    public bool IsTracking(DiffTextEditor leader, DiffTextEditor follower) =>
        ReferenceEquals(_leader, leader) && !double.IsNaN(_lastRequested)
        && Math.Abs(follower.TextArea.TextView.ScrollOffset.Y - _lastRequested) < 1;

    /// <summary>
    /// The offset for <paramref name="follower"/> after the leader moved from <paramref name="previousOffset"/>
    /// to <paramref name="offset"/>; the same two values mean the leader did not move but the layout did.
    /// </summary>
    public double Step(DiffTextEditor leader, DiffTextEditor follower, double previousOffset, double offset, Func<double, double> canonical)
    {
        var current = follower.TextArea.TextView.ScrollOffset.Y;
        if (!IsTracking(leader, follower))
        {
            _leader = leader;
            _anchorOffset = previousOffset;
            _anchorEdge = EdgeDistance(leader, previousOffset);
            _correction = current - canonical(previousOffset);
        }
        var target = canonical(offset) + _correction * Weight(leader, offset);
        var delta = offset - previousOffset;
        if (delta > 0 && target < current) target = current;
        else if (delta < 0 && target > current) target = current;
        target = follower.ClampToReachable(target);
        _lastRequested = target;
        return target;
    }

    /// <summary>
    /// How much of the kept difference still applies: all of it where it was taken, none a screen away,
    /// and none once the leader reaches the very top or bottom of its document, where the panes must show
    /// their starts (or ends) together.
    /// </summary>
    private double Weight(DiffTextEditor leader, double offset)
    {
        var page = leader.ScrollViewportHeight;
        if (page <= 0) return 0;
        var weight = Math.Max(0, 1 - Math.Abs(offset - _anchorOffset) / page);
        if (_anchorEdge > 0) weight *= Math.Clamp(EdgeDistance(leader, offset) / _anchorEdge, 0, 1);
        return weight;
    }

    /// <summary>How far the leader is from the nearer end of its scroll range, capped at half a screen; 0 when it cannot scroll.</summary>
    private static double EdgeDistance(DiffTextEditor leader, double offset)
    {
        var page = leader.ScrollViewportHeight;
        var range = leader.TextArea.TextView.DocumentHeight - page;
        if (page <= 0 || range <= 0) return 0;
        return Math.Clamp(Math.Min(offset, range - offset), 0, page / 2);
    }
}

