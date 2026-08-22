using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Headless analysis orchestration controller. Owns engine startup/shutdown,
/// beatmap polling, cancellation, deduplication, runner lifecycle, scene
/// generation, analysis execution, and the last pushed analysis snapshot.
/// </summary>
public sealed class HeadlessAnalysisController : IAsyncDisposable
{
    private readonly HeadlessEngineServices _engineServices;
    private readonly HttpClient _beatmapHttpClient;
    private readonly ITosuBeatmapSource _beatmapSource;
    private readonly IAnalysisSnapshotPresenter _presenter;
    private readonly EffectiveAnalysisConfigurationStore _configurationStore;
    private readonly TimeSpan _pollInterval;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private AnalyzerEngineSupervisor? _supervisor;
    private IAnalyzerScriptHost? _scriptHost;
    private WidgetAnalysisRunner? _widgetRunner;
    private WidgetAnalysisSceneRunner? _sceneRunner;
    private AnalysisRunScope? _sceneScope;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private EffectiveAnalysisConfiguration _configuration = EffectiveAnalysisConfigurationStore.CreateDefault();
    private HeadlessAnalysisKey? _lastAnalysisKey;
    private HeadlessSceneKey? _lastSceneKey;
    private DateTime _lastOsuNotRunningLogUtc = DateTime.MinValue;
    private DateTime _lastNoBeatmapLogUtc = DateTime.MinValue;
    private AnalysisSnapshot? _lastSnapshot;
    private AnalyzerEngineSupervisorState _currentState = AnalyzerEngineSupervisorState.NotStartedState;
    private int _pollInFlight;
    private bool _disposed;

