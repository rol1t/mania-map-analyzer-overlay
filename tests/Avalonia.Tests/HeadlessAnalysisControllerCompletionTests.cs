using System;
using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class HeadlessAnalysisControllerCompletionTests
{
    [Fact]
    public async Task PollWithoutPublishedSnapshotDoesNotCommitDeduplicationKey()
    {
        string catalogRoot = Path.Combine(
            Path.GetTempPath(),
            "mania-map-analyzer-headless-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(catalogRoot);

        var controller = CreateController(catalogRoot, new RecordingPresenter());

        try
        {
            // With no initialized supervisor the poll cannot produce or
            // publish a result. Such an interrupted/incomplete run must remain
            // retryable instead of poisoning the same-map deduplication key.
            await InvokePollOnceAsync(controller);

            Assert.Null(ReadPrivateField(controller, "_lastAnalysisKey"));
            Assert.Null(ReadPrivateField(controller, "_lastSceneKey"));
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DeduplicationKeyIsCommittedOnlyAfterPresenterAcknowledges()
    {
        string catalogRoot = Path.Combine(
            Path.GetTempPath(),
            "mania-map-analyzer-headless-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(catalogRoot);

        var presenter = new BlockingPresenter();
        var controller = CreateController(catalogRoot, presenter);
        TosuBeatmapSnapshot beatmap = CreateSnapshot();
        HeadlessAnalysisKey key = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(
            beatmap,
            EffectiveAnalysisConfigurationStore.CreateDefault());
        var analysis = new AnalysisSnapshot
        {
            SourceId = "headless",
            Beatmap = new BeatmapSnapshot { Id = beatmap.Identity.Id }
        };

        try
        {
            Task publish = controller.PushSnapshotAsync(analysis, key);
            await presenter.Entered;

            Assert.Null(ReadPrivateField(controller, "_lastAnalysisKey"));
            Assert.Null(ReadPrivateField(controller, "_lastSceneKey"));

            presenter.Release();
            await publish;

            Assert.Equal(key, ReadPrivateField(controller, "_lastAnalysisKey"));
            Assert.Equal(key.SceneKey, ReadPrivateField(controller, "_lastSceneKey"));
        }
        finally
        {
            presenter.Release();
            await controller.DisposeAsync();
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    private static HeadlessAnalysisController CreateController(
        string catalogRoot,
        IAnalysisSnapshotPresenter presenter)
    {
        return new HeadlessAnalysisController(
            new HeadlessEngineServices(
                new AnalyzerEngineCatalog(catalogRoot),
                new AnalyzerEnginePackageDeployer(),
                static () => throw new InvalidOperationException("The test must not start an analyzer host.")),
            new HttpClient(),
            new StaticBeatmapSource(CreateSnapshot()),
            presenter,
            new EffectiveAnalysisConfigurationStore(),
            TimeSpan.FromMilliseconds(10));
    }

    private static async Task InvokePollOnceAsync(HeadlessAnalysisController controller)
    {
        MethodInfo method = typeof(HeadlessAnalysisController).GetMethod(
            "PollOnceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PollOnceAsync was not found.");
        var task = method.Invoke(controller, [CancellationToken.None]) as Task
            ?? throw new InvalidOperationException("PollOnceAsync did not return a Task.");
        await task;
    }

    private static object? ReadPrivateField(HeadlessAnalysisController controller, string name)
    {
        FieldInfo field = typeof(HeadlessAnalysisController).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{name}' was not found.");
        return field.GetValue(controller);
    }

    private static TosuBeatmapSnapshot CreateSnapshot()
    {
        return new TosuBeatmapSnapshot(
            new BeatmapIdentity("101", "hash-a", "7"),
            "osu file format v14\n[General]\nMode:3",
            new TosuBeatmapMetadata
            {
                Artist = "Artist",
                Title = "Title",
                Version = "Version",
                Mapper = "Mapper",
                Mode = "3"
            },
            1d,
            ImmutableArray<string>.Empty,
            DateTimeOffset.UtcNow);
    }

    private sealed class StaticBeatmapSource(TosuBeatmapSnapshot snapshot) : ITosuBeatmapSource
    {
        public Task<TosuBeatmapSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RecordingPresenter : IAnalysisSnapshotPresenter
    {
        public Task PresentAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("An incomplete poll must not publish a snapshot.");
        }
    }

    private sealed class BlockingPresenter : IAnalysisSnapshotPresenter
    {
        private readonly TaskCompletionSource<object?> _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task PresentAsync(
            AnalysisSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult(null);
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult(null);
    }
}
