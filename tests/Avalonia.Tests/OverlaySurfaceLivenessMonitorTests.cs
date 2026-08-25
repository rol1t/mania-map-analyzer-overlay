using System;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Presentation;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class OverlaySurfaceLivenessMonitorTests
{
    [Fact]
    public void VisibleSurfaceWithoutFramesIsRecoveredAfterTimeout()
    {
        var now = DateTimeOffset.UnixEpoch;
        var recoveries = 0;
        var monitor = new OverlaySurfaceLivenessMonitor(
            TimeSpan.FromSeconds(1),
            () => recoveries++,
            () => now);

        monitor.SetExpected(true);
        now += TimeSpan.FromMilliseconds(999);
        monitor.Check();
        Assert.Equal(0, recoveries);

        now += TimeSpan.FromMilliseconds(1);
        monitor.Check();
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public void NewFramePostponesRecovery()
    {
        var now = DateTimeOffset.UnixEpoch;
        var recoveries = 0;
        var monitor = new OverlaySurfaceLivenessMonitor(
            TimeSpan.FromSeconds(1),
            () => recoveries++,
            () => now);

        monitor.SetExpected(true);
        now += TimeSpan.FromMilliseconds(900);
        monitor.ObserveFrame();
        now += TimeSpan.FromMilliseconds(900);
        monitor.Check();
        Assert.Equal(0, recoveries);

        now += TimeSpan.FromMilliseconds(100);
        monitor.Check();
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public void HiddenOrResetSurfaceIsNeverRecovered()
    {
        var now = DateTimeOffset.UnixEpoch;
        var recoveries = 0;
        var monitor = new OverlaySurfaceLivenessMonitor(
            TimeSpan.FromSeconds(1),
            () => recoveries++,
            () => now);

        monitor.SetExpected(true);
        monitor.Reset();
        now += TimeSpan.FromSeconds(5);
        monitor.Check();
        Assert.Equal(0, recoveries);

        monitor.SetExpected(false);
        now += TimeSpan.FromSeconds(5);
        monitor.Check();
        Assert.Equal(0, recoveries);
    }

    [Fact]
    public void FailedRecoveryIsRateLimitedAndCanRetry()
    {
        var now = DateTimeOffset.UnixEpoch;
        var attempts = 0;
        var failures = 0;
        var monitor = new OverlaySurfaceLivenessMonitor(
            TimeSpan.FromSeconds(1),
            () =>
            {
                attempts++;
                throw new InvalidOperationException("surface replacement failed");
            },
            () => now,
            _ => failures++);

        monitor.SetExpected(true);
        now += TimeSpan.FromSeconds(1);
        monitor.Check();
        monitor.Check();
        Assert.Equal(1, attempts);
        Assert.Equal(1, failures);

        now += TimeSpan.FromSeconds(1);
        monitor.Check();
        Assert.Equal(2, attempts);
        Assert.Equal(2, failures);
    }
}
