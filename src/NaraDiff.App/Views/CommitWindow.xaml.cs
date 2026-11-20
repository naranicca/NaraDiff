using System.ComponentModel;
using System.Windows;
using NaraDiff.App.Services;

namespace NaraDiff.App.Views;

/// <summary>Collects a commit message and the files to include; the caller runs the commit.</summary>
public partial class CommitWindow : Window
{
    private readonly List<CommitItem> _items;

    internal CommitWindow(string? branch, IReadOnlyList<GitStatusEntry> changes, IReadOnlySet<string> preselected, bool pushAfterCommit)
    {
        InitializeComponent();
        BranchText.Text = branch is null ? "HEAD is detached" : $"On branch {branch}";
        // The caller decides what starts ticked; the user can change it here before committing.
        _items = changes.Select(change => new CommitItem(change, preselected.Contains(change.Path))).ToList();
        FileList.ItemsSource = _items;
        PushBox.IsChecked = pushAfterCommit;
        PushBox.IsEnabled = branch is not null;
        UpdateState();
        Loaded += (_, _) => MessageInput.Focus();
    }

    /// <summary>The changes the user chose to commit.</summary>
    internal IReadOnlyList<GitStatusEntry> SelectedChanges => _items.Where(item => item.IsChecked).Select(item => item.Change).ToList();

    public string Message => MessageInput.Text.Trim();

    public bool PushAfterCommit => PushBox.IsChecked == true && PushBox.IsEnabled;

    private void UpdateState()
    {
        var count = _items.Count(item => item.IsChecked);
        CountText.Text = $"{count:N0} of {_items.Count:N0} selected";
        AllBox.IsChecked = count == _items.Count && count > 0;
        CommitButton.IsEnabled = count > 0 && MessageInput.Text.Trim().Length > 0;
    }

    private void Message_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateState();

    private void Item_Click(object sender, RoutedEventArgs e) => UpdateState();

    private void All_Click(object sender, RoutedEventArgs e)
    {
        var check = AllBox.IsChecked == true;
        foreach (var item in _items) item.IsChecked = check;
        UpdateState();
    }

    private void Commit_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public sealed class CommitItem(GitStatusEntry change, bool isChecked) : INotifyPropertyChanged
    {
        private bool _isChecked = isChecked;

        public GitStatusEntry Change { get; } = change;

        public string Path => Change.OriginalPath is null ? Change.Path : $"{Change.OriginalPath} → {Change.Path}";

        public string Status => Change.StageText.Length == 0 ? Change.Description : $"{Change.Description}, {Change.StageText}";

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
