namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public static class ReplayInputOrdering
{
    /// <summary>
    /// Stable ordering contract: sort by MapTimeMs, then by SourceSequence.
    /// Must not independently sort equal-time press/release edges.
    /// </summary>
    public static IReadOnlyList<ReplayInputEvent> Order(IReadOnlyList<ReplayInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events
            .OrderBy(item => item.MapTimeMs)
            .ThenBy(item => item.SourceSequence)
            .ToArray();
    }

    public static void ValidateSequenceMonotonic(IReadOnlyList<ReplayInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        for (int index = 1; index < events.Count; index++)
        {
            if (events[index].SourceSequence <= events[index - 1].SourceSequence)
            {
                throw new InvalidOperationException(
                    $"SourceSequence must be strictly increasing, but index {index} has {events[index].SourceSequence} after {events[index - 1].SourceSequence}.");
            }
        }
    }
}
