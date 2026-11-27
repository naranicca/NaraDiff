using System.Windows;
using System.Windows.Controls;

namespace NaraDiff.App.Controls;

public partial class ComparisonBusyIndicator : UserControl
{
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(ComparisonBusyIndicator),
            new PropertyMetadata(string.Empty));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public ComparisonBusyIndicator() => InitializeComponent();
}
