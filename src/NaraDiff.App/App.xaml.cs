using System.Windows;
using System.Windows.Threading;
using NaraDiff.App.Services;
using NaraDiff.App.Views;
using NaraDiff.Infrastructure.Logging;
using NaraDiff.Infrastructure.Persistence;

namespace NaraDiff.App;

public partial class App : Application
{
    private readonly FileLogger _logger = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) _logger.Error("domain", exception);
        };
        var store = new JsonSettingsStore();
        var settings = await store.LoadAsync();
        ThemeService.Apply(settings.Theme, settings.ColorBlindPalette);
        var window = new MainWindow(settings, store, _logger);
        MainWindow = window;
        window.Show();
        await window.HandleCommandLineAsync(e.Args);
    }

    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Error("dispatcher", e.Exception);
        MessageBox.Show(MainWindow!, $"An unexpected error occurred and was written to the log:\n\n{e.Exception.Message}",
            "NaraDiff", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
