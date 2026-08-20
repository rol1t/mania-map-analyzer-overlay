using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class AnalyzerCoordinatorTests
{
    [Fact]
    public void AcceptsSnapshotFromActiveAdapter()
    {
        var adapter = new JsonAdapter("first");
        var coordinator = new AnalyzerCoordinator([adapter], adapter.Descriptor.Id);
        var expected = Snapshot("first");

        var accepted = coordinator.TryAccept("first", JsonSerializer.Serialize(expected), out var actual);

        Assert.True(accepted);
        Assert.NotNull(actual);
        Assert.Equal("first", actual.SourceId);
        Assert.Equal("42", actual.Beatmap.Id);
        Assert.Equal(5.25, actual.Difficulty.StarRating);
        Assert.Same(actual, coordinator.CurrentSnapshot);
    }

    [Fact]
    public void RejectsSnapshotFromPreviouslySelectedAdapter()
    {
        var first = new JsonAdapter("first");
        var second = new JsonAdapter("second");
        var coordinator = new AnalyzerCoordinator([first, second], first.Descriptor.Id);
        coordinator.Switch(second.Descriptor.Id);

        var accepted = coordinator.TryAccept("first", JsonSerializer.Serialize(Snapshot("first")), out _);

        Assert.False(accepted);
        Assert.Null(coordinator.CurrentSnapshot);
    }

    [Fact]
    public void RejectsUnsupportedSnapshotSchema()
    {
        var adapter = new JsonAdapter("first");
        var coordinator = new AnalyzerCoordinator([adapter], adapter.Descriptor.Id);
        var incompatible = Snapshot("first") with
        {
            SchemaVersion = 999
        };

        var accepted = coordinator.TryAccept("first", JsonSerializer.Serialize(incompatible), out _);

        Assert.False(accepted);
        Assert.Null(coordinator.CurrentSnapshot);
    }

    private static AnalysisSnapshot Snapshot(string sourceId) => new()
    {
        SourceId = sourceId,
        Beatmap = new BeatmapSnapshot { Id = "42", Title = "Test map" },
        Difficulty = new DifficultySnapshot { StarRating = 5.25, Keys = 4 },
        Ranks = [new RankEstimate { SystemId = "rc-dan", Value = "Reform 4" }],
        Skills = [new SkillMetric { Id = "stream", Label = "Stream", NormalizedValue = 75 }]
    };

    private sealed class JsonAdapter(string id) : IAnalyzerAdapter
    {
        public AnalyzerDescriptor Descriptor
        {
            get;
        } = new(
            id,
            id,
            "/analysis",
            "/analysis?fullscreen=1",
            null,
            false);

        public bool TryNormalize(string payload, out AnalysisSnapshot? snapshot)
        {
            try
            {
                snapshot = JsonSerializer.Deserialize<AnalysisSnapshot>(payload);
                return snapshot is not null;
            }
            catch (JsonException)
            {
                snapshot = null;
                return false;
            }
        }
    }
}
