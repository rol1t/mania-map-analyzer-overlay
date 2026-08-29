using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class ReplayAnalysisSessionTests
{
    [Fact]
    public void ImportRequiresExplicitStableOsrFile()
    {
        var session = new ReplayAnalysisSession();

        Assert.Throws<ReplayCorruptException>(() => session.Import(ReadOnlyMemory<byte>.Empty, "play.osr"));
        Assert.Throws<ReplayUnsupportedException>(() => session.Import(new byte[] { 1 }, "play.txt"));
        Assert.False(session.HasSelectedReplay);
    }

    [Fact]
    public async Task AnalysisWithoutSelectedReplayReturnsVisibleDiagnostic()
    {
        var session = new ReplayAnalysisSession();
        var beatmap = new TosuBeatmapSnapshot(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        AnalysisResult result = await session.AnalyzeAsync(beatmap);

        Assert.Equal(AnalysisOutcome.Failed, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "replay.not_found");
    }

    [Fact]
    public void ReplayRequestCapturesIdentityAndArtifactAtCreationTime()
    {
        var session = new ReplayAnalysisSession();
        var beatmap = new TosuBeatmapSnapshot(
            new BeatmapIdentity("map", "hash"),
            "[HitObjects]\n",
            new TosuBeatmapMetadata { Title = "Test" },
            rate: 1,
            mods: [],
            capturedAt: DateTimeOffset.UtcNow);

        session.Import(new byte[] { 1 }, "first.osr");
        ReplayAnalysisRequest first = session.CreateRequest(beatmap, beatmapGeneration: 7);
        session.Import(new byte[] { 2 }, "second.osr");
        ReplayAnalysisRequest second = session.CreateRequest(beatmap, beatmapGeneration: 8);

        Assert.True(first.RequestId.IsValid);
        Assert.True(second.RequestId.IsValid);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal(7, first.BeatmapGeneration);
        Assert.Equal("map", first.BeatmapId);
        Assert.Equal("hash", first.BeatmapHash);
        Assert.NotEqual(first.ArtifactId, second.ArtifactId);
    }
}
