using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.RealtimeAnalysis;

/// <summary>
/// Deterministic aggregate-only builder for <see cref="PauseCoachSnapshot"/>.
/// Consumes an ordered list of <see cref="LiveTelemetryFrame"/> and, if the
/// latest playing -&gt; paused transition is present, returns a provisional snapshot
/// derived solely from the latest frame's <c>HitErrorArray</c> and cumulative
/// judgement counts. No per-column, per-object, finger or LN claims are ever
/// emitted. Polling frames are never double-counted: offsets are taken only
/// from the latest frame.
/// </summary>
public static class PauseCoachAnalyzer
{
    /// <summary>
    /// Minimum sample count required before emitting a timing-margin insight.
    /// Keeps coaching conservative; smaller buffers remain "unknown".
    /// </summary>
    public const int MinimumSampleForInsight = 10;

    /// <summary>
    /// Minimum sample count required before computing aggregate timing stats.
    /// Below this, stats remain unavailable but no insight is emitted.
    /// The analyzer still populates SampleCount but leaves mean/median/SD null.
    /// For the pure domain slice we allow stats at any sampleCount &gt;=1 and
    /// only gate insights/margins; leave null handling to the stats block.
    /// </summary>
    public const int MinimumSampleForStats = 1;

    private const string ProvisionalFidelity = "provisional";
    private const string ProvisionalReason = "Live aggregate only; per-column, per-object, finger and LN claims suppressed. Provisional timing from latest HitErrorArray and cumulative counts.";

    /// <summary>
    /// Builds a provisional <see cref="PauseCoachSnapshot"/> for the latest
    /// playing -&gt; paused transition, or <c>null</c> when no such transition
    /// or no usable frames exist. Deterministic and side-effect free.
    /// </summary>
    public static PauseCoachSnapshot? Analyze(IReadOnlyList<LiveTelemetryFrame> frames)
    {
        if (frames is null || frames.Count == 0)
        {
            return null;
        }

        int transitionIndex = -1;
        for (int index = 1; index < frames.Count; index++)
        {
            LiveTelemetryFrame previous = frames[index - 1];
            LiveTelemetryFrame current = frames[index];

            if (current.IsPaused && previous.IsPlaying && !previous.IsPaused)
            {
                transitionIndex = index;
            }
        }

        if (transitionIndex < 0)
        {
            return null;
        }

        // Target is the latest paused frame at or after the transition, so
        // polling duplicates while paused pick the freshest HitErrorArray.
        int targetIndex = transitionIndex;
        for (int index = frames.Count - 1; index >= transitionIndex; index--)
        {
            if (frames[index].IsPaused)
            {
                targetIndex = index;
                break;
            }
        }

        LiveTelemetryFrame latest = frames[targetIndex];
        LiveTelemetryFrame performanceBaseline = frames[transitionIndex - 1];

        // Diagnostics collection – missing offsets/counts preserved as Information.
        List<string> diagnostics = [];

        double[] offsets = latest.HitErrorArray.IsDefaultOrEmpty
            ? Array.Empty<double>()
            : latest.HitErrorArray.ToArray();

        if (offsets.Length == 0)
        {
            diagnostics.Add("pausecoach.timing.no_offsets: HitErrorArray unavailable; timing stats suppressed.");
        }

        // Aggregate timing stats from latest offsets only (no double-counting).
        PauseCoachTimingSnapshot timing = BuildTiming(offsets, diagnostics);

        // Cumulative whole/recent performance via DeltaFrom.
        // Recent is the play-segment delta from the last playing frame immediately
        // before the selected transition, not from the last paused polling frame.
        PauseCoachPerformanceSnapshot performance = BuildPerformance(latest, performanceBaseline, diagnostics);

        // Section stays null/empty – we do not invent NPS/strain/pattern.
        PauseCoachSectionSnapshot section = new();

        // Conservative insights only when sample threshold met.
        IReadOnlyList<PauseCoachInsightSnapshot> insights = BuildInsights(timing, diagnostics);

        // Provisional snapshot – aggregate only.
        PauseCoachSnapshot snapshot = new()
        {
            IsProvisional = true,
            Fidelity = ProvisionalFidelity,
            Reason = ProvisionalReason,
            MapProgressMs = latest.MapTimeMs,
            Score = latest.Score,
            Accuracy = latest.Accuracy,
            Health = latest.Health,
            Combo = latest.Combo,
            MaxCombo = latest.MaxCombo,
            Failed = latest.Failed,
            Mods = latest.Mods.IsDefaultOrEmpty ? Array.Empty<string>() : latest.Mods.ToArray(),
            Timing = timing,
            Performance = performance,
            Section = section,
            Insights = insights,
            Diagnostics = diagnostics.ToArray(),
        };

        return snapshot;
    }

