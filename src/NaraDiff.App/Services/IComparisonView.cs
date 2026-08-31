using NaraDiff.Core.Diff;
using NaraDiff.Core.Settings;

namespace NaraDiff.App.Services;

/// <summary>
/// What the main window needs from every kind of comparison tab, so the toolbar and the shortcuts
/// work the same for file comparisons, merges and folder comparisons.
/// </summary>
public interface IComparisonView
{
    /// <summary>Short text for the tab header.</summary>
    string Title { get; }

    /// <summary>One line summary for the status bar.</summary>
    string StatusText { get; }

    event EventHandler? TitleChanged;

    event EventHandler? StatusChanged;

    bool HasUnsavedChanges { get; }

    bool CanApplyChanges { get; }

    void ApplySettings(AppSettings settings);

    void ApplyDiffOptions(DiffOptions options);

    Task RefreshAsync();

    Task<bool> SaveAsync();

    void NextChange();

    void PreviousChange();

    void ApplyToRight();

    void ApplyToLeft();

    void ApplyAllToRight();

    void ApplyAllToLeft();

    void FocusSearch();

    void Close();
}