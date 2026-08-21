using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public static class ReplayModPolicy
{
    // Documented supported set (no enforcement): NM, HD, HR, DT, HT, NC, EZ, FL.
    // Only explicitly unsupported mods force Unsupported fidelity; unknown mods are passed
    // through so a future fixture can add coverage without a code change.

    private static readonly HashSet<string> UnsupportedMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "RD", "MR", "AP", "SO"
    };

    /// <summary>
    /// Explicit compatibility: unsupported mods produce a typed diagnostic and force
    /// Unsupported fidelity rather than guessing. Rate-changing mods (DT/HT/NC/DC)
    /// require both map and audio time to be preserved.
    /// </summary>
    public static ReplayDiagnostic? ValidateMods(IEnumerable<string>? mods, string? clientVersion)
    {
        if (mods is null)
        {
            return null;
        }

        List<string> modList = mods.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim().ToUpperInvariant()).ToList();
        string unsupported = modList.FirstOrDefault(item => UnsupportedMods.Contains(item)) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(unsupported))
        {
            return new ReplayDiagnostic(
                ReplayDiagnosticSeverity.Error,
                "replay.mod_unsupported",
                $"Mod '{unsupported}' is not supported for replay analysis (client {clientVersion ?? "unknown"}).",
                properties: new Dictionary<string, string>
                {
                    ["mod"] = unsupported,
                    ["clientVersion"] = clientVersion ?? string.Empty
                }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
        }

        return null;
    }

    public static bool RequiresRateNormalization(IEnumerable<string>? mods)
    {
        if (mods is null)
        {
            return false;
        }

        return mods.Any(item => string.Equals(item, "DT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item, "HT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item, "NC", StringComparison.OrdinalIgnoreCase));
    }
}
