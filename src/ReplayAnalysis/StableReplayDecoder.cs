namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Stable .osr frame decoder. The binary .osr replay data is lzma-compressed;
/// this decoder operates on already decompressed frame strings and on
/// synthetic frame lists used by tests. Real file I/O belongs to the host,
/// which must not expose raw bytes via settings/logs/WebView.
/// </summary>
public static class StableReplayDecoder
{
    public static IReadOnlyList<ReplayInputEvent> DecodeFrameString(
        string decompressedReplayData,
        string? sourcePrecision = null)
    {
        if (decompressedReplayData is null)
        {
            throw new ArgumentNullException(nameof(decompressedReplayData));
        }

        List<(int mapTimeMs, int keyMask)> frames = [];
        int currentTime = 0;

        string[] tokens = decompressedReplayData.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (string rawToken in tokens)
        {
            string token = rawToken.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            string[] parts = token.Split('|');
            if (parts.Length < 4)
            {
                continue;
            }

            if (!int.TryParse(parts[0].Trim(), out int delta))
            {
                continue;
            }

            if (!int.TryParse(parts[3].Trim(), out int keys))
            {
                continue;
            }

            currentTime += delta;

            // Skip negative time handling: keep as-is for lead-in detection.
            frames.Add((currentTime, keys));
        }

        return ReplayKeyMask.DecodeTransitions(frames, sourcePrecision ?? "stable.osr.frames");
    }

    public static IReadOnlyList<ReplayInputEvent> DecodeFrames(
        IReadOnlyList<(int mapTimeMs, int keyMask)> frames,
        string? sourcePrecision = null)
    {
        return ReplayKeyMask.DecodeTransitions(frames, sourcePrecision ?? "stable.osr.frames");
    }
}
