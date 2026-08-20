using System.Globalization;

namespace ManiaMapAnalyzerOverlay.Avalonia.Models;

public sealed class LauncherSettings
{
    public string AnalyzerProviderId { get; set; } = "mania-map-analyser";
    public int OverlayX { get; set; } = -32000;
    public int OverlayY { get; set; } = -32000;
    public int OverlayWidth { get; set; } = 520;
    public int OverlayHeight { get; set; } = 650;
    public bool OverlayHintShown
    {
        get; set;
    }
    public int OverlayHintVersion
    {
        get; set;
    }
    public string OverlayLayoutMode { get; set; } = "default";
    public string OverlayPresetId { get; set; } = "default";
    public int OverlayScalePercent { get; set; } = 100;
    public int CompanellaLayoutVersion { get; set; } = 3;
    public bool FullscreenOverlayEnabled
    {
        get; set;
    }
    public int FullscreenOverlayStyleVersion
    {
        get; set;
    }
    public string Language
    {
        get; set;
    } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";

    public LauncherSettings Clone() => new()
    {
        AnalyzerProviderId = AnalyzerProviderId,
        OverlayX = OverlayX,
        OverlayY = OverlayY,
        OverlayWidth = OverlayWidth,
        OverlayHeight = OverlayHeight,
        OverlayHintShown = OverlayHintShown,
        OverlayHintVersion = OverlayHintVersion,
        OverlayLayoutMode = OverlayLayoutMode,
        OverlayPresetId = OverlayPresetId,
        OverlayScalePercent = OverlayScalePercent,
        CompanellaLayoutVersion = CompanellaLayoutVersion,
        FullscreenOverlayEnabled = FullscreenOverlayEnabled,
        FullscreenOverlayStyleVersion = FullscreenOverlayStyleVersion,
        Language = Language
    };
}
