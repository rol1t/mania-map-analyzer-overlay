using System;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public static class OverlaySizingModel
{
    public static int ScalePercentFromClientWidth(double clientWidth, double baseWidth)
    {
        if (clientWidth <= 0 || baseWidth <= 0)
        {
            return 100;
        }

        return Math.Clamp((int)Math.Round(clientWidth / baseWidth * 100d), 50, 180);
    }

    public static int PhysicalWidth(double baseWidth, int scalePercent, double renderScaling)
    {
        var scale = Math.Clamp(scalePercent, 50, 180) / 100d;
        return (int)Math.Round(baseWidth * scale * renderScaling);
    }
}
