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
        Assert.Contains("__createRealtimePauseCoachRuntime", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"overlayMode\":true", scripts.ObserverScript, StringComparison.Ordinal);
    }

    [Fact]
    public void FullscreenRuntimePollsTheApplicationViewStateTransport()
    {
        var settings = new LauncherSettings
        {
            AnalyzerProviderId = "mania-map-analyser",
            OverlayLayoutMode = "default",
            OverlayPresetId = "default"
        };

        var scripts = new OverlayPresentationService().Build(settings, overlayMode: true);

        Assert.Contains("view-state.json", scripts.FullscreenObserverScript, StringComparison.Ordinal);
        Assert.Contains("overlay:view-state", scripts.FullscreenObserverScript, StringComparison.Ordinal);
        Assert.Contains("presentationEpoch", scripts.FullscreenObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"overlayMode\":false", scripts.FullscreenObserverScript, StringComparison.Ordinal);
    }

    [Fact]
    public void PauseCoachCardPresentationIsAvailable()
    {
        var settings = new LauncherSettings
        {
            AnalyzerProviderId = "mania-map-analyser",
            OverlayPresetId = "pause-coach-card",
            OverlayLayoutMode = "pause-coach-card"
        };

        var scripts = new OverlayPresentationService().Build(settings, overlayMode: true);

        Assert.Contains("overlay-pause-coach-card", scripts.SetupScript, StringComparison.Ordinal);
        Assert.Contains("overlay-pause-coach-primary", scripts.SetupScript, StringComparison.Ordinal);
        Assert.Contains("[data-overlay-preset-node],.overlay-pause-coach", scripts.SetupScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pause-coach-minimal")]
    [InlineData("pause-coach-signal")]
    public void RemovedPauseCoachVariantsMigrateToCard(string removedPreset)
    {
        Assert.Equal("pause-coach-card", OverlayPresentationService.NormalizeLayout(removedPreset));
    }
}
