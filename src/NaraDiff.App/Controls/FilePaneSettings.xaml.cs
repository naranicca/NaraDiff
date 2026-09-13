using System.Windows;
using System.Windows.Controls;
using NaraDiff.Core.Settings;
using NaraDiff.Core.Text;

namespace NaraDiff.App.Controls;

public partial class FilePaneSettings : UserControl
{
    private static readonly (string Label, SaveLineEndingMode Mode)[] LineEndingModes = [("Keep as is", SaveLineEndingMode.Preserve), ("LF", SaveLineEndingMode.Lf), ("CRLF", SaveLineEndingMode.CrLf), ("CR", SaveLineEndingMode.Cr)];
    private bool _suppressEvents;
    public FilePaneSettings()
    {
        InitializeComponent();
        foreach (var choice in EncodingCatalog.All) EncodingBox.Items.Add(choice);
        foreach (var (label, _) in LineEndingModes) LineEndingBox.Items.Add(label);
        EncodingBox.SelectedIndex = 0;
        LineEndingBox.SelectedIndex = 0;
    }
    public event EventHandler<EncodingChoice>? EncodingChanged;
    public event EventHandler<SaveLineEndingMode>? LineEndingChanged;
    public EncodingChoice SelectedEncoding => EncodingBox.SelectedItem as EncodingChoice ?? EncodingCatalog.Utf8;
    public SaveLineEndingMode SelectedLineEndingMode => LineEndingModes[Math.Max(0, LineEndingBox.SelectedIndex)].Mode;
    public void SetEncoding(EncodingChoice choice) { _suppressEvents = true; EncodingBox.SelectedItem = EncodingCatalog.All.FirstOrDefault(item => item.Id == choice.Id) ?? EncodingCatalog.Utf8; _suppressEvents = false; }
    public void SetLineEndingMode(SaveLineEndingMode mode) { _suppressEvents = true; LineEndingBox.SelectedIndex = Math.Max(0, Array.FindIndex(LineEndingModes, entry => entry.Mode == mode)); _suppressEvents = false; }
    public void SetState(string detail, bool isReadOnly, bool isModified) { DetailText.Text = detail; ReadOnlyChip.Visibility = isReadOnly ? Visibility.Visible : Visibility.Collapsed; ModifiedChip.Visibility = isModified ? Visibility.Visible : Visibility.Collapsed; }
    private void EncodingBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if ( !_suppressEvents) EncodingChanged ?. Invoke(this, SelectedEncoding); }
    private void LineEndingBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if ( !_suppressEvents) LineEndingChanged ?. Invoke(this, SelectedLineEndingMode); }
}
