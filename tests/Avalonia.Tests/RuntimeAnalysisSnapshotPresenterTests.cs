using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class RuntimeAnalysisSnapshotPresenterTests
{
    [Fact]
    public async Task ForwardsSnapshotWithoutTouchingWebView()
    {
        AnalysisSnapshot? received = null;
        var presenter = new RuntimeAnalysisSnapshotPresenter(snapshot => received = snapshot);
        var snapshot = new AnalysisSnapshot
        {
            SourceId = "headless",
            Beatmap = new BeatmapSnapshot { Id = "674175" }
        };

        await presenter.PresentAsync(snapshot);

        Assert.Same(snapshot, received);
    }

    [Fact]
    public async Task HonorsCancellationBeforeForwarding()
    {
        bool called = false;
        var presenter = new RuntimeAnalysisSnapshotPresenter(_ => called = true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => presenter.PresentAsync(
            new AnalysisSnapshot(),
            cancellation.Token));
        Assert.False(called);
    }

    [Fact]
    public async Task WaitsForRuntimeAcknowledgement()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presenter = new RuntimeAnalysisSnapshotPresenter(async (_, _) =>
        {
            entered.TrySetResult(null);
            await release.Task;
        });

        Task presentation = presenter.PresentAsync(new AnalysisSnapshot());
        await entered.Task;

        Assert.False(presentation.IsCompleted);

        release.TrySetResult(null);
        await presentation;
    }
    [Fact]
    public async Task PreservesVersionedRequestAndConfigurationIdentity()
    {
        ManiaMapAnalyzerOverlay.Application.AnalysisRequestId? receivedRequest = null;
        string? receivedConfiguration = null;
        var presenter = new RuntimeAnalysisSnapshotPresenter(
            (snapshot, requestId, configurationIdentity, _) =>
            {
                receivedRequest = requestId;
                receivedConfiguration = configurationIdentity;
                return Task.CompletedTask;
            });
        var requestId = new ManiaMapAnalyzerOverlay.Application.AnalysisRequestId(42);

        await presenter.PresentAsync(new AnalysisSnapshot(), requestId, "config-A");

        Assert.Equal(requestId, receivedRequest);
        Assert.Equal("config-A", receivedConfiguration);
    }
}
