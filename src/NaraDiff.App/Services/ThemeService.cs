using System.Windows;
using NaraDiff.Core.Settings;

namespace NaraDiff.App.Services;

/// <summary>
/// Swaps the theme resource dictionaries and publishes the diff palette. Custom drawn controls
/// listen to <see cref="Changed"/> and redraw; templated controls follow the dynamic resources.
/// </summary>
public static class ThemeService
{
    private const string SharedUri = "pack://application:,,,/NaraDiff;component/Themes/Shared.xaml";
    private const string DarkUri = "pack://application:,,,/NaraDiff;component/Themes/Dark.xaml";
    private const string LightUri = "pack://application:,,,/NaraDiff;component/Themes/Light.xaml";

    public static event EventHandler? Changed;

    public static ThemeKind Current { get; private set; } = ThemeKind.Dark;

    public static bool AccessiblePalette { get; private set; }

    public static DiffPalette Palette { get; private set; } = DiffPalette.Create(ThemeKind.Dark, false);

    public static void Apply(ThemeKind theme, bool accessiblePalette)
    {
        Current = theme;
        AccessiblePalette = accessiblePalette;
        Palette = DiffPalette.Create(theme, accessiblePalette);
        var application = Application.Current;
        if (application is not null)
        {
            var dictionaries = application.Resources.MergedDictionaries;
            dictionaries.Clear();
            dictionaries.Add(new ResourceDictionary { Source = new Uri(theme == ThemeKind.Dark ? DarkUri : LightUri) });
            dictionaries.Add(new ResourceDictionary { Source = new Uri(SharedUri) });
            application.Resources["DiffInsertFill"] = Palette.InsertFill;
            application.Resources["DiffInsertStroke"] = Palette.InsertStroke;
            application.Resources["DiffDeleteFill"] = Palette.DeleteFill;
            application.Resources["DiffDeleteStroke"] = Palette.DeleteStroke;
            application.Resources["DiffModifyFill"] = Palette.ModifyFill;
            application.Resources["DiffModifyStroke"] = Palette.ModifyStroke;
            application.Resources["DiffConflictFill"] = Palette.ConflictFill;
            application.Resources["DiffConflictStroke"] = Palette.ConflictStroke;
            application.Resources["DiffMoveFill"] = Palette.MoveFill;
            application.Resources["DiffMoveStroke"] = Palette.MoveStroke;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Looks up a themed brush by key, with a transparent fallback while resources load.</summary>
    public static System.Windows.Media.Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as System.Windows.Brush ?? System.Windows.Media.Brushes.Transparent;
}