    private static PauseCoachTimingSnapshot BuildTiming(double[] offsets, List<string> diagnostics)
    {
        int sampleCount = offsets.Length;

        if (sampleCount == 0)
        {
            return new PauseCoachTimingSnapshot
            {
                SampleCount = 0,
                MeanMs = null,
                MedianMs = null,
                UnstableRate = null,
                EarlyLateRatio = null,
                DriftMs = null,
                PreviousBaselineMs = null,
                TimingMargin = "unknown",
                RecentOffsets = Array.Empty<double>(),
            };
        }

        double mean = offsets.Average();
        double median = MedianOf(offsets);
        double variance = offsets.Select(value => (value - mean) * (value - mean)).Average();
        double sd = Math.Sqrt(variance);
        double ur = sd * 10.0;

        int early = offsets.Count(value => value < 0);
        int late = offsets.Count(value => value > 0);
        double? ratio = early > 0 && late > 0 ? (double)early / late : null;

        // Timing margin is conservative and only meaningful with enough samples.
        string margin;
        if (sampleCount < MinimumSampleForInsight)
        {
            margin = "unknown";
        }
        else if (Math.Abs(mean) <= 5.0)
        {
            margin = "centered";
        }
        else if (mean < 0)
        {
            margin = "early";
        }
        else
        {
            margin = "late";
        }

        double[] recent = offsets.Length <= 20 ? offsets.ToArray() : offsets[^20..];

        return new PauseCoachTimingSnapshot
        {
            SampleCount = sampleCount,
            MeanMs = mean,
            MedianMs = median,
            UnstableRate = ur,
            EarlyLateRatio = ratio,
            DriftMs = mean,
            PreviousBaselineMs = null,
            TimingMargin = margin,
            RecentOffsets = recent,
        };
    }

    private static PauseCoachPerformanceSnapshot BuildPerformance(LiveTelemetryFrame latest, LiveTelemetryFrame? previousFrame, List<string> diagnostics)
    {
        LiveJudgementCounts whole = latest.Judgements;
        LiveJudgementCounts delta = previousFrame is null ? whole : whole.DeltaFrom(previousFrame.Judgements);

        if (whole.Total == 0)
        {
            diagnostics.Add("pausecoach.performance.no_counts: Cumulative judgement counts empty; totals provisional.");
        }

        // Recent counts are delta-derived; handle reset safely via DeltaFrom.
        int? recentHits = delta.HitTotal;
        int? recentMisses = delta.CountMiss;

        // If previous frame null and whole zero, recent stays 0 – reflects no new events.
        // No inventing of per-object accuracy beyond what telemetry provides.
        return new PauseCoachPerformanceSnapshot
        {
            WholeHits = whole.HitTotal,
            WholeMisses = whole.CountMiss,
            WholeAccuracy = latest.Accuracy,
            RecentHits = recentHits,
            RecentMisses = recentMisses,
            RecentAccuracy = null,
        };
    }

    private static IReadOnlyList<PauseCoachInsightSnapshot> BuildInsights(PauseCoachTimingSnapshot timing, List<string> diagnostics)
    {
        if (timing.SampleCount < MinimumSampleForInsight)
        {
            // Leave insights empty when sample is small; margin already "unknown".
            return Array.Empty<PauseCoachInsightSnapshot>();
        }

        List<PauseCoachInsightSnapshot> insights = [];

        if (timing.MeanMs.HasValue && Math.Abs(timing.MeanMs.Value) > 8.0)
        {
            string direction = timing.MeanMs.Value < 0 ? "early" : "late";
            insights.Add(new PauseCoachInsightSnapshot
            {
                Code = $"pausecoach.timing.drift_{direction}",
                Message = $"Aggregate bias {timing.MeanMs.Value:F1}ms {direction} (n={timing.SampleCount}, provisional aggregate).",
                Confidence = 0.6,
            });
        }

        if (timing.UnstableRate.HasValue && timing.UnstableRate.Value > 45.0)
        {
            insights.Add(new PauseCoachInsightSnapshot
            {
                Code = "pausecoach.timing.unstable",
                Message = $"UR {timing.UnstableRate.Value:F1} suggests unstable timing (n={timing.SampleCount}, provisional).",
                Confidence = 0.55,
            });
        }

        // Early/late imbalance insight only when ratio is extreme; explicitly suppressed when prior insight exists.
        if (timing.EarlyLateRatio.HasValue && double.IsFinite(timing.EarlyLateRatio.Value))
        {
            if (timing.EarlyLateRatio.Value > 2.0 || timing.EarlyLateRatio.Value < 0.5)
            {
                if (insights.Count == 0 && timing.SampleCount >= MinimumSampleForInsight)
                {
                    insights.Add(new PauseCoachInsightSnapshot
                    {
                        Code = "pausecoach.timing.imbalance",
                        Message = $"Early/late ratio {timing.EarlyLateRatio.Value:F2} (n={timing.SampleCount}, provisional).",
                        Confidence = 0.5,
                    });
                }
                else if (insights.Count != 0)
                {
                    // Explicit suppression: conservative behavior keeps imbalance muted when drift/unstable already emitted.
                }
            }
        }

        return insights;
    }

    private static double MedianOf(double[] values)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int count = sorted.Length;
        if (count % 2 == 1)
        {
            return sorted[count / 2];
        }

        return (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
    }
}
