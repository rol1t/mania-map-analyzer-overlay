using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Lazer replay ingestion stub. Full decoding requires version-pinned fixtures
/// and a ScoreV2-aware judge matrix. Until fidelity is proven, this source
/// is marked Unsupported with a typed diagnostic rather than guessing.
/// </summary>
public static class LazerReplayDecoder
{
    public static IReadOnlyList<ReplayInputEvent> Decode(byte[] lazerReplayBytes, string? sourcePrecision = null)
    {
        ArgumentNullException.ThrowIfNull(lazerReplayBytes);
        throw new ReplayUnsupportedException(
            "Lazer replay decoding is not yet enabled. Provide version-pinned fixtures and enable ScoreV2 policy.");
    }

    public static ReplayDiagnostic CreateUnsupportedDiagnostic(string? clientVersion = null, string? mods = null)
    {
        return new ReplayDiagnostic(
            ReplayDiagnosticSeverity.Error,
            "replay.lazer_not_supported",
            $"Lazer replay ingestion requires fixtures for client '{clientVersion ?? "unknown"}' mods '{mods ?? "none"}'.",
            properties: new Dictionary<string, string>
            {
                ["clientVersion"] = clientVersion ?? string.Empty,
                ["mods"] = mods ?? string.Empty
            }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }
}
