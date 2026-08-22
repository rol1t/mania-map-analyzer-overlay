using System;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Detects beatmaps that are explicitly not osu!mania so the headless polling
/// loop can skip them without invoking the analyzer engine.
/// </summary>
public static class HeadlessBeatmapMode
{
    public static bool IsExplicitlyNonMania(TosuBeatmapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rawMode = TryGetRawGeneralMode(snapshot.RawBeatmap);
        if (rawMode is not null)
        {
            var trimmed = rawMode.Trim();
            if (trimmed.Length != 0)
            {
                if (IsManiaValue(trimmed))
                {
                    return false;
                }

                if (IsExplicitNonManiaValue(trimmed))
                {
                    return true;
                }

                // Unknown explicit value – let the engine decide.
                return false;
            }
        }

        var metadataMode = snapshot.Metadata?.Mode;
        if (string.IsNullOrWhiteSpace(metadataMode))
        {
            return false;
        }

        var metaTrimmed = metadataMode.Trim();
        if (IsManiaValue(metaTrimmed))
        {
            return false;
        }

        if (IsExplicitNonManiaValue(metaTrimmed))
        {
            return true;
        }

        return false;
    }

    private static string? TryGetRawGeneralMode(string rawBeatmap)
    {
        if (string.IsNullOrEmpty(rawBeatmap))
        {
            return null;
        }

        var lines = rawBeatmap.Split(['\r', '\n']);
        var inGeneral = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
            {
                inGeneral = string.Equals(line, "[General]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inGeneral)
            {
                continue;
            }

            // Look for Mode: value inside [General].
            if (line.Length < 5)
            {
                continue;
            }

            if (!line.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line.Substring(5).Trim();
            return value;
        }

        return null;
    }

    private static bool IsManiaValue(string value)
    {
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "mania", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(trimmed, "osu!mania", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Numeric 3 is the canonical mania mode.
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric == 3;
        }

        return false;
    }

    private static bool IsExplicitNonManiaValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        // Numeric modes 0,1,2 are explicitly non-mania.
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric is 0 or 1 or 2;
        }

        var lower = trimmed.ToLowerInvariant();
        return lower switch
        {
            "osu" => true,
            "standard" => true,
            "taiko" => true,
            "fruits" => true,
            "catch" => true,
            "ctb" => true,
            "osu!catch" => true,
            _ => false
        };
    }
}
