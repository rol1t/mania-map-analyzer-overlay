namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record ReplayRollingWindow
{
    public ReplayRollingWindow(
        int startIndex,
        int endIndex,
        int startTimeMs,
        int endTimeMs,
        int sampleCount,
        ReplayTimingStats? timing)
    {
        StartIndex = startIndex;
        EndIndex = endIndex;
        StartTimeMs = startTimeMs;
        EndTimeMs = endTimeMs;
        SampleCount = sampleCount;
        Timing = timing;
    }

    public int StartIndex
    {
        get;
    }
    public int EndIndex
    {
        get;
    }
    public int StartTimeMs
    {
        get;
    }
    public int EndTimeMs
    {
        get;
    }
    public int SampleCount
    {
        get;
    }
    public ReplayTimingStats? Timing
    {
        get;
    }

    /// <summary>
    /// Rolling 50-note windows (sliding, step=1). Each window is a filtered
    /// JudgedHitEvent slice; caller can map sampleCount to confidence.
    /// </summary>
    public static IReadOnlyList<ReplayRollingWindow> ByNoteCount(
        IReadOnlyList<JudgedHitEvent> judgedHits,
        int windowSize = 50)
    {
        ArgumentNullException.ThrowIfNull(judgedHits);
        if (windowSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        }

        List<ReplayRollingWindow> windows = [];

        for (int start = 0; start + windowSize <= judgedHits.Count; start++)
        {
            JudgedHitEvent[] slice = judgedHits.Skip(start).Take(windowSize).ToArray();
            ReplayTimingStats? timing = ReplayTimingStats.Calculate(slice);
            windows.Add(new ReplayRollingWindow(
                startIndex: start,
                endIndex: start + windowSize - 1,
                startTimeMs: slice.First().ExpectedMapTimeMs,
                endTimeMs: slice.Last().ExpectedMapTimeMs,
                sampleCount: timing?.SampleCount ?? 0,
                timing: timing));
        }

        return windows;
    }

    /// <summary>
    /// Rolling 10-second windows (fixed time, sliding by note). Each window
    /// contains notes whose ExpectedMapTimeMs falls within [windowStart, windowStart+duration).
    /// </summary>
    public static IReadOnlyList<ReplayRollingWindow> ByDuration(
        IReadOnlyList<JudgedHitEvent> judgedHits,
        int windowDurationMs = 10000)
    {
        ArgumentNullException.ThrowIfNull(judgedHits);
        if (windowDurationMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowDurationMs));
        }

        if (judgedHits.Count == 0)
        {
            return [];
        }

        List<ReplayRollingWindow> windows = [];
        int startTime = judgedHits.First().ExpectedMapTimeMs;
        int endTime = judgedHits.Last().ExpectedMapTimeMs;

        for (int windowStart = startTime; windowStart <= endTime; windowStart += windowDurationMs)
        {
            int windowEnd = windowStart + windowDurationMs;
            JudgedHitEvent[] slice = judgedHits
                .Where(item => item.ExpectedMapTimeMs >= windowStart && item.ExpectedMapTimeMs < windowEnd)
                .ToArray();

            int firstIndex = slice.Length > 0 ? judgedHits.IndexOf(slice.First()) : -1;
            int lastIndex = slice.Length > 0 ? judgedHits.IndexOf(slice.Last()) : -1;
            ReplayTimingStats? timing = ReplayTimingStats.Calculate(slice);

            windows.Add(new ReplayRollingWindow(
                startIndex: firstIndex,
                endIndex: lastIndex,
                startTimeMs: windowStart,
                endTimeMs: windowEnd,
                sampleCount: timing?.SampleCount ?? 0,
                timing: timing));
        }

        return windows;
    }
}

internal static class ListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> source, T item)
    {
        for (int index = 0; index < source.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(source[index], item))
            {
                return index;
            }
        }

        return -1;
    }
}
