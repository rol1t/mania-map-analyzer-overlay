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
}