    public HeadlessAnalysisController(
        HeadlessEngineServices engineServices,
        HttpClient beatmapHttpClient,
        ITosuBeatmapSource beatmapSource,
        IAnalysisSnapshotPresenter presenter,
        EffectiveAnalysisConfigurationStore configurationStore,
        TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(engineServices);
        ArgumentNullException.ThrowIfNull(engineServices.Catalog);
        ArgumentNullException.ThrowIfNull(engineServices.Deployer);
        ArgumentNullException.ThrowIfNull(engineServices.ScriptHostFactory);
        ArgumentNullException.ThrowIfNull(beatmapHttpClient);
        ArgumentNullException.ThrowIfNull(beatmapSource);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(configurationStore);

        _engineServices = engineServices;
        _beatmapHttpClient = beatmapHttpClient;
        _beatmapSource = beatmapSource;
        _presenter = presenter;
        _configurationStore = configurationStore;
        _pollInterval = pollInterval > TimeSpan.Zero
            ? pollInterval
            : throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public event EventHandler<AnalyzerEngineSupervisorState>? StateChanged;

    public event EventHandler<HeadlessAnalysisResultEventArgs>? ResultProduced;

    public event EventHandler<HeadlessBeatmapSourceStateEventArgs>? BeatmapSourceStateChanged;

    public AnalyzerEngineSupervisorState CurrentState
    {
        get
        {
            lock (_sync)
            {
                return _currentState;
            }
        }
    }

    public bool IsHeadlessActive
    {
        get
        {
            lock (_sync)
            {
                return _supervisor is not null && _currentState.IsReady;
            }
        }
    }

    public AnalysisSnapshot? LastSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _lastSnapshot;
            }
        }
    }

    public EffectiveAnalysisConfiguration CurrentConfiguration
    {
        get
        {
            lock (_sync)
            {
                return _configuration;
            }
        }
    }

    public ITosuBeatmapSource BeatmapSource => _beatmapSource;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopAsync(cancellationToken).ConfigureAwait(false);

        _configuration = _configurationStore.Load();
        AppLogger.Info(
            "Effective analysis configuration",
            $"Loaded effective configuration: engine={_configuration.DefaultEngineId} algorithm={_configuration.DefaultAlgorithm} widgets={_configuration.Widgets.Length}");

        try
        {
            var scriptHost = _engineServices.ScriptHostFactory();
            var supervisor = new AnalyzerEngineSupervisor(
                _engineServices.Catalog,
                _engineServices.Deployer,
                scriptHost);
            supervisor.StateChanged += Supervisor_StateChanged;

            lock (_sync)
            {
                _scriptHost = scriptHost;
                _supervisor = supervisor;
            }

            var preferredEngineId = string.IsNullOrWhiteSpace(_configuration.DefaultEngineId)
                ? _engineServices.Catalog.Available().FirstOrDefault()?.Id
                : _configuration.DefaultEngineId;

            await supervisor.StartAsync(preferredEngineId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeEngineAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await DisposeEngineAsync().ConfigureAwait(false);
            AppLogger.Error("Starting headless analysis controller", exception);
            var fallbackState = new AnalyzerEngineSupervisorState(
                AnalyzerEngineSupervisorStatus.Fallback,
                null,
                null,
                $"Headless engine initialization failed: {exception.Message}. Legacy DOM adapter remains the explicit fallback.",
                [],
                IsFallback: true,
                IsReady: false);
            EnterState(fallbackState);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var pollTask = StopPolling();

        if (pollTask is not null)
        {
            var timeoutExpired = false;
            var callerCancelled = false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                await pollTask.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                callerCancelled = true;
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown timeout elapsed (WaitAsync was cancelled by the timeout token)
                // or the poll loop was cancelled. Only treat as timeout if the task is still live.
                if (!pollTask.IsCompleted)
                {
                    timeoutExpired = true;
                    AppLogger.Warning("Stopping headless analysis", "Polling did not stop within the shutdown timeout; awaiting completion before releasing resources.");
                }
            }
            catch (TimeoutException)
            {
                if (!pollTask.IsCompleted)
                {
                    timeoutExpired = true;
                    AppLogger.Warning("Stopping headless analysis", "Polling did not stop within the shutdown timeout; awaiting completion before releasing resources.");
                }
            }

            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the polling loop is cancelled.
            }

            if (callerCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var remainingPollTask = StopPolling();
            if (remainingPollTask is not null)
            {
                try
                {
                    await remainingPollTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            DisposeRunners();
        }
        finally
        {
            _stateGate.Release();
        }

        await DisposeEngineAsync().ConfigureAwait(false);
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReloadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _configuration = _configurationStore.Load();
            AppLogger.Info(
                "Effective analysis mapping",
                $"Reloaded mapping: {_configuration.Widgets.Length} widget(s), engine={_configuration.DefaultEngineId}, algorithm={_configuration.DefaultAlgorithm}");

            var pollTask = StopPolling();
            if (pollTask is not null)
            {
                await pollTask.ConfigureAwait(false);
            }

            DisposeRunners();
            InitializeRunners();

            lock (_sync)
            {
                _lastAnalysisKey = null;
                _lastSceneKey = null;
            }

            AnalyzerEngineSupervisor? supervisorCopy;
            lock (_sync)
            {
                supervisorCopy = _supervisor;
            }

            if (supervisorCopy?.IsReady == true)
            {
                await TriggerPollAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public Task NotifyNavigationAsync(CancellationToken cancellationToken = default)
    {
        AnalyzerEngineSupervisor? supervisor;
        lock (_sync)
        {
            supervisor = _supervisor;
        }

        return supervisor?.NotifyNavigationAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public Task NotifyTosuRestartAsync(CancellationToken cancellationToken = default)
    {
        AnalyzerEngineSupervisor? supervisor;
        lock (_sync)
        {
            supervisor = _supervisor;
        }

        return supervisor?.NotifyTosuRestartAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public async Task PushSnapshotAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            await _presenter.PresentAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _lastSnapshot = snapshot;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Headless snapshot push", $"Could not push snapshot: {exception.Message}", exception);
        }
    }

    public void UpdateLastSnapshot(AnalysisSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            _lastSnapshot = snapshot;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _beatmapHttpClient.Dispose();
        _stateGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Supervisor_StateChanged(object? sender, AnalyzerEngineSupervisorState state)
    {
        EnterState(state);
    }

    private void EnterState(AnalyzerEngineSupervisorState state)
    {
        lock (_sync)
        {
            _currentState = state;
        }

        StateChanged?.Invoke(this, state);

        _ = ApplyStateAsync(state);
    }

    private async Task ApplyStateAsync(AnalyzerEngineSupervisorState state)
    {
        try
        {
            await _stateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (state.IsReady)
                {
                    InitializeRunners();
                    StartPolling();
                }
                else if (state.IsFallback)
                {
                    var pollTask = StopPolling();
                    if (pollTask is not null)
                    {
                        await pollTask.ConfigureAwait(false);
                    }

                    DisposeRunners();
                }
            }
            finally { _stateGate.Release(); }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Applying headless supervisor state", exception, userVisible: false);
        }
    }

    private void InitializeRunners()
    {
        DisposeRunners();
        AnalyzerEngineSupervisor? supervisor;
        lock (_sync)
        {
            supervisor = _supervisor;
        }

        var coordinator = supervisor?.Coordinator;
        if (coordinator is null)
        {
            AppLogger.Warning("Headless runners", "Cannot initialize widget runners: coordinator is not ready.");
            return;
        }

        try
        {
            _widgetRunner = new WidgetAnalysisRunner(coordinator);
            _sceneRunner = new WidgetAnalysisSceneRunner(coordinator);
            _sceneScope = new AnalysisRunScope("headless-scene");
            _widgetRunner.SnapshotComposed += WidgetSnapshotComposed;
            _sceneRunner.SnapshotComposed += SceneSnapshotComposed;
            AppLogger.Info("Headless runners", $"Initialized widget runners for {_configuration.Widgets.Length} widget(s).");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Initializing headless runners", exception);
        }
    }

    private void DisposeRunners()
    {
        if (_widgetRunner is not null)
        {
            _widgetRunner.SnapshotComposed -= WidgetSnapshotComposed;
            _widgetRunner.Dispose();
            _widgetRunner = null;
        }

        if (_sceneRunner is not null)
        {
            _sceneRunner.SnapshotComposed -= SceneSnapshotComposed;
            _sceneRunner.Dispose();
            _sceneRunner = null;
        }

        _sceneScope?.Dispose();
        _sceneScope = null;

        lock (_sync)
        {
            _lastAnalysisKey = null;
            _lastSceneKey = null;
        }
    }

    private void WidgetSnapshotComposed(ComposedWidgetSnapshot snapshot)
    {
        AppLogger.Debug(
            "Headless widget composition",
            $"Widget '{snapshot.WidgetId}' composed with outcome {snapshot.Outcome} and {snapshot.Metrics.Count} metrics. Diagnostics: {string.Join(", ", snapshot.Diagnostics.Select(diagnostic => diagnostic.Code))}");
    }

    private void SceneSnapshotComposed(WidgetAnalysisSceneSnapshot snapshot)
    {
        AppLogger.Debug(
            "Headless scene composition",
            $"Scene '{snapshot.SceneId}' generation {snapshot.Generation} composed with {snapshot.OrderedSnapshots.Length} widget(s).");
    }

    private void StartPolling()
    {
        StopPolling();
        lock (_sync)
        {
            var cancellation = new CancellationTokenSource();
            _pollCancellation = cancellation;
            _pollTask = Task.Run(() => PollLoopAsync(cancellation.Token), cancellation.Token);
        }
    }

    private Task? StopPolling()
    {
        lock (_sync)
        {
            var task = _pollTask;
            var cancellation = _pollCancellation;
            _pollTask = null;
            _pollCancellation = null;
            cancellation?.Cancel();
            if (task is null)
            {
                cancellation?.Dispose();
            }
            else
            {
                _ = task.ContinueWith(_ => cancellation?.Dispose(), TaskScheduler.Default);
            }

            return task;
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TriggerPollAsync(CancellationToken cancellationToken)
    {
        CancellationToken token;
        lock (_sync)
        {
            token = _pollCancellation?.Token ?? cancellationToken;
        }
        try
        {
            await PollOnceAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Expected when polling is stopped or the controller is disposed.
        }
    }

    private async Task DisposeEngineAsync()
    {
        AnalyzerEngineSupervisor? supervisor;
        IAnalyzerScriptHost? scriptHost;
        lock (_sync)
        {
            supervisor = _supervisor;
            scriptHost = _scriptHost;
            _supervisor = null;
            _scriptHost = null;
        }

        if (supervisor is not null)
        {
            supervisor.StateChanged -= Supervisor_StateChanged;
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }

        if (scriptHost is not null)
        {
            await scriptHost.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _pollInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            TosuBeatmapSnapshot snapshot;
            try
            {
                snapshot = await _beatmapSource.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (TosuBeatmapSourceException exception)
            {
                if (IsOsuNotRunningBeatmapException(exception))
                {
                    var now = DateTime.UtcNow;
                    if (now - _lastOsuNotRunningLogUtc > TimeSpan.FromSeconds(5))
                    {
                        _lastOsuNotRunningLogUtc = now;
                        AppLogger.Info("Headless beatmap poll", "osu! client is not running — headless beatmap fetch skipped (tosu HTTP 500).");
                    }

                    lock (_sync)
                    {
                        _lastAnalysisKey = null;
                    }

                    BeatmapSourceStateChanged?.Invoke(this, new HeadlessBeatmapSourceStateEventArgs(
                        HeadlessBeatmapSourceState.OsuNotRunning));
                    return;
                }

                if (IsNoBeatmapBeatmapException(exception))
                {
                    var now = DateTime.UtcNow;
                    if (now - _lastNoBeatmapLogUtc > TimeSpan.FromSeconds(5))
                    {
                        _lastNoBeatmapLogUtc = now;
                        AppLogger.Info("Headless beatmap poll", "No current beatmap is available — osu! is running but no map is selected.");
                    }

                    lock (_sync)
                    {
                        _lastAnalysisKey = null;
                    }

                    BeatmapSourceStateChanged?.Invoke(this, new HeadlessBeatmapSourceStateEventArgs(
                        HeadlessBeatmapSourceState.NoBeatmap));
                    return;
                }

                AppLogger.Warning("Headless beatmap poll", exception.Message, exception);
                BeatmapSourceStateChanged?.Invoke(this, new HeadlessBeatmapSourceStateEventArgs(
                    HeadlessBeatmapSourceState.Error,
                    exception.Message));
                return;
            }

            HeadlessAnalysisKey? lastAnalysisKey;
            HeadlessSceneKey? lastSceneKey;
            lock (_sync)
            {
                lastAnalysisKey = _lastAnalysisKey;
                lastSceneKey = _lastSceneKey;
            }

            if (HeadlessAnalysisKeyBuilder.IsSameBeatmapAndConfig(snapshot, _configuration, lastAnalysisKey, lastSceneKey))
            {
                return;
            }

            var isNewSceneGeneration = HeadlessAnalysisKeyBuilder.IsNewSceneGeneration(snapshot, _configuration, lastSceneKey);
            if (isNewSceneGeneration)
            {
                var newSceneKey = HeadlessAnalysisKeyBuilder.BuildSceneKey(snapshot, _configuration);
                AppLogger.Info("Headless scene", $"Effective scene generation invalidated: newKey={newSceneKey}");
            }

            var analysisKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, _configuration);
            var sceneKey = analysisKey.SceneKey;
            lock (_sync)
            {
                _lastAnalysisKey = analysisKey;
                _lastSceneKey = sceneKey;
            }

            AppLogger.Debug(
                "Headless beatmap poll",
                $"New beatmap {snapshot.Identity.StableKey} title={snapshot.Metadata.Title} version={snapshot.Metadata.Version} rate={snapshot.Rate} mods=[{string.Join(",", snapshot.Mods)}] effective={analysisKey.SceneKey}");

            if (_sceneRunner is not null && _widgetRunner is not null)
            {
                var sceneSpec = BuildSceneSpec(snapshot);
                if (sceneSpec is not null)
                {
                    try
                    {
                        var sceneSnapshot = await _sceneRunner.RunAsync(sceneSpec, cancellationToken).ConfigureAwait(false);
                        await PushSceneSnapshotAsync(snapshot, sceneSnapshot, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        AppLogger.Info("Headless scene", $"Scene generation for {sceneKey} was superseded by a newer map/config generation.");
                        return;
                    }
                    catch (Exception exception)
                    {
                        AppLogger.Warning("Headless scene composition", $"Scene composition failed for {sceneKey}: {exception.Message}", exception);
                    }
                }
            }

            AnalyzerEngineSupervisor? supervisor;
            lock (_sync)
            {
                supervisor = _supervisor;
            }

            if (supervisor is null)
            {
                return;
            }

            var result = await supervisor.AnalyzeAsync(
                snapshot,
                requestedAlgorithm: _configuration.DefaultAlgorithm,
                profileId: "headless-overlay",
                configurationVersion: _configuration.ConfigurationVersion,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                AppLogger.Info("Headless analysis", $"Headless analysis returned no result for {snapshot.Identity.StableKey}. DOM adapter remains the explicit fallback for this beatmap.");
                return;
            }

            LogAnalysisResult(snapshot, result);
            await PushAnalysisResultSnapshotAsync(snapshot, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when polling is stopped.
        }
        catch (Exception exception)
        {
            AppLogger.Error("Polling headless beatmap", exception, userVisible: false);
        }
        finally
        {
            Interlocked.Exchange(ref _pollInFlight, 0);
        }
    }

    private async Task PushSceneSnapshotAsync(
        TosuBeatmapSnapshot snapshot,
        WidgetAnalysisSceneSnapshot sceneSnapshot,
        CancellationToken cancellationToken)
    {
        LogSceneResult(sceneSnapshot);

        var firstWidget = sceneSnapshot.OrderedSnapshots.FirstOrDefault();
        if (firstWidget is null)
        {
            return;
        }

        var actualAlgorithm = firstWidget.Metrics.Values.FirstOrDefault()?.Provenance.ActualAlgorithm;
        var headlessSnapshot = HeadlessSnapshotConverter.FromComposed(snapshot, null, firstWidget);
        await PushSnapshotAsync(headlessSnapshot, cancellationToken).ConfigureAwait(false);

        ResultProduced?.Invoke(this, new HeadlessAnalysisResultEventArgs(
            snapshot,
            firstWidget.Outcome,
            actualAlgorithm,
            firstWidget.Diagnostics,
            headlessSnapshot,
            isSceneResult: true));
    }

    private async Task PushAnalysisResultSnapshotAsync(
        TosuBeatmapSnapshot snapshot,
        AnalysisResult result,
        CancellationToken cancellationToken)
    {
        var headlessSnapshot = HeadlessSnapshotConverter.FromAnalysisResult(snapshot, null, result);
        await PushSnapshotAsync(headlessSnapshot, cancellationToken).ConfigureAwait(false);

        ResultProduced?.Invoke(this, new HeadlessAnalysisResultEventArgs(
            snapshot,
            result.Outcome,
            result.ActualAlgorithm,
            result.Diagnostics,
            headlessSnapshot,
            isSceneResult: false));
    }

    private void LogSceneResult(WidgetAnalysisSceneSnapshot sceneSnapshot)
    {
        foreach (var widgetSnapshot in sceneSnapshot.OrderedSnapshots)
        {
            var outcomeText = widgetSnapshot.Outcome switch
            {
                AnalysisOutcome.Success => "Success",
                AnalysisOutcome.Partial => "Partial",
                AnalysisOutcome.Failed => "Failed",
                AnalysisOutcome.Cancelled => "Cancelled",
                _ => widgetSnapshot.Outcome.ToString()
            };
            var metricsSummary = string.Join(", ", widgetSnapshot.Metrics.Take(4).Select(metric => metric.Key + "=" + metric.Value.Metric.Value.ToString()));
            var diagnosticsSummary = widgetSnapshot.Diagnostics.Length == 0
                ? string.Empty
                : $" Diagnostics: {string.Join("; ", widgetSnapshot.Diagnostics.Take(2).Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message))}";
            AppLogger.Info(
                "Headless scene result",
                $"Widget '{widgetSnapshot.WidgetId}' outcome={outcomeText} metrics=[{metricsSummary}]{diagnosticsSummary}");
        }
    }

    private void LogAnalysisResult(TosuBeatmapSnapshot snapshot, AnalysisResult result)
    {
        var outcomeText = result.Outcome switch
        {
            AnalysisOutcome.Success => "Success",
            AnalysisOutcome.Partial => "Partial (inspect diagnostics)",
            AnalysisOutcome.Failed => "Failed (fallback to DOM)",
            AnalysisOutcome.Cancelled => "Cancelled",
            _ => result.Outcome.ToString()
        };
        var metricsSummary = string.Join(", ", result.Metrics.Take(4).Select(metric => metric.Key + "=" + metric.Value.Value.ToString()));
        var diagnosticsSummary = result.Diagnostics.Length == 0
            ? string.Empty
            : $" Diagnostics: {string.Join("; ", result.Diagnostics.Take(3).Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message))}";
        AppLogger.Debug(
            "Headless analysis result",
            $"Beatmap {snapshot.Identity.StableKey} outcome={outcomeText} metrics=[{metricsSummary}]{diagnosticsSummary}");
    }

    private WidgetAnalysisSceneSpec? BuildSceneSpec(TosuBeatmapSnapshot snapshot)
    {
        AnalyzerEngineSupervisor? supervisor;
        lock (_sync)
        {
            supervisor = _supervisor;
        }

        var descriptor = supervisor?.ActiveDescriptor;
        if (descriptor is null)
        {
            AppLogger.Warning("Headless composition", "Cannot build scene spec: no active analyzer descriptor.");
            return null;
        }

        var widgets = new List<WidgetAnalysisSpec>();
        foreach (var effectiveWidget in _configuration.Widgets)
        {
            var sources = new List<AnalysisSourceSpec>();
            foreach (var effectiveSource in effectiveWidget.Sources)
            {
                if (!string.Equals(effectiveSource.EngineId, descriptor.Id, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Debug(
                        "Headless composition",
                        $"Source '{effectiveSource.SourceId}' requests engine '{effectiveSource.EngineId}' but active is '{descriptor.Id}'. Skipping source.");
                    continue;
                }

                var configuration = new AnalysisConfiguration(
                    effectiveSource.RequestedAlgorithm,
                    effectiveSource.ConfigurationVersion,
                    effectiveSource.Options);
                var request = new AnalysisRequest(
                    descriptor.Id,
                    snapshot.Identity,
                    snapshot.RawBeatmap,
                    configuration,
                    effectiveWidget.WidgetId,
                    snapshot.Rate,
                    snapshot.Mods);
                sources.Add(new AnalysisSourceSpec(effectiveSource.SourceId, request, descriptor));
            }

            if (sources.Count == 0)
            {
                AppLogger.Debug("Headless composition", $"Widget '{effectiveWidget.WidgetId}' has no usable sources for active engine '{descriptor.Id}'.");
                continue;
            }

            var bindings = effectiveWidget.Bindings.Select(binding =>
                new WidgetMetricBinding(binding.TargetMetricId, binding.Candidates, binding.AllowsNull));
            try
            {
                widgets.Add(new WidgetAnalysisSpec(effectiveWidget.WidgetId, sources, bindings));
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Headless composition", $"Widget '{effectiveWidget.WidgetId}' has invalid bindings: {exception.Message}", exception);
            }
        }

        if (widgets.Count == 0)
        {
            AppLogger.Warning("Headless composition", "No widgets could be built from effective configuration.");
            return null;
        }

        try
        {
            return new WidgetAnalysisSceneSpec("headless-scene", widgets);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Headless composition", $"Could not build scene spec: {exception.Message}", exception);
            return null;
        }
    }

    private static bool IsOsuNotRunningBeatmapException(TosuBeatmapSourceException exception)
    {
        var message = exception.Message ?? string.Empty;
        if (message.Contains("500", StringComparison.Ordinal) &&
            message.Contains("osu", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var inner = exception.InnerException?.Message ?? string.Empty;
        return inner.Contains("500", StringComparison.Ordinal) &&
            inner.Contains("osu", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNoBeatmapBeatmapException(TosuBeatmapSourceException exception)
    {
        var message = exception.Message ?? string.Empty;
        if (message.Contains("without a current beatmap identity", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("without beatmap metadata", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("A beatmap id or hash is required", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var inner = exception.InnerException?.Message ?? string.Empty;
        return inner.Contains("without a current beatmap identity", StringComparison.OrdinalIgnoreCase) ||
            inner.Contains("without beatmap metadata", StringComparison.OrdinalIgnoreCase) ||
            inner.Contains("A beatmap id or hash is required", StringComparison.OrdinalIgnoreCase);
    }
}
