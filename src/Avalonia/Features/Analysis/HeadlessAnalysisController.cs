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
    private readonly SemaphoreSlim _pollWakeSignal = new(0, 1);

    private AnalyzerEngineSupervisor? _supervisor;
    private IAnalyzerScriptHost? _scriptHost;
    private WidgetAnalysisRunner? _widgetRunner;
    private WidgetAnalysisSceneRunner? _sceneRunner;
    private AnalysisRunScope? _sceneScope;
    private readonly HeadlessPollingLifecycle _pollingLifecycle;
    private EffectiveAnalysisConfiguration _configuration = EffectiveAnalysisConfigurationStore.CreateDefault();
    private HeadlessAnalysisKey? _lastAnalysisKey;
    private HeadlessSceneKey? _lastSceneKey;
    private DateTime _lastOsuNotRunningLogUtc = DateTime.MinValue;
    private DateTime _lastNoBeatmapLogUtc = DateTime.MinValue;
    private AnalysisSnapshot? _lastSnapshot;
    // The map id alone is not an analysis identity. A rate/modifier change
    // on the same beatmap must not allow a cached NM result to replace a DT
    // result (or vice versa) after WebView recreation.
    private HeadlessAnalysisKey? _lastPublishedAnalysisKey;
    private HeadlessAnalysisKey? _candidateAnalysisKey;
    private int _candidateAnalysisObservations;
    private string _confirmedRealtimeBeatmapId = string.Empty;
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
        _pollingLifecycle = new HeadlessPollingLifecycle(PollLoopAsync);
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

    /// <summary>
    /// Wakes the headless poll loop when the authoritative realtime boundary
    /// observes another beatmap/mod selection. The signal is coalesced and is
    /// retained when a poll is already in progress, so the newest state is
    /// checked immediately after that poll completes.
    /// </summary>
    public void RequestImmediatePoll(string beatmapId)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _confirmedRealtimeBeatmapId = beatmapId?.Trim() ?? string.Empty;
        }

        if (!_pollingLifecycle.IsRunning)
        {
            return;
        }

        try
        {
            _pollWakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake already represents the latest runtime state.
        }
        catch (ObjectDisposedException)
        {
            // Disposal can race the final UI-thread telemetry callback.
        }
    }

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
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                await _pollingLifecycle.StopAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            DisposeRunners();
            await DisposeEngineAsync().ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
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

            await RestartPollingAsync(withImmediatePoll: true, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Replays the last completed analysis snapshot into the current WebView
    /// document. Navigation replaces the JavaScript renderer, while the
    /// headless engine intentionally keeps its cached result. Without this
    /// explicit replay the first realtime adapter frame can clear the map
    /// summary (it only contains the numeric beatmap id) until the next full
    /// analysis run completes.
    /// </summary>
    public async Task RepublishLastSnapshotAsync(CancellationToken cancellationToken = default)
    {
        AnalysisSnapshot? snapshot;
        HeadlessAnalysisKey? publishedKey;
        lock (_sync)
        {
            snapshot = _lastSnapshot;
            publishedKey = _lastPublishedAnalysisKey;
        }

        if (snapshot is null)
        {
            return;
        }

        // Navigation can happen immediately after the player selects another
        // map. The cached analysis belongs to the previous map and must not be
        // replayed into the new widget document while the new headless run is
        // still being prepared. Verify the current Tosu identity before
        // replaying the cache; realtime adapter frames remain responsible for
        // the live transition.
        try
        {
            var currentBeatmap = await _beatmapSource.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (publishedKey is not null
                ? !IsSameAnalysisTarget(publishedKey, HeadlessAnalysisKeyBuilder.BuildAnalysisKey(currentBeatmap, _configuration))
                : !IsSameBeatmap(snapshot, currentBeatmap))
            {
                AppLogger.Info(
                    "Headless snapshot replay",
                    $"Skipped cached snapshot for map {snapshot.Beatmap.Id} because Tosu now reports {currentBeatmap.Identity.Id} with rate={currentBeatmap.Rate} mods=[{string.Join(',', currentBeatmap.Mods)}].");
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TosuBeatmapSourceException exception)
        {
            // A replay after an unknown source transition can put a previous
            // map back on screen. Wait for a verified live identity instead.
            AppLogger.Debug("Headless snapshot replay", $"Skipped unverified cached map: {exception.Message}");
            return;
        }

        var replayExtensions = new Dictionary<string, object?>(snapshot.Extensions, StringComparer.OrdinalIgnoreCase)
        {
            ["headlessReplay"] = true
        };
        await _presenter.PresentAsync(
            snapshot with
            {
                Extensions = replayExtensions
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSameBeatmap(AnalysisSnapshot snapshot, TosuBeatmapSnapshot current)
    {
        var cachedId = snapshot.Beatmap.Id?.Trim() ?? string.Empty;
        var currentId = current.Identity.Id?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(cachedId) && !string.IsNullOrWhiteSpace(currentId))
        {
            return string.Equals(cachedId, currentId, StringComparison.OrdinalIgnoreCase);
        }

        var cachedSet = snapshot.Beatmap.SetId?.Trim() ?? string.Empty;
        var currentSet = current.Identity.SetId?.Trim() ?? string.Empty;
        var cachedVersion = snapshot.Beatmap.Version?.Trim() ?? string.Empty;
        var currentVersion = current.Metadata.Version?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(cachedSet) && !string.IsNullOrWhiteSpace(currentSet))
        {
            return string.Equals(cachedSet, currentSet, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(cachedVersion)
                    || string.IsNullOrWhiteSpace(currentVersion)
                    || string.Equals(cachedVersion, currentVersion, StringComparison.OrdinalIgnoreCase));
        }

        return string.Equals(snapshot.Beatmap.Artist?.Trim(), current.Metadata.Artist?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(snapshot.Beatmap.Title?.Trim(), current.Metadata.Title?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(cachedVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsCurrentAnalysisAsync(
        HeadlessAnalysisKey expectedKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _beatmapSource.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var currentKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(current, _configuration);
            if (!IsSameAnalysisTarget(expectedKey, currentKey))
            {
                AppLogger.Info(
                    "Headless snapshot freshness",
                    $"Rejected stale result: expected={expectedKey.BeatmapKey.StableKey} rate={expectedKey.BeatmapKey.Rate} mods=[{expectedKey.BeatmapKey.Mods}], current={currentKey.BeatmapKey.StableKey} rate={currentKey.BeatmapKey.Rate} mods=[{currentKey.BeatmapKey.Mods}].");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TosuBeatmapSourceException exception)
        {
            AppLogger.Debug("Headless snapshot freshness", $"Could not verify analysis map before publishing: {exception.Message}");
            // The result is intentionally not published without a verified
            // identity, but the same map must be eligible for the next poll.
            // Otherwise a single transient Tosu 404 permanently suppresses
            // the already completed analysis.
            lock (_sync)
            {
                _lastAnalysisKey = null;
                _lastSceneKey = null;
                _lastPublishedAnalysisKey = null;
                _candidateAnalysisKey = null;
                _candidateAnalysisObservations = 0;
            }
            return false;
        }
    }

    private static bool IsSameAnalysisTarget(HeadlessAnalysisKey expected, HeadlessAnalysisKey current)
    {
        return expected.Equals(current);
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

    public Task PushSnapshotAsync(
        AnalysisSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        PushSnapshotCoreAsync(snapshot, null, cancellationToken);

    public Task PushSnapshotAsync(
        AnalysisSnapshot snapshot,
        HeadlessAnalysisKey analysisKey,
        CancellationToken cancellationToken = default) =>
        PushSnapshotCoreAsync(snapshot, analysisKey, cancellationToken);

    private async Task PushSnapshotCoreAsync(
        AnalysisSnapshot snapshot,
        HeadlessAnalysisKey? analysisKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            await _presenter.PresentAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _lastSnapshot = snapshot;
                _lastPublishedAnalysisKey = analysisKey;
                if (analysisKey is not null)
                {
                    // Deduplication is a completion checkpoint, not an
                    // in-flight marker. A fast A -> B -> C transition can make
                    // freshness reject B (or cancel its scene) after analysis
                    // starts. Recording the key before this successful
                    // presentation would then suppress every retry if B/C is
                    // the final selection.
                    _lastAnalysisKey = analysisKey;
                    _lastSceneKey = analysisKey.SceneKey;
                }
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
            _lastPublishedAnalysisKey = null;
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
        await _pollingLifecycle.DisposeAsync().ConfigureAwait(false);
        _beatmapHttpClient.Dispose();
        _pollWakeSignal.Dispose();
        _stateGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Supervisor_StateChanged(object? sender, AnalyzerEngineSupervisorState state)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
        }

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
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }

                if (state.IsReady)
                {
                    await RestartPollingAsync(withImmediatePoll: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                else if (state.IsFallback)
                {
                    try
                    {
                        await _pollingLifecycle.StopAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    DisposeRunners();
                }
            }
            finally { _stateGate.Release(); }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Error("Applying headless supervisor state", exception, userVisible: false);
        }
    }

    private async Task RestartPollingAsync(bool withImmediatePoll, CancellationToken cancellationToken)
    {
        try
        {
            await _pollingLifecycle.StopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        DisposeRunners();
        InitializeRunners();

        lock (_sync)
        {
            _lastAnalysisKey = null;
            _lastSceneKey = null;
            _candidateAnalysisKey = null;
            _candidateAnalysisObservations = 0;
        }

        AnalyzerEngineSupervisor? supervisorCopy;
        lock (_sync)
        {
            supervisorCopy = _supervisor;
        }

        if (supervisorCopy?.IsReady != true)
        {
            return;
        }

        if (withImmediatePoll)
        {
            await TriggerPollAsync(cancellationToken).ConfigureAwait(false);
        }

        await _pollingLifecycle.StartAsync().ConfigureAwait(false);
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
            _candidateAnalysisKey = null;
            _candidateAnalysisObservations = 0;
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

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                await _pollWakeSignal.WaitAsync(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TriggerPollAsync(CancellationToken cancellationToken)
    {
        var token = cancellationToken;
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
                        _candidateAnalysisKey = null;
                        _candidateAnalysisObservations = 0;
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
                        _candidateAnalysisKey = null;
                        _candidateAnalysisObservations = 0;
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

            if (HeadlessBeatmapMode.IsExplicitlyNonMania(snapshot))
            {
                lock (_sync)
                {
                    _lastAnalysisKey = null;
                    _lastSceneKey = null;
                    _candidateAnalysisKey = null;
                    _candidateAnalysisObservations = 0;
                }

                var modeMessage = $"Current beatmap {snapshot.Identity.StableKey} is not osu!mania.";
                AppLogger.Info("Headless beatmap poll", $"Skipping non-mania beatmap {snapshot.Identity.StableKey} title={snapshot.Metadata.Title} version={snapshot.Metadata.Version} mode={snapshot.Metadata.Mode} — analysis not started.");
                AppLogger.Debug("Headless beatmap poll", modeMessage);
                BeatmapSourceStateChanged?.Invoke(this, new HeadlessBeatmapSourceStateEventArgs(
                    HeadlessBeatmapSourceState.UnsupportedMode,
                    modeMessage));
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
                lock (_sync)
                {
                    _candidateAnalysisKey = null;
                    _candidateAnalysisObservations = 0;
                }
                await RefreshPublishedMetadataAsync(snapshot, lastAnalysisKey!, cancellationToken).ConfigureAwait(false);
                return;
            }

            var isNewSceneGeneration = HeadlessAnalysisKeyBuilder.IsNewSceneGeneration(snapshot, _configuration, lastSceneKey);
            if (isNewSceneGeneration)
            {
                var newSceneKey = HeadlessAnalysisKeyBuilder.BuildSceneKey(snapshot, _configuration);
                AppLogger.Info("Headless scene", $"Effective scene generation invalidated: newKey={newSceneKey}");
            }

            var analysisKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, _configuration);
            if (!ConfirmAnalysisCandidate(analysisKey))
            {
                AppLogger.Debug(
                    "Headless beatmap poll",
                    $"Holding transient map/config candidate {analysisKey.BeatmapKey.StableKey} rate={analysisKey.BeatmapKey.Rate} mods=[{analysisKey.BeatmapKey.Mods}] until the next consistent poll.");
                return;
            }
            var sceneKey = analysisKey.SceneKey;

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
                        await PushSceneSnapshotAsync(snapshot, sceneSnapshot, analysisKey, cancellationToken).ConfigureAwait(false);
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
            await PushAnalysisResultSnapshotAsync(snapshot, result, analysisKey, cancellationToken).ConfigureAwait(false);
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
        HeadlessAnalysisKey analysisKey,
        CancellationToken cancellationToken)
    {
        if (!await IsCurrentAnalysisAsync(analysisKey, cancellationToken).ConfigureAwait(false))
        {
            AppLogger.Info("Headless snapshot push", $"Skipped stale scene result for beatmap {snapshot.Identity.Id}; Tosu now reports another map.");
            return;
        }

        LogSceneResult(sceneSnapshot);

        // A WebView2 worker can terminate while a navigation or Tosu restart
        // is in flight. The scene runner still returns a valid failed
        // snapshot, so the polling key would otherwise mark this beatmap as
        // complete forever and no later request could recreate the runtime.
        // Let the next poll retry transient worker/bootstrap failures while
        // keeping ordinary beatmap parse failures cached as before.
        bool hasTransientEngineFailure = sceneSnapshot.OrderedSnapshots.Any(HasTransientEngineFailure);
        if (hasTransientEngineFailure)
        {
            lock (_sync)
            {
                _lastAnalysisKey = null;
                _lastSceneKey = null;
                _lastPublishedAnalysisKey = null;
                _candidateAnalysisKey = null;
                _candidateAnalysisObservations = 0;
            }

            AppLogger.Warning(
                "Headless scene",
                $"Transient analyzer failure for beatmap {snapshot.Identity.StableKey}; retrying on the next poll.");
        }

        var firstWidget = sceneSnapshot.OrderedSnapshots.FirstOrDefault();
        if (firstWidget is null)
        {
            return;
        }

        var actualAlgorithm = firstWidget.Metrics.Values.FirstOrDefault()?.Provenance.ActualAlgorithm;
        var headlessSnapshot = HeadlessSnapshotConverter.FromComposed(snapshot, null, firstWidget);
        if (hasTransientEngineFailure)
        {
            // Publish the diagnostic snapshot, but do not mark this analysis
            // identity as complete. The next poll must be able to recreate a
            // failed worker/runtime without requiring a map or modifier change.
            await PushSnapshotAsync(headlessSnapshot, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PushSnapshotAsync(headlessSnapshot, analysisKey, cancellationToken).ConfigureAwait(false);
        }

        ResultProduced?.Invoke(this, new HeadlessAnalysisResultEventArgs(
            snapshot,
            firstWidget.Outcome,
            actualAlgorithm,
            firstWidget.Diagnostics,
            headlessSnapshot,
            isSceneResult: true));
    }

    private static bool HasTransientEngineFailure(ComposedWidgetSnapshot widget)
    {
        return widget.Diagnostics.Any(AnalyzerDiagnosticClassifier.IsTransientEngineFailure);
    }

    private bool ConfirmAnalysisCandidate(HeadlessAnalysisKey candidate)
    {
        lock (_sync)
        {
            // There is no prior presentation to protect during startup. The
            // first verified map can be analyzed immediately.
            if (_lastPublishedAnalysisKey is null || _lastPublishedAnalysisKey.Equals(candidate))
            {
                _candidateAnalysisKey = null;
                _candidateAnalysisObservations = 0;
                return true;
            }

            // Realtime collection already applies its own carousel stability
            // guard. Once that authoritative boundary accepts this beatmap,
            // repeating the same debounce in headless polling only adds up to
            // one full poll interval before analysis can start.
            if (!string.IsNullOrWhiteSpace(_confirmedRealtimeBeatmapId)
                && string.Equals(
                    _confirmedRealtimeBeatmapId,
                    candidate.BeatmapKey.BeatmapId,
                    StringComparison.OrdinalIgnoreCase))
            {
                _candidateAnalysisKey = null;
                _candidateAnalysisObservations = 0;
                return true;
            }

            // The selected rate/modifier is part of the analysis identity, but
            // it is not a carousel transition. Tosu can expose the new
            // selectPlay payload for only one poll before another partial
            // packet arrives. Once the beatmap file revision is known to be
            // unchanged, start the requested recalculation immediately rather
            // than hiding it behind the two-observation map debounce.
            if (HeadlessAnalysisKeyBuilder.IsSameBeatmapRevision(_lastPublishedAnalysisKey, candidate))
            {
                _candidateAnalysisKey = null;
                _candidateAnalysisObservations = 0;
                return true;
            }

            if (_candidateAnalysisKey is null || !_candidateAnalysisKey.Equals(candidate))
            {
                _candidateAnalysisKey = candidate;
                _candidateAnalysisObservations = 1;
                return false;
            }

            _candidateAnalysisObservations++;
            if (_candidateAnalysisObservations < 2)
            {
                return false;
            }

            _candidateAnalysisKey = null;
            _candidateAnalysisObservations = 0;
            return true;
        }
    }

    private async Task RefreshPublishedMetadataAsync(
        TosuBeatmapSnapshot beatmap,
        HeadlessAnalysisKey analysisKey,
        CancellationToken cancellationToken)
    {
        AnalysisSnapshot? current;
        HeadlessAnalysisKey? publishedKey;
        lock (_sync)
        {
            current = _lastSnapshot;
            publishedKey = _lastPublishedAnalysisKey;
        }

        if (current is null || publishedKey is null || !publishedKey.Equals(analysisKey))
        {
            return;
        }

        AnalysisSnapshot enriched = HeadlessSnapshotConverter.WithLatestBeatmapMetadata(current, beatmap);
        if (Equals(enriched, current))
        {
            return;
        }

        AppLogger.Debug(
            "Headless snapshot metadata",
            $"Refreshed delayed Tosu metadata for {beatmap.Identity.StableKey}: star={enriched.Difficulty.StarRating?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}; bpm={enriched.Beatmap.BpmLabel}; keys={enriched.Difficulty.Keys?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}.");
        await PushSnapshotAsync(enriched, analysisKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task PushAnalysisResultSnapshotAsync(
        TosuBeatmapSnapshot snapshot,
        AnalysisResult result,
        HeadlessAnalysisKey analysisKey,
        CancellationToken cancellationToken)
    {
        if (!await IsCurrentAnalysisAsync(analysisKey, cancellationToken).ConfigureAwait(false))
        {
            AppLogger.Info("Headless snapshot push", $"Skipped stale analysis result for beatmap {snapshot.Identity.Id}; Tosu now reports another map.");
            return;
        }

        var headlessSnapshot = HeadlessSnapshotConverter.FromAnalysisResult(snapshot, null, result);
        await PushSnapshotAsync(headlessSnapshot, analysisKey, cancellationToken).ConfigureAwait(false);

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
        return exception.FailureKind == TosuBeatmapSourceFailureKind.OsuNotRunning
            || exception.InnerException is TosuBeatmapSourceException
            {
                FailureKind: TosuBeatmapSourceFailureKind.OsuNotRunning
            };
    }

    private static bool IsNoBeatmapBeatmapException(TosuBeatmapSourceException exception)
    {
        return exception.FailureKind == TosuBeatmapSourceFailureKind.NoBeatmap
            || exception.InnerException is TosuBeatmapSourceException
            {
                FailureKind: TosuBeatmapSourceFailureKind.NoBeatmap
            };
    }
}
