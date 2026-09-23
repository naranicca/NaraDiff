using System.Windows

namespace NaraDiff.App.Controls;

/// <summary>
/// Attached to a ScrollViewer to put its vertical scroll bar on the left instead of the right. Done
/// by swapping which Grid column each part occupies in the ScrollViewer's own template (see
/// Shared.xaml), rather than mirroring the control with FlowDirection.RightToLeft: that trick also
/// flips the coordinate space a stylus reports through GetPosition, which reverses the direction of
/// pen-drag panning on the mirrored editor while mouse dragging stays correct.
/// </summary>
public static class ScrollBarPlacement
{
    public static readonly DependencyProperty IsOnLeftProperty =
        DependencyProperty.RegisterAttached("IsOnLeft", typeof(bool), typeof(ScrollBarPlacement),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static void SetIsOnLeft(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsOnLeftProperty, value);
    }

    public static bool GetIsOnLeft(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsOnLeftProperty);
    }
}
