using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class ReplaySnapshotContractTests
{
    [Fact]
    public void DeserializesFractionalLiveHitOffsets()
    {
        const string payload = """
            {
              "schemaVersion": 1,
              "sourceId": "mania-map-analyser",
              "replay": {
                "recentOffsets": [-12.75, 4.5, 0]
              }
            }
            """;

        AnalysisSnapshot? snapshot = JsonSerializer.Deserialize<AnalysisSnapshot>(
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Replay);
        Assert.Equal([-12.75, 4.5, 0], snapshot.Replay.RecentOffsets);
    }

    [Fact]
    public void ReplayTimingValuesCountAsReplayData()
    {
        const string payload = """
            {
              "schemaVersion": 1,
              "sourceId": "replay.analysis",
              "replay": {
                "ur": 42.5,
                "meanMs": -1.25,
                "sampleCount": 128
              }
            }
            """;

        AnalysisSnapshot? snapshot = JsonSerializer.Deserialize<AnalysisSnapshot>(
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Replay);
        Assert.True(snapshot.Replay.HasData);
    }
}
