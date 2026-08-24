using ManiaMapAnalyzerOverlay.Avalonia.Platform;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class OsuWindowPresenceTrackerTests
{
    [Fact]
    public void ReportsClosedAfterConsecutiveMissingWindowObservations()
    {
        var tracker = new OsuWindowPresenceTracker(missingObservationThreshold: 4);

        Assert.True(tracker.Observe(processRunning: true, windowPresent: true));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: false));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: false));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: false));
        Assert.False(tracker.Observe(processRunning: true, windowPresent: false));
    }

    [Fact]
    public void WindowReappearanceCancelsPendingClosure()
    {
        var tracker = new OsuWindowPresenceTracker(missingObservationThreshold: 3);

        Assert.True(tracker.Observe(processRunning: true, windowPresent: true));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: false));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: false));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: true));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: false));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: false));
        Assert.False(tracker.Observe(processRunning: true, windowPresent: false));
    }

    [Fact]
    public void UnknownWindowObservationDoesNotAdvanceClosure()
    {
        var tracker = new OsuWindowPresenceTracker(missingObservationThreshold: 2);

        Assert.True(tracker.Observe(processRunning: true, windowPresent: true));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: false));
        Assert.Null(tracker.Observe(processRunning: true, windowPresent: null));
        Assert.False(tracker.Observe(processRunning: true, windowPresent: false));
    }

    [Fact]
    public void ProcessExitReportsClosedImmediately()
    {
        var tracker = new OsuWindowPresenceTracker();

        Assert.True(tracker.Observe(processRunning: true, windowPresent: true));
        Assert.False(tracker.Observe(processRunning: false, windowPresent: false));
        Assert.Null(tracker.Observe(processRunning: false, windowPresent: false));
    }
}
