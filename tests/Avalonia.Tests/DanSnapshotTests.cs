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
        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "dan.rc.label");
        Assert.Contains(widget.Bindings, binding => binding.TargetMetricId == "dan.rc.numeric");
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
                Metric("difficulty.star", 3.77)
            ],
            []);

        AnalysisSnapshot snapshot = HeadlessSnapshotConverter.FromComposed(beatmap, null, result);
        RankEstimate rc = Assert.Single(snapshot.Ranks, rank => rank.SystemId == "rc-dan");
        RankEstimate ln = Assert.Single(snapshot.Ranks, rank => rank.SystemId == "ln-dan");
        Assert.Equal("3.77 SR", rc.Value);
        Assert.Equal("LN 5", ln.Value);
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

    private static ResolvedSemanticMetric Metric(string id, object value)
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
                "Mixed",
                "Mixed",
                AnalysisOutcome.Success));
    }
}
