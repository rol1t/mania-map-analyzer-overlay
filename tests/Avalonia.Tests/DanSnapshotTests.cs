using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class DanSnapshotTests
{
    [Fact]
    public void DefaultConfigurationRequestsNormalizedDanMetrics()
    {
        EffectiveAnalysisConfiguration configuration = EffectiveAnalysisConfigurationStore.CreateDefault();
        EffectiveWidgetSpec widget = Assert.Single(configuration.Widgets);

        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "difficulty.label");
        EffectiveWidgetBinding timeline = Assert.Single(
            widget.Bindings.Where(binding => binding.TargetMetricId == "difficulty.timeline"));
        Assert.True(timeline.AllowsNull);
        EffectiveWidgetBinding riceTimeline = Assert.Single(
            widget.Bindings.Where(binding => binding.TargetMetricId == "difficulty.rice.timeline"));
        Assert.True(riceTimeline.AllowsNull);
        EffectiveWidgetBinding lnTimeline = Assert.Single(
            widget.Bindings.Where(binding => binding.TargetMetricId == "difficulty.ln.timeline"));
        Assert.True(lnTimeline.AllowsNull);
        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "dan.rc.label");
        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "dan.rc.numeric");
        EffectiveWidgetBinding lnPercent = Assert.Single(
            widget.Bindings.Where(binding => binding.TargetMetricId == "difficulty.lnPercent"));
        Assert.Contains(lnPercent.Candidates, candidate => candidate.MetricId == "difficulty.lnPercent");
        Assert.Contains(lnPercent.Candidates, candidate => candidate.MetricId == "pattern.lnPercent");
        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "dan.ln.label");
        foreach (string metricId in new[]
        {
            "skills.overall",
            "skills.stream",
            "skills.jumpstream",
            "skills.handstream",
            "skills.stamina",
            "skills.jackspeed",
            "skills.chordjack",
            "skills.technical"
        })
        {
            Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == metricId);
        }
    }

    [Fact]
    public void LegacyDefaultConfigurationIsMigratedWithoutChangingCustomMappings()
    {
        EffectiveAnalysisConfiguration legacy = new()
        {
            Widgets =
            [
                new EffectiveWidgetSpec(
                    "headless-overlay",
                    [new EffectiveAnalysisSource("headless-primary", "mania-map-analyser-headless", "Mixed", "1")],
                    [new EffectiveWidgetBinding(
                        "difficulty.star",
                        [new SourceMetricCandidate("headless-primary", "difficulty.star")])])
            ]
        };

        EffectiveAnalysisConfiguration migrated = legacy.Normalize();
        EffectiveWidgetSpec widget = Assert.Single(migrated.Widgets);
        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "dan.rc.label");
        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "dan.rc.numeric");
        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "difficulty.lnPercent");
        EffectiveWidgetBinding timeline = Assert.Single(
            widget.Bindings.Where(binding => binding.TargetMetricId == "difficulty.timeline"));
        Assert.True(timeline.AllowsNull);
        EffectiveWidgetBinding riceTimeline = Assert.Single(
            widget.Bindings.Where(binding => binding.TargetMetricId == "difficulty.rice.timeline"));
        Assert.True(riceTimeline.AllowsNull);
        EffectiveWidgetBinding lnTimeline = Assert.Single(
            widget.Bindings.Where(binding => binding.TargetMetricId == "difficulty.ln.timeline"));
        Assert.True(lnTimeline.AllowsNull);
        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "dan.ln.label");

        EffectiveAnalysisConfiguration previouslyMigrated = legacy with
        {
            Widgets =
            [
                new EffectiveWidgetSpec(
                    "headless-overlay",
                    legacy.Widgets[0].Sources,
                    [
                        legacy.Widgets[0].Bindings[0],
                        new EffectiveWidgetBinding(
                            "difficulty.label",
                            [new SourceMetricCandidate("headless-primary", "difficulty.label")]),
                        new EffectiveWidgetBinding(
                            "dan.rc.label",
                            [new SourceMetricCandidate("headless-primary", "dan.rc.label")]),
                        new EffectiveWidgetBinding(
                            "dan.rc.numeric",
                            [new SourceMetricCandidate("headless-primary", "dan.rc.numeric")])
                    ])
            ]
        };

        EffectiveWidgetSpec migratedExisting = previouslyMigrated.Normalize().Widgets[0];
        Assert.Contains(migratedExisting.Bindings, binding => binding.TargetMetricId == "difficulty.lnPercent");
        Assert.Contains(migratedExisting.Bindings, binding => binding.TargetMetricId == "dan.ln.label");
        Assert.Contains(migratedExisting.Bindings, binding => binding.TargetMetricId == "skills.stream");

        EffectiveAnalysisConfiguration custom = legacy with
        {
            Widgets =
            [
                new EffectiveWidgetSpec(
                    "custom-widget",
                    legacy.Widgets[0].Sources,
                    legacy.Widgets[0].Bindings)
            ]
        };

        Assert.Single(custom.Normalize().Widgets[0].Bindings);

        EffectiveAnalysisConfiguration customHeadlessId = legacy with
        {
            Widgets =
            [
                new EffectiveWidgetSpec(
                    "headless-overlay",
                    legacy.Widgets[0].Sources,
                    [new EffectiveWidgetBinding(
                        "difficulty.star",
                        [new SourceMetricCandidate("headless-primary", "custom.star")])])
            ]
        };

        Assert.Single(customHeadlessId.Normalize().Widgets[0].Bindings);
    }

    [Fact]
    public void IncompleteHeadlessMetricsCanStillBuildRankFromDifficultyLabel()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric("difficulty.label", "3.77 SR || LN 5"),
                Metric("difficulty.star", 3.77),
                Metric("difficulty.lnPercent", 12.5)
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);
        RankEstimate rc = Assert.Single(snapshot.Ranks, rank => rank.SystemId == "rc-dan");
        RankEstimate ln = Assert.Single(snapshot.Ranks, rank => rank.SystemId == "ln-dan");
        Assert.Equal("3.77 SR", rc.Value);
        Assert.Equal("LN 5", ln.Value);
    }

    [Fact]
    public void DifficultyTimelineMetricIsConvertedToOrderedSnapshot()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Timeline" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);
        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [Metric(
                "difficulty.timeline",
                new
                {
                    times = new[] { 0, 1000, 2500, 4000 },
                    values = new[] { 2.1, 3.4, 2.8, 4.2 }
                })],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);

        Assert.NotNull(snapshot.Difficulty.Timeline);
        DifficultyTimelineSnapshot timeline = snapshot.Difficulty.Timeline!;
        Assert.Equal(4, timeline.Points.Count);
        Assert.Equal(0, timeline.Points[0].TimeMs);
        Assert.Equal(3.4, timeline.Points[1].Value);
        Assert.Equal(4000, timeline.DurationMs);
    }

    [Fact]
    public void RiceAndLnTimelineMetricsAreConvertedToSeparateSnapshotSeries()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Timeline" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);
        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric(
                    "difficulty.rice.timeline",
                    new
                    {
                        times = new[] { 0, 1000, 2500 },
                        values = new[] { 2.1, 3.4, 2.8 }
                    }),
                Metric(
                    "difficulty.ln.timeline",
                    new
                    {
                        times = new[] { 0, 1000, 2500 },
                        values = new[] { 1.2, 2.4, 1.8 }
                    })
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);

        Assert.NotNull(snapshot.Difficulty.RiceTimeline);
        Assert.NotNull(snapshot.Difficulty.LnTimeline);
        Assert.Same(snapshot.Difficulty.RiceTimeline, snapshot.Difficulty.Timeline);
        Assert.Equal(3, snapshot.Difficulty.RiceTimeline!.Points.Count);
        Assert.Equal(2.4, snapshot.Difficulty.LnTimeline!.Points[1].Value);
        Assert.Equal(2500, snapshot.Difficulty.LnTimeline.DurationMs);
    }

    [Fact]
    public void TosuStarRatingFillsMissingAnalyzerMetric()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test", StarRating = 4.75 },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);
        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Partial,
            [Metric("difficulty.label", "Reform 4 low")],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);

        Assert.Equal(4.75, snapshot.Difficulty.StarRating);
    }

    [Fact]
    public void AnalyzerStarRatingRemainsAuthoritativeOverTosuMetadata()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test", StarRating = 4.75 },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);
        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [Metric("difficulty.star", 5.5)],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);

        Assert.Equal(5.5, snapshot.Difficulty.StarRating);
    }

    [Fact]
    public void StarRatingCarriesActualEstimatorProvenance()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);
        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [Metric("difficulty.star", 5.5, actualAlgorithm: "Roxy")],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);

        Assert.Equal("mania-map-analyser-headless", snapshot.Difficulty.StarRatingProvider);
        Assert.Equal("Roxy", snapshot.Difficulty.StarRatingAlgorithm);
    }

    [Fact]
    public void DelayedTosuMetadataEnrichesFirstSnapshotWithoutReplacingAnalysis()
    {
        AnalysisSnapshot first = new()
        {
            Beatmap = new BeatmapSnapshot { Id = "map", Title = "Test" },
            Difficulty = new DifficultySnapshot { LnPercent = 42 },
            Skills = [new SkillMetric { Id = "skills.stream", Value = 12.5 }]
        };
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash", "set"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata
            {
                Title = "Test",
                Mapper = "Mapper",
                Bpm = 180,
                StarRating = 4.75,
                CircleSize = 7
            },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        AnalysisSnapshot enriched = HeadlessSnapshotConverter.WithLatestBeatmapMetadata(first, beatmap);

        Assert.Equal("Mapper", enriched.Beatmap.Mapper);
        Assert.Equal("180", enriched.Beatmap.BpmLabel);
        Assert.Equal(4.75, enriched.Difficulty.StarRating);
        Assert.Equal(7, enriched.Difficulty.Keys);
        Assert.Equal(42, enriched.Difficulty.LnPercent);
        Assert.Equal(12.5, Assert.Single(enriched.Skills).Value);
    }

    [Fact]
    public void SkillsUseDisplayLabelsInsteadOfMetricIds()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric("skills.overall", 15.2),
                Metric("skills.stream", 13.5),
                Metric("skills.jackspeed", 7.7)
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);

        Assert.Equal("Overall", Assert.Single(snapshot.Skills, skill => skill.Id == "skills.overall").Label);
        Assert.Equal("Stream", Assert.Single(snapshot.Skills, skill => skill.Id == "skills.stream").Label);
        Assert.Equal("JackSpeed", Assert.Single(snapshot.Skills, skill => skill.Id == "skills.jackspeed").Label);
    }

    [Fact]
    public void UsesBeatmapCircleSizeWhenAnalyzerOmitsKeyCountMetric()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test", CircleSize = 7 },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);
        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Partial,
            [Metric("difficulty.star", 5.25)],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);

        Assert.Equal(7, snapshot.Difficulty.Keys);
    }

    [Fact]
    public void LnDanIsSuppressedWhenLnPercentIsZero()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric("dan.rc.label", "Reform"),
                Metric("dan.ln.label", "LN 5"),
                Metric("difficulty.lnPercent", 0),
                Metric("difficulty.star", 4.0)
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);
        Assert.Single(snapshot.Ranks);
        Assert.DoesNotContain(snapshot.Ranks, rank => rank.SystemId == "ln-dan");
        Assert.Equal("Reform", Assert.Single(snapshot.Ranks).Value);
    }

    [Fact]
    public void LnDanIsSuppressedWhenLnPercentIsNull()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric("dan.rc.label", "Reform"),
                Metric("dan.ln.label", "LN 5"),
                Metric("difficulty.star", 4.0)
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);
        Assert.DoesNotContain(snapshot.Ranks, rank => rank.SystemId == "ln-dan");
    }

    [Fact]
    public void LnDanRemainsWhenLnPercentIsPositive()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric("dan.rc.label", "Reform"),
                Metric("dan.ln.label", "LN 6"),
                Metric("difficulty.lnPercent", 25.5)
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);
        Assert.Contains(snapshot.Ranks, rank => rank.SystemId == "ln-dan" && rank.Value == "LN 6");
        Assert.True(snapshot.Ranks.Single(rank => rank.SystemId == "rc-dan").IsPrimary);
        Assert.False(snapshot.Ranks.Single(rank => rank.SystemId == "ln-dan").IsPrimary);
    }

    [Theory]
    [InlineData(4, 44.9, "rc-dan")]
    [InlineData(4, 45.0, "ln-dan")]
    [InlineData(6, 44.9, "rc-dan")]
    [InlineData(6, 45.0, "ln-dan")]
    [InlineData(7, 37.4, "rc-dan")]
    [InlineData(7, 37.5, "ln-dan")]
    public void PrimaryDanUsesKeymodeSpecificLnIdentityLine(
        int keys,
        double lnPercent,
        string expectedPrimary)
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test", CircleSize = keys },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric("dan.rc.label", "Reform 7 mid"),
                Metric("dan.ln.label", "LN 7 mid"),
                Metric("difficulty.lnPercent", lnPercent),
                Metric("difficulty.keys", keys)
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);

        Assert.Equal(expectedPrimary, Assert.Single(snapshot.Ranks, rank => rank.IsPrimary).SystemId);
    }

    [Fact]
    public void PatternLnPercentPopulatesCanonicalDifficultyAndLnDan()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric("dan.rc.label", "Reform 4"),
                Metric("dan.ln.label", "LN 6"),
                Metric("pattern.lnPercent", 51.4),
                Metric("difficulty.star", 4.2)
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);
        Assert.Equal(51.4, snapshot.Difficulty.LnPercent);
        Assert.Contains(snapshot.Ranks, rank => rank.SystemId == "ln-dan" && rank.Value == "LN 6");
    }

    [Fact]
    public void LnDanFallbackFromDifficultyLabelIsSuppressedForZeroLn()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric("difficulty.label", "3.77 SR || LN 5"),
                Metric("difficulty.star", 3.77),
                Metric("difficulty.lnPercent", 0)
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);
        Assert.DoesNotContain(snapshot.Ranks, rank => rank.SystemId == "ln-dan");
        Assert.Single(snapshot.Ranks, rank => rank.SystemId == "rc-dan");
    }

    [Fact]
    public void DanCategoryDoesNotRenderAsStarRatingLabel()
    {
        TosuBeatmapSnapshot beatmap = new(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        var result = new ComposedWidgetSnapshot(
            "headless-overlay",
            AnalysisOutcome.Success,
            [
                Metric("difficulty.star", 3.77),
                Metric("difficulty.label", "Reform ...")
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);
        Assert.Equal(3.77, snapshot.Difficulty.StarRating);
        Assert.Equal(string.Empty, snapshot.Difficulty.StarLabel);
        Assert.Equal("Reform ...", snapshot.Ranks.Single(rank => rank.SystemId == "rc-dan").Value);
    }

    private static ResolvedSemanticMetric Metric(
        string id,
        object value,
        string requestedAlgorithm = "Mixed",
        string actualAlgorithm = "Mixed")
    {
        return new ResolvedSemanticMetric(
            id,
            new SemanticMetric(id, JsonSerializer.SerializeToElement(value)),
            new AnalysisMetricProvenance(
                "headless-primary",
                id,
                "mania-map-analyser-headless",
                "1",
                "2.0.0",
                "1",
                requestedAlgorithm,
                actualAlgorithm,
                AnalysisOutcome.Success));
    }
}
