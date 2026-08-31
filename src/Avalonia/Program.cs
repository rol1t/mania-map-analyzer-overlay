using System;
using Avalonia;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 &&
                string.Equals(args[0], "--verify-runtime-package", StringComparison.Ordinal))
            {
                var assembly = typeof(RuntimeAssetDeployment).Assembly;
                RuntimeAssetDeployment.DeployEmbeddedRuntimeAssets(assembly, args[1]);
                if (RuntimeAssetDeployment.DeployEmbeddedUpdater(
                        assembly,
                        System.IO.Path.Combine(args[1], "tools")) is null)
                {
                    throw new InvalidOperationException("The launcher does not contain its updater helper.");
                }

                return 0;
            }

            RuntimeAssetDeployment.Initialize();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            AppLogger.Error("Starting application", exception);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
