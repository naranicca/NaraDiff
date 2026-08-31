using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NaraDiff.Core.Settings;

namespace NaraDiff.App.Services;

/// <summary>
/// Asks Windows to draw the title bar in the dark or light variant so the frame matches the theme.
/// The call is ignored on builds that do not support it.
/// </summary>
public static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static void ApplyTheme(Window window, ThemeKind theme)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = theme == ThemeKind.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}