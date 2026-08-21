using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class StableOsrReplayReaderTests
{
    [Fact]
    public void EmptyArtifactIsReportedAsCorrupt()
    {
        ReplayCorruptException exception = Assert.Throws<ReplayCorruptException>(
            () => StableOsrReplayReader.Read(ReadOnlyMemory<byte>.Empty));

        Assert.Equal("replay.corrupt", exception.Code);
    }

    [Fact]
    public void TruncatedHeaderIsReportedAsCorrupt()
    {
        ReplayCorruptException exception = Assert.Throws<ReplayCorruptException>(
            () => StableOsrReplayReader.Read(new byte[] { 3, 0, 0 }));

        Assert.Equal("replay.corrupt", exception.Code);
    }

    [Fact]
    public void InvalidOsuStringMarkerIsReportedAsCorrupt()
    {
        byte[] bytes = new byte[9];
        bytes[0] = 3;

        ReplayCorruptException exception = Assert.Throws<ReplayCorruptException>(
            () => StableOsrReplayReader.Read(bytes));

        Assert.Equal("replay.corrupt", exception.Code);
    }
}
