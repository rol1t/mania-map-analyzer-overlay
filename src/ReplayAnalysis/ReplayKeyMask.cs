namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Stable .osr key-mask helpers. Each bit maps to a mania column (bit 0 → column 0).
/// Transitions are derived by xor of consecutive masks, preserving source order
/// for same-timestamp press/release edges.
/// </summary>
public static class ReplayKeyMask
{
    public static IReadOnlyList<ReplayInputEvent> DecodeTransitions(
        IReadOnlyList<(int mapTimeMs, int keyMask)> frames,
        string? sourcePrecision = null)
    {
        ArgumentNullException.ThrowIfNull(frames);

        List<ReplayInputEvent> events = new(capacity: frames.Count * 2);
        int previousMask = 0;
        long sequence = 0;

        for (int index = 0; index < frames.Count; index++)
        {
            (int mapTimeMs, int keyMask) = frames[index];
            int changed = previousMask ^ keyMask;

            if (changed == 0)
            {
                previousMask = keyMask;
                continue;
            }

            // Preserve source order: process bits low→high as stable encoding order.
            // Press edges first would reorder same-frame press/release for jacks.
            // Instead, emit in global bit order while keeping press/release identity,
            // and rely on the sourceSequence to carry the original order. For same
            // timestamp, callers must not re-sort; tests validate that rule.
            for (int bit = 0; bit < 32; bit++)
            {
                int bitMask = 1 << bit;
                if ((changed & bitMask) == 0)
                {
                    continue;
                }

                bool isPress = (keyMask & bitMask) != 0;
                events.Add(
                    new ReplayInputEvent(
                        mapTimeMs: mapTimeMs,
                        column: bit,
                        kind: isPress ? ReplayInputKind.Press : ReplayInputKind.Release,
                        sourceSequence: sequence++,
                        keyMask: keyMask,
                        sourcePrecision: sourcePrecision));
            }

            previousMask = keyMask;
        }

        return events;
    }

    public static int EncodeMask(params int[] columns)
    {
        int mask = 0;
        foreach (int column in columns)
        {
            if (column < 0 || column >= 32)
            {
                throw new ArgumentOutOfRangeException(nameof(columns), "Column must be within [0, 32).");
            }

            mask |= 1 << column;
        }

        return mask;
    }
}
