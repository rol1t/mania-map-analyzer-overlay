using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record ReplayCorrelationEvidence
{
    public ReplayCorrelationEvidence(
        ReplayPatternKind pattern,
        int eligibleNotes,
        int missCount,
        double missShare,
        double patternMissRate,
        double overallMissRate,
        double? patternUr,
        double? overallUr)
    {
        Pattern = pattern;
        EligibleNotes = eligibleNotes;
        MissCount = missCount;
        MissShare = missShare;
        PatternMissRate = patternMissRate;
        OverallMissRate = overallMissRate;
        PatternUr = patternUr;
        OverallUr = overallUr;
    }

    public ReplayPatternKind Pattern
    {
        get;
    }
    public int EligibleNotes
    {
        get;
    }
    public int MissCount
    {
        get;
    }
    public double MissShare
    {
        get;
    }
    public double PatternMissRate
    {
        get;
    }
    public double OverallMissRate
    {
        get;
    }
    public double? PatternUr
    {
        get;
    }
    public double? OverallUr
    {
        get;
    }
}

public static class ReplayPatternCorrelation
{
    public const int MinimumEligibleNotes = 20;
    public const double MinimumMissShareForClaim = 0.4;

    /// <summary>
    /// Evidence-first correlation: for each pattern, what share of all misses
    /// comes from that pattern, and what is the miss rate within the pattern.
    /// No claim is made when eligibleNotes &lt; MinimumEligibleNotes.
    /// </summary>
    public static IReadOnlyList<ReplayCorrelationEvidence> Correlate(
        IReadOnlyList<JudgedHitEvent> judgedHits,
        IReadOnlyList<ReplayPatternMembership> memberships,
        IReadOnlyDictionary<string, double>? externalDifficulty = null,
        int minimumEligibleNotes = MinimumEligibleNotes)
    {
        ArgumentNullException.ThrowIfNull(judgedHits);
        ArgumentNullException.ThrowIfNull(memberships);

        Dictionary<string, ReplayPatternMembership> byId = memberships.ToDictionary(item => item.BeatmapObjectId, item => item, StringComparer.Ordinal);
        int totalMisses = judgedHits.Count(item => item.IsMiss);
        double overallMissRate = judgedHits.Count == 0 ? 0 : (double)totalMisses / judgedHits.Count;
        double? overallUr = ReplayTimingStats.Calculate(judgedHits)?.UnstableRate;

        List<ReplayCorrelationEvidence> evidences = [];

        foreach (ReplayPatternKind pattern in Enum.GetValues<ReplayPatternKind>())
        {
            JudgedHitEvent[] eligible = judgedHits
                .Where(hit => byId.TryGetValue(hit.BeatmapObjectId, out ReplayPatternMembership? membership) && membership.HasPattern(pattern))
                .ToArray();

            if (eligible.Length < minimumEligibleNotes)
            {
                continue;
            }

            int patternMisses = eligible.Count(item => item.IsMiss);
            double missShare = totalMisses == 0 ? 0 : (double)patternMisses / totalMisses;
            double patternMissRate = eligible.Length == 0 ? 0 : (double)patternMisses / eligible.Length;
            double? patternUr = ReplayTimingStats.Calculate(eligible)?.UnstableRate;

            // externalDifficulty (e.g. ManiaMapAnalyser star/NPS) is available for
            // enrichment but does not gate the claim; thresholds are on sample size.
            _ = externalDifficulty;

            evidences.Add(new ReplayCorrelationEvidence(
                pattern: pattern,
                eligibleNotes: eligible.Length,
                missCount: patternMisses,
                missShare: missShare,
                patternMissRate: patternMissRate,
                overallMissRate: overallMissRate,
                patternUr: patternUr,
                overallUr: overallUr));
        }

        return evidences;
    }

    public static IReadOnlyDictionary<string, double> WithExternalDifficulty(
        OsuManiaBeatmap beatmap,
        Func<OsuManiaHitObject, double> difficultySelector)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(difficultySelector);
        return beatmap.HitObjects.ToDictionary(item => item.Id, difficultySelector, StringComparer.Ordinal);
    }

    public static IReadOnlyList<ReplayInsight> ToInsights(IReadOnlyList<ReplayCorrelationEvidence> evidences)
    {
        List<ReplayInsight> insights = [];
        foreach (ReplayCorrelationEvidence evidence in evidences)
        {
            if (evidence.MissShare < MinimumMissShareForClaim)
            {
                continue;
            }

            double confidence = Math.Clamp((evidence.MissShare - 0.3) * 1.5, 0, 1);
            if (confidence < ReplayInsights.MinimumConfidence)
            {
                continue;
            }

            string message = $"{evidence.Pattern.ToString().ToLowerInvariant()}s contain {evidence.MissShare:P0} of misses ({evidence.MissCount} of {evidence.EligibleNotes} eligible notes, miss rate {evidence.PatternMissRate:P0} vs {evidence.OverallMissRate:P0}).";

            insights.Add(new ReplayInsight(
                code: $"replay.pattern.{evidence.Pattern.ToString().ToLowerInvariant()}_miss_share",
                message: message,
                confidence: confidence,
                sampleCount: evidence.EligibleNotes,
                evidence: new Dictionary<string, string>
                {
                    ["pattern"] = evidence.Pattern.ToString(),
                    ["eligibleNotes"] = evidence.EligibleNotes.ToString(),
                    ["missCount"] = evidence.MissCount.ToString(),
                    ["missShare"] = evidence.MissShare.ToString("F2"),
                    ["patternMissRate"] = evidence.PatternMissRate.ToString("F2"),
                    ["patternUr"] = evidence.PatternUr?.ToString("F1") ?? "n/a",
                    ["overallUr"] = evidence.OverallUr?.ToString("F1") ?? "n/a"
                }));
        }

        return insights;
    }
}

public sealed record ReplayStoredComparison
{
    public ReplayStoredComparison(string userId, IReadOnlyList<ReplayAnalysisSnapshot> snapshots)
    {
        UserId = userId;
        Snapshots = snapshots;
    }

    public string UserId
    {
        get;
    }
    public IReadOnlyList<ReplayAnalysisSnapshot> Snapshots
    {
        get;
    }

    /// <summary>
    /// Opt-in only: comparison across a user's own stored analyses. No cloud upload;
    /// caller must have explicit consent and local storage. This stub validates the gate.
    /// </summary>
    public static ReplayStoredComparison CreateOptIn(string? userId, IReadOnlyList<ReplayAnalysisSnapshot> snapshots, bool hasConsent)
    {
        if (!hasConsent)
        {
            throw new ReplayUnsupportedException("Stored comparison requires explicit user consent for local replay storage.");
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A user id is required for opt-in comparison.", nameof(userId));
        }

        return new ReplayStoredComparison(userId.Trim(), snapshots);
    }
}
