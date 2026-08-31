using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class OverlayVisibilityPolicyTests
{
    [Theory]
    [InlineData("always", true, false, true)]
    [InlineData("always", true, true, true)]
    [InlineData("outside-play", true, false, false)]
    [InlineData("outside-play", true, true, true)]
    [InlineData("outside-play", false, false, true)]
    [InlineData("during-play", true, false, true)]
    [InlineData("during-play", true, true, false)]
    [InlineData("during-play", false, false, false)]
    [InlineData("paused-only", true, true, true)]
    [InlineData("paused-only", true, false, false)]
    [InlineData("paused-only", false, true, false)]
    [InlineData("outside-only", false, false, true)]
    [InlineData("outside-only", true, false, false)]
    [InlineData("outside-and-during-play", false, false, true)]
    [InlineData("outside-and-during-play", true, false, true)]
    [InlineData("outside-and-during-play", true, true, false)]
    [InlineData("during-and-paused", false, false, false)]
    [InlineData("during-and-paused", true, false, true)]
    [InlineData("during-and-paused", true, true, true)]
    [InlineData("never", false, false, false)]
    public void EvaluatesConfiguredVisibility(
        string policy,
        bool isPlaying,
        bool isPaused,
        bool expected)
    {
        Assert.Equal(expected, OverlayVisibilityPolicy.ShouldShow(policy, isPlaying, isPaused));
    }

    [Fact]
    public void UnknownPolicyFallsBackToAlways()
    {
        Assert.Equal(OverlayVisibilityPolicy.Always, OverlayVisibilityPolicy.Normalize("not-a-policy"));
        Assert.True(OverlayVisibilityPolicy.ShouldShow("not-a-policy", true, false));
    }

    [Theory]
    [InlineData("outside-play", false, false)]
    [InlineData("during-play", true, false)]
    [InlineData("never", false, false)]
    public void KeepsOverlayVisibleWhenOsuIsMinimized(string policy, bool isPlaying, bool isPaused)
    {
        Assert.True(OverlayVisibilityPolicy.ShouldShow(policy, isPlaying, isPaused, osuMinimized: true));
    }

    [Theory]
    [InlineData("always", true)]
    [InlineData("outside-play", true)]
    [InlineData("during-play", true)]
    [InlineData("paused-only", true)]
    [InlineData("never", false)]
    public void KeepsOverlayVisibleUntilGameplayStateIsKnown(string policy, bool expected)
    {
        Assert.Equal(expected, OverlayVisibilityPolicy.ShouldShowBeforeGameplayStateIsKnown(policy));
    }

    [Theory]
    [InlineData(true, true, true, "always")]
    [InlineData(true, true, false, "outside-and-during-play")]
    [InlineData(true, false, true, "outside-play")]
    [InlineData(true, false, false, "outside-only")]
    [InlineData(false, true, true, "during-and-paused")]
    [InlineData(false, true, false, "during-play")]
    [InlineData(false, false, true, "paused-only")]
    [InlineData(false, false, false, "never")]
    public void ComposesEveryIndependentVisibilityCombination(
        bool outsidePlay,
        bool duringPlay,
        bool paused,
        string expected)
    {
        string policy = OverlayVisibilityPolicy.FromVisibleStates(outsidePlay, duringPlay, paused);

        Assert.Equal(expected, policy);
        Assert.Equal(outsidePlay, OverlayVisibilityPolicy.ShouldShow(policy, false, false));
        Assert.Equal(duringPlay, OverlayVisibilityPolicy.ShouldShow(policy, true, false));
        Assert.Equal(paused, OverlayVisibilityPolicy.ShouldShow(policy, true, true));
    }
}
