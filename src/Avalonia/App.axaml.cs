using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ManiaMapAnalyzerOverlay.Avalonia.ViewModels;
using ManiaMapAnalyzerOverlay.Avalonia.Views;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Avalonia.Threading;
using System;
using System.IO;

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
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "avalonia-error.log"),
                DateTime.Now.ToString("O") + Environment.NewLine + e.Exception + Environment.NewLine + Environment.NewLine);
        }
        catch { }
        e.Handled = true;
    }
}
