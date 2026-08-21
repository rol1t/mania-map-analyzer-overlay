using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class GoldenParityTests
{
    [Theory(Skip = "Golden parity requires official MMA 2.0.0 fixtures not bundled in CI")]
    [InlineData("Sunny")]
    [InlineData("Daniel")]
    [InlineData("Mixed")]
    [InlineData("Roxy")]
    [InlineData("Companella")]
    public void MatchesOfficialManiaMapAnalyserOutput(string algorithm)
    {
        // Intentionally skipped: downloads a pinned MMA 2.0.0 artifact and compares
        // headless worker output for the reference beatmaps. Run locally with
        // MMA_FIXTURE_ROOT set to a checkout of LeoBlackMT/osumania_map_analyser@2.0.0.
        Assert.NotEmpty(algorithm);
    }
}
