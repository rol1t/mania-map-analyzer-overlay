namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Deterministic fixed-duration sections covering the beatmap's expected time.
/// Each section's evidence is the filtered JudgedHitEvent slice; no global
/// inference is performed.
/// </summary>
public sealed record ReplaySection
{
    public ReplaySection(
        int sectionIndex,
        int startTimeMs,
        int endTimeMs,
        int noteCount,
        int missCount,
        double accuracy,
        double? biasMs,
        double? unstableRate,
        IReadOnlyList<JudgedHitEvent> hits)
    {
        SectionIndex = sectionIndex;
        StartTimeMs = startTimeMs;
        EndTimeMs = endTimeMs;
        NoteCount = noteCount;
        MissCount = missCount;
        Accuracy = accuracy;
        BiasMs = biasMs;
        UnstableRate = unstableRate;
        Hits = hits;
    }

    public int SectionIndex
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
    public int NoteCount
    {
        get;
    }
    public int MissCount
    {
        get;
    }
    public double Accuracy
    {
        get;
    }
    public double? BiasMs
    {
        get;
    }
    public double? UnstableRate
    {
        get;
    }
    public IReadOnlyList<JudgedHitEvent> Hits
    {
        get;
    }

    public static IReadOnlyList<ReplaySection> Build(
        IReadOnlyList<JudgedHitEvent> judgedHits,
        int sectionDurationMs = 10000,
        ReplayScorePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(judgedHits);
        if (sectionDurationMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sectionDurationMs));
        }

        policy ??= ReplayScorePolicy.StableClassic;

        if (judgedHits.Count == 0)
        {
            return [];
        }

        int startTime = judgedHits.Min(item => item.ExpectedMapTimeMs);
        int endTime = judgedHits.Max(item => item.ExpectedMapTimeMs);
        int sectionCount = (int)Math.Ceiling((endTime - startTime + 1) / (double)sectionDurationMs);

        List<ReplaySection> sections = new(capacity: sectionCount);

        for (int sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            int sectionStart = startTime + sectionIndex * sectionDurationMs;
            int sectionEnd = sectionStart + sectionDurationMs;
            JudgedHitEvent[] slice = judgedHits
                .Where(item => item.ExpectedMapTimeMs >= sectionStart && item.ExpectedMapTimeMs < sectionEnd)
                .ToArray();

            ReplayTimingStats? timing = ReplayTimingStats.Calculate(slice);
            ReplayScoreSummary summary = ReplayScoreCalculator.Summarize(slice, policy);

            sections.Add(new ReplaySection(
                sectionIndex: sectionIndex,
                startTimeMs: sectionStart,
                endTimeMs: sectionEnd,
                noteCount: slice.Length,
                missCount: summary.Miss,
                accuracy: summary.Accuracy,
                biasMs: timing?.MeanMs,
                unstableRate: timing?.UnstableRate,
                hits: slice));
        }

        return sections;
    }
}
