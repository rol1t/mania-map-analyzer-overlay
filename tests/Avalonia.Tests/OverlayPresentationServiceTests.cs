using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class OverlayPresentationServiceTests
{
    [Fact]
    public void RuntimeScriptUsesTheConfigurationNameExpectedByHost()
    {
        var settings = new LauncherSettings
        {
            AnalyzerProviderId = "mania-map-analyser",
            OverlayLayoutMode = "default",
            OverlayPresetId = "default"
        };

        var scripts = new OverlayPresentationService().Build(settings, overlayMode: true);

        Assert.Contains("window.__overlayHostConfig=", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.DoesNotContain("window._overlayHostConfig=", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"overlayMode\":true", scripts.ObserverScript, StringComparison.Ordinal);
    }
}
