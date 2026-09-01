using System.Windows;
using System.Windows.Media;
using NaraDiff.App.Services;
using NaraDiff.Core.Folders;
using NaraDiff.Infrastructure.IO;
using NaraDiff.Infrastructure.Logging;

namespace NaraDiff.App.Views;

/// <summary>One row of the synchronisation preview.</summary>
public sealed class SyncActionRow
{
    public required string Direction { get; init; }

    public required string Path { get; init; }

    public required string Target { get; init; }

    public required string Size { get; init; }

    public required string Reason { get; init; }

    public required Brush Accent { get; init; }
}

/// <summary>
/// The mandatory dry run before any file is copied or deleted. The plan is recalculated whenever an
/// option changes, deletions need a separate confirmation, and nothing happens until Run is pressed.
/// </summary>
public partial class SyncPreviewWindow : Window
{
    private static readonly (string Label, SyncDirection Direction)[] Directions =
    [
        ("Left to right", SyncDirection.LeftToRight),
        ("Right to left", SyncDirection.RightToLeft),
        ("Both ways (newer wins)", SyncDirection.Bidirectional)
    ];

    private readonly FolderComparisonResult _comparison;
    private readonly FileLogger _logger;
    private SyncPlan? _plan;
    private bool _ready;

    public SyncPreviewWindow(FolderComparisonResult comparison, SyncOptions options, FileLogger logger)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _comparison = comparison;
        _logger = logger;
        InitializeComponent();
        foreach (var (label, _) in Directions) DirectionBox.Items.Add(label);
        DirectionBox.SelectedIndex = Math.Max(0, Array.FindIndex(Directions, entry => entry.Direction == options.Direction));
        CopyMissingBox.IsChecked = options.CopyMissing;
        OverwriteBox.IsChecked = options.OverwriteDifferent;
        DeleteBox.IsChecked = options.DeleteOrphans;
        ResultOptions = options;
        _ready = true;
        Rebuild();
    }

    public SyncOptions ResultOptions { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Rebuild();
    }

    private void Rebuild()
    {
        ResultOptions = new SyncOptions
        {
            Direction = Directions[Math.Max(0, DirectionBox.SelectedIndex)].Direction,
            CopyMissing = CopyMissingBox.IsChecked == true,
            OverwriteDifferent = OverwriteBox.IsChecked == true,
            DeleteOrphans = DeleteBox.IsChecked == true
        };
        _plan = SyncPlanner.Create(_comparison, ResultOptions);
        var palette = ThemeService.Palette;
        ActionList.ItemsSource = _plan.Actions.Select(action => new SyncActionRow
        {
            Direction = action.DirectionText,
            Path = action.RelativePath,
            Target = action.TargetPath,
            Size = action.IsDirectory ? string.Empty : $"{action.Bytes:N0}",
            Reason = action.Reason + (action.Overwrites && !action.IsDelete ? " (replaces the target file)" : string.Empty),
            Accent = action.IsDelete ? palette.DeleteStroke : action.Overwrites ? palette.ModifyStroke : palette.InsertStroke
        }).ToList();
        SummaryText.Text = _plan.IsEmpty
            ? "Nothing to do: the folders already match the selected rules."
            : $"{_plan.CopyCount:N0} files to copy ({_plan.TotalBytes:N0} bytes), {_plan.DirectoryCount:N0} folders to create, " +
              $"{_plan.OverwriteCount:N0} files would be replaced and {_plan.DeleteCount:N0} deleted.";
        ConfirmDeleteBox.Visibility = _plan.DeleteCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_plan.DeleteCount == 0) ConfirmDeleteBox.IsChecked = false;
        RunButton.IsEnabled = !_plan.IsEmpty && (_plan.DeleteCount == 0 || ConfirmDeleteBox.IsChecked == true);
        StatusText.Text = _plan.DeleteCount > 0 && ConfirmDeleteBox.IsChecked != true
            ? "Confirm the deletions to enable Run."
            : string.Empty;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _plan.IsEmpty) return;
        var allowDeletions = ConfirmDeleteBox.IsChecked == true;
        var question = $"Run {_plan.Actions.Count:N0} operations?" +
                       (_plan.OverwriteCount > 0 ? $"\n{_plan.OverwriteCount:N0} existing files will be replaced." : string.Empty) +
                       (_plan.DeleteCount > 0 ? $"\n{_plan.DeleteCount:N0} files or folders will be deleted." : string.Empty);
        if (MessageBox.Show(this, question, "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        RunButton.IsEnabled = false;
        StatusText.Text = "Running ... ";
        try
        {
            var report = await new DirectorySyncExecutor().ExecuteAsync(_plan, allowDeletions, new Progress<string>(path => StatusText.Text = path));
            Summary = $"Synchronisation finished: {report.SucceededCount:N0} succeeded, {report.FailedCount:N0} failed.";
            if (report.FailedCount > 0)
            {
                var details = string.Join(Environment.NewLine, report.Failures.Take(12).Select(failure => $"{failure.Action.RelativePath}: {failure.Error}"));
                MessageBox.Show(this, $"{Summary}{Environment.NewLine}{Environment.NewLine}{details}", "NaraDiff", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _logger.Error("folder-sync", ex);
            MessageBox.Show(this, $"The synchronisation failed: {ex.Message}", "NaraDiff", MessageBoxButton.OK, MessageBoxImage.Error);
            RunButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = DialogResult ?? false;
}
