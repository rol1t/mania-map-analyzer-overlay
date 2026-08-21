using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Avalonia.ViewModels;
using ManiaMapAnalyzerOverlay.Avalonia.Views;

namespace ManiaMapAnalyzerOverlay.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Avalonia dispatcher", e.Exception);
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var flattened = e.Exception.Flatten();
        var isTransport = flattened.InnerExceptions.Any(exception =>
            exception.Message.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("An existing connection was forcibly closed", StringComparison.OrdinalIgnoreCase) ||
            exception is System.Net.Http.HttpRequestException ||
            exception is System.IO.IOException);

        if (isTransport)
        {
            AppLogger.Warning(
                "Unobserved background task (transport closed)",
                "A background HTTP request was aborted when tosu/osu closed the connection.",
                e.Exception);
        }
        else
        {
            AppLogger.Error("Unobserved background task", e.Exception);
        }

        e.SetObserved();
    }

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            AppLogger.Error("Unhandled application exception", exception);
        else
            AppLogger.Error("Unhandled application exception", e.ExceptionObject?.ToString() ?? "Unknown exception.");
    }
}
