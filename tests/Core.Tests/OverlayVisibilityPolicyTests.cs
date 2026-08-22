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
}
