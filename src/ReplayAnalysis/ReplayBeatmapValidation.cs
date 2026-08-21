using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public static class ReplayBeatmapValidation
{
    public static void ValidateBeatmapMatch(string expectedBeatmapHash, string actualBeatmapHash)
    {
        if (string.IsNullOrWhiteSpace(expectedBeatmapHash))
        {
            throw new ArgumentException("An expected beatmap hash is required.", nameof(expectedBeatmapHash));
        }

        if (string.IsNullOrWhiteSpace(actualBeatmapHash))
        {
            throw new ArgumentException("An actual beatmap hash is required.", nameof(actualBeatmapHash));
        }

        string expected = expectedBeatmapHash.Trim();
        string actual = actualBeatmapHash.Trim();

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplayBeatmapMismatchException(
                $"Replay beatmap mismatch: expected '{expected}' but replay references '{actual}'.")
            {
                ExpectedBeatmapHash = expected,
                ActualBeatmapHash = actual
            };
        }
    }

    public static ReplayDiagnostic CreateMismatchDiagnostic(string expectedBeatmapHash, string actualBeatmapHash)
    {
        return new ReplayDiagnostic(
            ReplayDiagnosticSeverity.Error,
            "replay.beatmap_mismatch",
            $"Replay beatmap hash '{actualBeatmapHash}' does not match map '{expectedBeatmapHash}'.",
            properties: new Dictionary<string, string>
            {
                ["expectedBeatmapHash"] = expectedBeatmapHash,
                ["actualBeatmapHash"] = actualBeatmapHash
            }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }
}
