using System;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class HeadlessPollingLifecycleTests
{
    [Fact]
    public async Task RestartWaitsForInFlightLoopAndNeverOverlaps()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var lifecycle = new HeadlessPollingLifecycle(async cancellationToken =>
        {
            var current = Interlocked.Increment(ref active);
            InterlockedMax(ref maximum, current);
            entered.TrySetResult(null);
            try
            {
                // Remain in-flight until release is set, ignoring cancellation while waiting.
                // Observe cancellation only after release so Stop/Restart deterministically waits.
                await release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        await lifecycle.StartAsync();
        await entered.Task;
        var restart = lifecycle.RestartAsync();
        Assert.False(restart.IsCompleted);
        release.TrySetResult(null);
        await restart;
        Assert.Equal(1, maximum);
        await lifecycle.StopAsync();
    }

    [Fact]
    public async Task RepeatedStartDoesNotCreateAnotherLoop()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lifecycle = new HeadlessPollingLifecycle(async token =>
        {
            entered.TrySetResult(null);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        await lifecycle.StartAsync();
        await lifecycle.StartAsync();
        await entered.Task;
        await lifecycle.StopAsync();
        Assert.False(lifecycle.IsRunning);
    }

    private static void InterlockedMax(ref int location, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (value <= current || Interlocked.CompareExchange(ref location, value, current) == current)
            {
                return;
            }
        }
    }
}
