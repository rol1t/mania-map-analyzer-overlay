using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class ReplayPatternTests
{
    [Fact]
    public void ClassifierAllowsMultipleMembershipsWithWeights()
    {
        string osu = """
            [Difficulty]
            CircleSize:4
            [HitObjects]
            64,192,1000,1,0,0:0:0:0:
            64,192,1050,1,0,0:0:0:0:
            192,192,1100,1,0,0:0:0:0:
            320,192,1150,1,0,0:0:0:0:
            """;
        OsuManiaBeatmap beatmap = OsuBeatmapParser.Parse(osu, "h");
        IReadOnlyList<ReplayPatternMembership> memberships = ReplayPatternClassifier.Classify(beatmap);
        Assert.Equal(4, memberships.Count);
        ReplayPatternMembership jack = memberships[1];
        Assert.True(jack.Weights.Values.Sum() <= 1.0001);
        Assert.True(jack.HasPattern(ReplayPatternKind.Minijack) || jack.HasPattern(ReplayPatternKind.Jack));
    }

    [Fact]
    public void CorrelationLinksEvidenceWithThresholds()
    {
        List<JudgedHitEvent> judged = [];
        List<ReplayPatternMembership> memberships = [];
        for (int index = 0; index < 60; index++)
        {
            bool isMinijack = index % 3 == 0;
            string id = $"obj-{index}";
            judged.Add(new JudgedHitEvent(id, 1000 + index * 100, 0, ReplayHitPhase.Note, isMinijack && index < 30 ? ReplayJudgement.Miss : ReplayJudgement.Great, 1.0, index, 1000 + index * 100, 0));
            memberships.Add(new ReplayPatternMembership(id, new Dictionary<ReplayPatternKind, double>
            {
                [isMinijack ? ReplayPatternKind.Minijack : ReplayPatternKind.Single] = 1.0
            }));
        }

        IReadOnlyList<ReplayCorrelationEvidence> evidences = ReplayPatternCorrelation.Correlate(judged, memberships, minimumEligibleNotes: 20);
        ReplayCorrelationEvidence minijack = Assert.Single(evidences, item => item.Pattern == ReplayPatternKind.Minijack);
        Assert.Equal(20, minijack.EligibleNotes);
        Assert.True(minijack.MissShare > 0.5);
        Assert.Equal(10, minijack.MissCount);

        IReadOnlyList<ReplayInsight> insights = ReplayPatternCorrelation.ToInsights(evidences);
        Assert.Contains(insights, item => item.Code.Contains("minijack"));
        Assert.All(insights, item => Assert.Contains("eligibleNotes", item.Evidence.Keys));
    }

    [Fact]
    public void CorrelationRequiresSampleThresholdAndExternalDifficultyIsOptional()
    {
        List<JudgedHitEvent> judged = [new JudgedHitEvent("obj-1", 1000, 0, ReplayHitPhase.Note, ReplayJudgement.Miss, 1.0, 0), new JudgedHitEvent("obj-2", 1100, 0, ReplayHitPhase.Note, ReplayJudgement.Great, 1.0, 1, 1100, 0)];
        List<ReplayPatternMembership> memberships = [
            new ReplayPatternMembership("obj-1", new Dictionary<ReplayPatternKind, double> { [ReplayPatternKind.Single] = 1.0 }),
            new ReplayPatternMembership("obj-2", new Dictionary<ReplayPatternKind, double> { [ReplayPatternKind.Single] = 1.0 })
        ];

        OsuManiaBeatmap beatmap = OsuBeatmapParser.Parse("[Difficulty]\nCircleSize:4\n[HitObjects]\n64,192,1000,1,0,0:0:0:0:\n", "h");
        IReadOnlyDictionary<string, double> difficulty = ReplayPatternCorrelation.WithExternalDifficulty(beatmap, _ => 5.5);
        Assert.Single(difficulty);

        IReadOnlyList<ReplayCorrelationEvidence> evidences = ReplayPatternCorrelation.Correlate(judged, memberships, externalDifficulty: difficulty, minimumEligibleNotes: 20);
        Assert.Empty(evidences);
    }

    [Fact]
    public void OptInComparisonRequiresConsent()
    {
        Assert.Throws<ReplayUnsupportedException>(() => ReplayStoredComparison.CreateOptIn("user1", [], hasConsent: false));
        ReplayStoredComparison comparison = ReplayStoredComparison.CreateOptIn("user1", [], hasConsent: true);
        Assert.Equal("user1", comparison.UserId);
    }
}
