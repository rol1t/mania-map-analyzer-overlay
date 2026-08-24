using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class LatestWinsSnapshotPublisherTests
{
    [Fact]
    public async Task HiddenCollectionFlushesOnlyNewestPausedSnapshotWhenVisible()
    {
        var published = new List<string>();
        var publisher = CreatePublisher(published);

        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.Submit("1s");
        publisher.Submit("5s");
        publisher.Submit("30s-paused");

        Assert.Empty(published);

        publisher.SetPresentationVisible(true);
        await EventuallyAsync(() => published.Count == 1);

        Assert.Equal(["30s-paused"], published);
    }

    [Fact]
    public async Task HiddenPlayingFrameFollowedByPauseFlushesPauseFrameFirst()
    {
        var published = new List<string>();
        var publisher = CreatePublisher(published);

        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.Submit("Playing@29000");
        publisher.Submit("Paused@30000");
        publisher.SetPresentationVisible(true);

        await EventuallyAsync(() => published.Count == 1);

        Assert.Equal(["Paused@30000"], published);
    }

    [Fact]
    public async Task HiddenPlayingFrameFollowedByResultsFlushesResultsFrameFirst()
    {
        var published = new List<string>();
        var publisher = CreatePublisher(published);

        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.Submit("Playing@29000");
        publisher.Submit("Results@30000");
        publisher.SetPresentationVisible(true);

        await EventuallyAsync(() => published.Count == 1);

        Assert.Equal(["Results@30000"], published);
    }

    [Fact]
    public async Task ShowingAfterHidePublishesNewestFrameCollectedWhileHidden()
    {
        var published = new List<string>();
        var publisher = CreatePublisher(published);

        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.SetPresentationVisible(true);
        publisher.Submit("Playing@10000");
        await EventuallyAsync(() => published.Count == 1);

        publisher.SetPresentationVisible(false);
        publisher.Submit("Playing@20000");
        publisher.Submit("Paused@30000");

        Assert.Equal(["Playing@10000"], published);

        publisher.SetPresentationVisible(true);
        await EventuallyAsync(() => published.Count == 2);

        Assert.Equal(["Playing@10000", "Paused@30000"], published);
    }

    [Fact]
    public async Task SlowPublishRetainsNewestFrameAndPublishesItAfterCurrentCall()
    {
        var published = new List<string>();
        var firstStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new LatestWinsSnapshotPublisher<string>(async snapshot =>
        {
            published.Add(snapshot);
            if (snapshot == "1s")
            {
                firstStarted.TrySetResult(null);
                await releaseFirst.Task;
            }
        });

        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.SetPresentationVisible(true);
        publisher.Submit("1s");
        await firstStarted.Task;

        publisher.Submit("5s");
        publisher.Submit("20s");
        publisher.Submit("30s-paused");
        releaseFirst.TrySetResult(null);

        await EventuallyAsync(() => published.Count == 2);
        Assert.Equal(["1s", "30s-paused"], published);
    }

    [Fact]
    public async Task InvokeScriptExceptionDoesNotPoisonLaterSnapshots()
    {
        var published = new List<string>();
        var failures = 0;
        var publisher = new LatestWinsSnapshotPublisher<string>(snapshot =>
        {
            if (snapshot == "broken")
            {
                throw new InvalidOperationException("old WebView");
            }

            published.Add(snapshot);
            return Task.CompletedTask;
        }, _ => failures++);

        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.SetPresentationVisible(true);
        publisher.Submit("broken");
        await EventuallyAsync(() => failures == 1);

        publisher.Submit("recovered");
        await EventuallyAsync(() => published.Count == 1);

        Assert.Equal(["recovered"], published);
    }

    [Fact]
    public async Task WebViewRecreationDoesNotLetOldInFlightCallBlockNewSession()
    {
        var published = new List<string>();
        var oldStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new LatestWinsSnapshotPublisher<string>(async snapshot =>
        {
            published.Add(snapshot);
            if (snapshot == "old")
            {
                oldStarted.TrySetResult(null);
                await releaseOld.Task;
            }
        });

        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.SetPresentationVisible(true);
        publisher.Submit("old");
        await oldStarted.Task;

        publisher.BeginPresentationSession();
        publisher.Submit("new");
        publisher.SetBrowserReady(true);
        publisher.SetPresentationVisible(true);
        await EventuallyAsync(() => published.Contains("new"));

        releaseOld.TrySetResult(null);
        await EventuallyAsync(() => published.Count == 2);
        Assert.Contains("new", published);
    }

    [Fact]
    public async Task LeaveAndReenterReplaysLatestSnapshotIntoNewDocument()
    {
        var published = new List<string>();
        var publisher = CreatePublisher(published);

        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.SetPresentationVisible(true);
        publisher.Submit("paused-30s");
        await EventuallyAsync(() => published.Count == 1);

        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.SetPresentationVisible(true);
        await EventuallyAsync(() => published.Count == 2);

        Assert.Equal(["paused-30s", "paused-30s"], published);
    }

    private static LatestWinsSnapshotPublisher<string> CreatePublisher(List<string> published) =>
        new(snapshot =>
        {
            published.Add(snapshot);
            return Task.CompletedTask;
        });

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Expected asynchronous publisher condition was not reached.");
    }
}
