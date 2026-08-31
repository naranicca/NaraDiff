using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NaraDiff.App.Controls;
using NaraDiff.App.Services;
using NaraDiff.Core.Services;
using NaraDiff.Core.Settings;
using NaraDiff.Infrastructure.Logging;

namespace NaraDiff.App.Views;

/// <summary>
/// The application window: the toolbar, the comparison tabs, the options flyout and the status bar.
/// Every toolbar command is routed to the active tab through <see cref="IComparisonView"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ISettingsStore _store;
    private readonly FileLogger _logger;
    private readonly AppSettings _settings;
    private DiffOptionsPanel? _optionsPanel;

    public MainWindow(AppSettings settings, ISettingsStore store, FileLogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _store = store;
        _logger = logger;
        InitializeComponent();
        Width = Math.Max(760, settings.WindowWidth);
        Height = Math.Max(480, settings.WindowHeight);
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
        VersionText.Text = $"NaraDiff {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";
        OptionSummary.Text = settings.DiffOptions.Describe();
        Closing += MainWindow_Closing;
        SourceInitialized += (_,_) => WindowChrome.ApplyTheme(this, _settings.Theme);
        ThemeService.Changed +=(_, _) => WindowChrome.ApplyTheme(this, _settings.Theme);
    }

    private IComparisonView? ActiveView => (Tabs.SelectedItem as TabItem)?.Content as IComparisonView;

    /// <summary>
    /// Opens what the command line asked for: two files, two folders, or a three way merge
    /// (<c>NaraDiff base local remote [merged]</c>).
    /// </summary>
    public async Task HandleCommandLineAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var paths = args.Where(argument => !argument.StartsWith('-') && !argument.StartsWith('/')).ToList();
        var merge = args.Any(argument => argument is "-m" or " -- merge" or "/m");
        try
        {
            if (paths.Count >= 3 || (merge && paths.Count == 3))
            {
                await NewMergeAsync(paths[0], paths[1], paths[2], paths.Count > 3 ? paths[3] :paths[1]);
                return;
            }
            if (paths.Count == 2)
            {
                if (Directory.Exists(paths[0]) && Directory.Exists(paths[1])) await NewFolderCompareAsync(paths[0], paths[1]);
                else await NewFileCompareAsync(paths[0], paths[1]);
                return;
            }
            if (paths.Count == 1)
            {
                if (Directory.Exists(paths[0])) await NewFolderCompareAsync(paths[0], null);
                else await NewFileCompareAsync(paths[0], null);
                return;
            }
            await NewFileCompareAsync(null, null);
        }
        catch (Exception ex)
        {
            _logger.Error("command-line", ex);
            MessageBox.Show(this, $"The command line could not be opened: {ex.Message}", "NaraDiff", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- tabs ----------

    private TabItem AddTab(UserControl view, IComparisonView api)
    {
        var item = new TabItem { Content = view };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = api.Title, Maxwidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new IconButton
        {
            Icon= TryFindResource("IconClose") as System.Windows.Media.Geometry,
            Margin = new Thickness(6, 0, -4, 0),
            Height = 20,
            MinWidth = 20,
            ToolTip = "Close this comparison (Ctrl+W)"
        };
        close.Click += (_, _) => CloseTab(item);
        Grid.SetColumn(close, 1);
        header.Children.Add(title);
        header.Children.Add(close);
        item.Header = header;
        api. TitleChanged += (_, _) => title.Text = api.Title;
        api.StatusChanged += (_, _) =>
        {
            if (ReferenceEquals(ActiveView, api)) StatusText.Text = api.StatusText;
        };
        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        EmptyHint.Visibility = Visibility.Collapsed;
        UpdateCommandState();
        return item;
    }

    private void CloseTab(TabItem item)
    {
        if (item.Content is IComparisonView view)
        {
            if (view.HasUnsavedChanges &&
                MessageBox.Show(this, "This comparison has unsaved changes. Close it anyway?", "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            view.Close();
        }
        Tabs.Items.Remove(item);
        EmptyHint.Visibility = Tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = ActiveView?.StatusText ?? string.Empty;
        UpdateCommandState();
    }

    private async Task NewFileCompareAsync(string? left, string? right)
    {
        var view = new FileCompareView(_settings, logger);
        AddTab(view, view);
        await view.OpenAsync(left, right);
    }

    private async Task NewFolderCompareAsync(string? left, string? right)
    {
        var view = new FolderCompareView(_settings, _logger);
        view.FileComparisonRequested += async (_, pair) => await NewFileCompareAsync(pair.Left, pair.Right);
        AddTab(view, view);
        await view.OpenAsync(left, right);
    }

    private async Task NewMergeAsync(string? basePath, string? left, string? right, string? output)
    {
        var view = new MergeView(_settings, _logger);
        AddTab(view, view);
        await view.OpenAsync(basePath, left, right, output);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        StatusText.Text = ActiveView?.StatusText ?? string.Empty;
        UpdateCommandState();
    }

    /// <summary>The copy commands are only enabled for views that can apply changes.</summary>
    private void UpdateCommandState()
    {
        var canApply = ActiveView?.CanApplyChanges == true;
        ToRightButton.IsEnabled = canApply;
        ToLeftButton. IsEnabled = canApply;
        AllToRightButton.IsEnabled = canApply;
        AllToLeftButton. IsEnabled = canApply;
    }

    private async void NewFileCompare_Click(object sender, RoutedEventArgs e)
    {
        var left = PickFile("Select the left file");
        if (left is null)
        {
            await NewFileCompareAsync(null, null);
            return;
        }
        var right = PickFile("Select the right file", Path.GetDirectoryName(left));
        await NewFileCompareAsync(left, right);
    }

    private async void NewFolderCompare_Click(object sender, RoutedEventArgs e)
    {
        var left = PickFolder("Select the left folder");
        var right = left is null ? null : PickFolder("Select the right folder");
        await NewFolderCompareAsync(left, right);
    }

    private async void NewMerge_Click(object sender, RoutedEventArgs e)
    {
        var basePath = PickFile("Select the base (common ancestor) file");
        if (basePath is null)
        {
            await NewMergeAsync(null, null, null, null);
            return;
        }
        var directory = Path.GetDirectoryName(basePath);
        var left = PickFile("Select the left (mine) file", directory);
        var right = PickFile("Select the right (theirs) file", directory);
        await NewMergeAsync(basePath, left, right, left);
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await NewFileCompareAsync(PickFile("Select the left file"), null);

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveView is null) return;
        await ActiveView.SaveAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveView is null) return;
        await ActiveView.RefreshAsync();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => ActiveView?.PreviousChange();

    private void Next_Click(object sender, RoutedEventArgs e) => ActiveView?.NextChange();

    private void ToRight_Click(object sender, RoutedEventArgs e) => ActiveView?.ApplyToRight();

    private void ToLeft_Click(object sender, RoutedEventArgs e) => ActiveView?.ApplyToLeft();

    private void AllToRight_Click(object sender, RoutedEventArgs e) => ActiveView?.ApplyAllToRight();

    private void AllToLeft_Click(object sender, RoutedEventArgs e) => ActiveView?.ApplyAllToLeft();

    private void Search_Click(object sender, RoutedEventArgs e) => ActiveView?.FocusSearch();

    private void OptionsToggle_Click(object sender, RoutedEventArgs e) => ShowOptions(OptionsToggle.IsChecked == true);

    private void ShowOptions(bool visible)
    {
        OptionsToggle.IsChecked = visible;
        if (!visible)
        {
            OptionsHost.Visibility = Visibility.Collapsed;
            return;
        }
        if (_optionsPanel is null)
        {
            optionsPanel = new DiffOptionsPanel(_settings);
            optionsPanel.OptionsChanged += (_, options) =>
            {
            OptionSummary.Text = options.Describe();
            foreach (var item in Tabs.Items.OfType<TabItem>())
                if (item.Content is IComparisonView view) view.ApplyDiffOptions(options);
            };
            _optionsPanel.CloseRequested += (_, _) => ShowOptions(false);
            OptionsHost.Content = _optionsPanel;
        }
        _optionsPanel.Load(_settings.DiffOptions);
        OptionsHost.Visibility = Visibility.Visible;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        window.SettingsApplied += (_, _) =>
        {
            foreach (var item in Tabs.Items.OfType<TabItem>())
                if (item.Content is IComparisonView view) view.ApplySettings(_settings);
        };
        window.ShowDialog();
        _ = _store.SaveAsync(_settings);
    }

    // ---------- keyboard ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        switch (e.Key)
        {
            case Key.F7 when !shift:
                ActiveView?.NextChange();
                break;
            case Key.F7 when shift:
                ActiveView?.PreviousChange();
                break;
            case Key.F8 when ActiveView is MergeView merge && !shift:
                merge.NextConflict();
                break;
            case Key.F8 when ActiveView is MergeView previous && shift:
                previous.PreviousConflict();
                break;
            case Key.F5:
                _ = ActiveView?.RefreshAsync();
                break;
            case Key.S when control:
                _ = ActiveView?.SaveAsync();
                break;
            case Key.F when control:
                ActiveView?.FocusSearch();
                break;
            case Key.P when control:
                ShowOptions(OptionsToggle. IsChecked != true);
                break;
            case Key.N when control && shift:
                NewFolderCompare_Click(sender, e);
                break;
            case Key.N when control:
                NewFileCompare_Click(sender, e);
                break;
            case Key.M when control:
                NewMerge_Click(sender, e);
                break;
            case Key.W when control:
                if (Tabs.SelectedItem is TabItem item) CloseTab(item);
                break;
            case Key.Right when alt && control:
                if (ActiveView?.CanApplyChanges == true) ActiveView.ApplyAllToRight();
                break;
            case Key.Left when alt && control:
                if (ActiveView?.CanApplyChanges == true) ActiveView.ApplyAllToLeft();
                break;
            case Key.Right when alt:
                if (ActiveView?.CanApplyChanges == true) ActiveView.ApplyToRight();
                break;
            case Key.Left when alt:
                if (ActiveView?.CanApplyChanges == true) ActiveView.ApplyToLeft();
                break;
            case Key.D1 when alt && ActiveView is MergeView left:
                left.TakeLeft();
                break;
            case Key.D2 when alt && ActiveView is MergeView baseView:
                baseView.TakeBase();
                break;
            case Key.D3 when alt && ActiveView is MergeView right:
                right.TakeRight();
                break;
            case Key.B when alt && ActiveView is MergeView both:
                both. TakeBoth();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private string? PickFile(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog { Title = title, CheckFileExists = true };
        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        else if (_settings.RecentFiles.Count > 0)
        {
            var directory = Path.GetDirectoryName(_settings.RecentFiles[0]);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        }
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { title = title };
        if (_settings.RecentFolders.Count > 0 && Directory.Exists(_settings.RecentFolders[0])) dialog.InitialDirectory = _settings.RecentFolders[0];
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var unsaved = Tabs.Items.OfType<TabItem>().Select(item => item.Content).OfType<IComparisonView>().Count(view => view.HasUnsavedChanges);
        if (unsaved > 0 &&
            MessageBox.Show(this, $"{unsaved} comparison(s) have unsaved changes. Exit anyway?", "NaraDiff", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        foreach (var view in Tabs.Items.OfType<TabItem>().Select(item => item.Content).OfType<IComparisonView>()) view.Close();
        // Saving has to finish before the process ends, but waiting on the dispatcher thread would
        // deadlock the continuations, so the write runs on the thread pool.
        try
        {
            Task.Run(() => _store.SaveAsync(_settings)).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            logger.Error("settings-save", ex);
        }
    }
}