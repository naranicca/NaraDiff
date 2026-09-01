using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NaraDiff.Core.Settings;
using NaraDiff.Core.Text;

namespace NaraDiff.App.Controls;

/// <summary>
/// The bar above one editor: the path, the encoding, the line ending that will be written, and the
/// read-only and modified indicators.
/// </summary>
public partial class FilePaneHeader : UserControl
{
    private static readonly (string Label, SaveLineEndingMode Mode)[] LineEndingModes =
    [
        ("Keep as is", SaveLineEndingMode.Preserve),
        ("LF", SaveLineEndingMode.Lf),
        ("CRLF", SaveLineEndingMode.CrLf),
        ("CR", SaveLineEndingMode.Cr)
    ];

    private bool _suppressEvents;

    public FilePaneHeader()
    {
        InitializeComponent();
        foreach (var choice in EncodingCatalog.All) EncodingBox.Items.Add(choice);
        foreach (var (label, _) in LineEndingModes) LineEndingBox.Items.Add(label);
        EncodingBox.SelectedIndex = 0;
        LineEndingBox.SelectedIndex = 0;
    }

    public event EventHandler? BrowseRequested;

    public event EventHandler? ReloadRequested;

    public event EventHandler? SaveRequested;

    public event EventHandler<string>? PathCommitted;

    public event EventHandler<EncodingChoice>? EncodingChanged;

    public event EventHandler<SaveLineEndingMode>? LineEndingChanged;

    public string Role
    {
        get => RoleText.Text;
        set => RoleText.Text = value;
    }

    public string PathText
    {
        get => PathBox.Text;
        set
        {
            _suppressEvents = true;
            PathBox.Text = value;
            PathBox.ToolTip = string.IsNullOrEmpty(value) ? "File path; press Enter to load" : value;
            _suppressEvents = false;
        }
    }

    public EncodingChoice SelectedEncoding => EncodingBox.SelectedItem as EncodingChoice ?? EncodingCatalog.Utf8;

    public SaveLineEndingMode SelectedLineEndingMode => LineEndingModes[Math.Max(0, LineEndingBox.SelectedIndex)].Mode;

    public bool CanSave
    {
        get => SaveButton.IsEnabled;
        set => SaveButton.IsEnabled = value;
    }

    public void SetEncoding(EncodingChoice choice)
    {
        _suppressEvents = true;
        EncodingBox.SelectedItem = EncodingCatalog.All.FirstOrDefault(item => item.Id == choice.Id) ?? EncodingCatalog.Utf8;
        _suppressEvents = false;
    }

    public void SetLineEndingMode(SaveLineEndingMode mode)
    {
        _suppressEvents = true;
        var index = Array.FindIndex(LineEndingModes, entry => entry.Mode == mode);
        LineEndingBox.SelectedIndex = index < 0 ? 0 : index;
        _suppressEvents = false;
    }

    /// <summary>Updates the informational text and the state chips.</summary>
    public void SetState(string detail, bool isReadOnly, bool isModified)
    {
        DetailText.Text = detail;
        ReadOnlyChip.Visibility = isReadOnly ? Visibility.Visible : Visibility.Collapsed;
        ModifiedChip.Visibility = isModified ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke(this, EventArgs.Empty);

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke(this, EventArgs.Empty);

    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PathCommitted?.Invoke(this, PathBox.Text);
        e.Handled = true;
    }

    private void PathBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => PathBox.ToolTip = string.IsNullOrEmpty(PathBox. Text) ? "File path; press Enter to load" : PathBox. Text;

    private void EncodingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        EncodingChanged?.Invoke(this, SelectedEncoding);
    }

    private void LineEndingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        LineEndingChanged?.Invoke(this, SelectedLineEndingMode);
    }
}