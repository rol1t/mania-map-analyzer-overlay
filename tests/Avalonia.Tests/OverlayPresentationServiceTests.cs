using System.Text.RegularExpressions;
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
            OverlayLayoutMode = "companella",
            OverlayPresetId = "companella"
        };

        var scripts = new OverlayPresentationService().Build(settings, overlayMode: true);

        Assert.Contains("window.__overlayHostConfig=", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.DoesNotContain("window._overlayHostConfig=", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("__createRealtimePauseCoachRuntime", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("window.__overlayPauseCoachOptions=", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"recentWindowSeconds\":20", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"minimumTimingSamples\":12", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"overlayMode\":true", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"nativeRealtimeAuthority\":true", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"nativeRealtimeAuthority\":false", scripts.FullscreenObserverScript, StringComparison.Ordinal);
    }

    [Fact]
    public void FullscreenRuntimePollsTheApplicationViewStateTransport()
    {
        var settings = new LauncherSettings
        {
            AnalyzerProviderId = "mania-map-analyser",
            OverlayLayoutMode = "companella",
            OverlayPresetId = "companella"
        };

        var scripts = new OverlayPresentationService().Build(settings, overlayMode: true);

        Assert.Contains("view-state.json", scripts.FullscreenObserverScript, StringComparison.Ordinal);
        Assert.Contains("overlay:view-state", scripts.FullscreenObserverScript, StringComparison.Ordinal);
        Assert.Contains("presentationEpoch", scripts.FullscreenObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"overlayMode\":false", scripts.FullscreenObserverScript, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeEmbedsTheCompleteDanBadgeCatalogForBothPresenters()
    {
        var settings = new LauncherSettings
        {
            AnalyzerProviderId = "mania-map-analyser",
            OverlayLayoutMode = "companella",
            OverlayPresetId = "companella"
        };

        var scripts = new OverlayPresentationService().Build(settings, overlayMode: true);

        Assert.Contains("window.__overlayDanAssets=Object.freeze(", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Equal(97, Regex.Matches(scripts.ObserverScript, @"data:image/(?:svg(?:\+|\\u002B)xml|webp);base64,").Count);
        Assert.Contains("\"reform/1.svg\":\"data:image/svg\\u002Bxml;base64,", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"reform/alpha.webp\":\"data:image/webp;base64,", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"ln/17.svg\":\"data:image/svg\\u002Bxml;base64,", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"6k/terra.svg\":\"data:image/svg\\u002Bxml;base64,", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"6k/ln-finish.svg\":\"data:image/svg\\u002Bxml;base64,", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"7k/gamma.svg\":\"data:image/svg\\u002Bxml;base64,", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("\"7k/ln-stellium.svg\":\"data:image/svg\\u002Bxml;base64,", scripts.ObserverScript, StringComparison.Ordinal);
        Assert.Contains("window.__overlayDanAssets=Object.freeze(", scripts.FullscreenObserverScript, StringComparison.Ordinal);
        Assert.Equal(97, Regex.Matches(scripts.FullscreenObserverScript, @"data:image/(?:svg(?:\+|\\u002B)xml|webp);base64,").Count);
    }

    [Fact]
    public void LegacyPauseCoachCardMigratesToCompanellaReplay()
    {
        var settings = new LauncherSettings
        {
            AnalyzerProviderId = "mania-map-analyser",
            OverlayPresetId = "pause-coach-card",
            OverlayLayoutMode = "pause-coach-card"
        };

        var scripts = new OverlayPresentationService().Build(settings, overlayMode: true);

        Assert.Contains("overlay-layout-companella-replay", scripts.SetupScript, StringComparison.Ordinal);
        Assert.Contains("overlay-pause-coach-primary", scripts.SetupScript, StringComparison.Ordinal);
        Assert.Contains("[data-overlay-preset-node],.overlay-pause-coach", scripts.SetupScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default", "companella")]
    [InlineData("horizontal", "companella")]
    [InlineData("companella-focus", "companella")]
    [InlineData("companella-neon", "companella")]
    [InlineData("pause-coach-card", "companella-replay")]
    [InlineData("pause-coach-minimal", "companella-replay")]
    [InlineData("pause-coach-signal", "companella-replay")]
    public void RemovedBuiltInPresetsMigrateToCompanella(string removedPreset, string expectedPreset)
    {
        Assert.Equal(expectedPreset, OverlayPresentationService.NormalizeLayout(removedPreset));
    }

    [Fact]
    public void BuiltInCatalogExposesTheCompanellaPresetFamily()
    {
        var catalog = new OverlayPresetCatalog();
        var builtInRoot = Path.GetFullPath(catalog.BuiltInDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var builtIns = catalog.List()
            .Where(preset => preset.SourceDirectory is not null &&
                Path.GetFullPath(preset.SourceDirectory).StartsWith(builtInRoot, StringComparison.OrdinalIgnoreCase))
            .Select(preset => preset.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["companella", "companella-glass", "companella-radar", "companella-replay"],
            builtIns);
    }

    [Theory]
    [InlineData("companella-glass", "overlay-layout-companella-glass")]
    [InlineData("companella-radar", "overlay-layout-companella-radar")]
    public void CompanellaVariantsComposeBaseTemplateAndVariantStylesheet(
        string presetId,
        string expectedLayoutClass)
    {
        var settings = new LauncherSettings
        {
            AnalyzerProviderId = "mania-map-analyser",
            OverlayLayoutMode = presetId,
            OverlayPresetId = presetId
        };

        var scripts = new OverlayPresentationService().Build(settings, overlayMode: true);

        Assert.Contains(expectedLayoutClass, scripts.SetupScript, StringComparison.Ordinal);
        Assert.Contains("overlay-summary-star-method", scripts.SetupScript, StringComparison.Ordinal);
        Assert.Contains($".{expectedLayoutClass}", scripts.SetupScript, StringComparison.Ordinal);
    }
}
