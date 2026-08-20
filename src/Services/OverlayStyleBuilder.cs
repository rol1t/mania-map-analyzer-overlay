using System;
using System.Globalization;

namespace ManiaMapAnalyzerOverlay;

/// <summary>
/// Shared numeric formatting used when passing layout variables to external
/// overlay resources. Presentation CSS itself lives under assets/overlay.
/// </summary>
internal static class OverlayStyleBuilder
{
    internal static string Pixels(double value, double scale)
    {
        var pixels = Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));
        return pixels.ToString(CultureInfo.InvariantCulture) + "px";
    }
}
