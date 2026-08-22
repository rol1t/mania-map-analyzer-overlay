using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Avalonia.Platform;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Avalonia.ViewModels;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Views;

public partial class MainWindow : Window
{
    private const string BaseUrl = "http://127.0.0.1:24050";
    private const string FullscreenEditorUrl = BaseUrl + "/api/ingame?edit=true";
    private static readonly Uri TosuBaseUri = new(BaseUrl);

    private readonly OverlayPresetCatalog _presetCatalog = new();
    private readonly AnalyzerAdapterCatalog _analyzerCatalog = new();
    private readonly OverlayPresentationService _presentation;
    private readonly FullscreenOverlayService _fullscreen = new();
    private readonly UpdateService _updates = new();
    private readonly WindowsOverlayController _windowsOverlay;
    private readonly DispatcherTimer _overlayResizeDebounceTimer;
    private readonly DispatcherTimer _overlayGameplayPollTimer;
    private readonly DispatcherTimer _headlessBeatmapPollTimer;
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly AnalyzerEngineCatalog _analyzerEngineCatalog = new();
    private readonly AnalyzerEnginePackageDeployer _analyzerEngineDeployer = new();
    private readonly EffectiveAnalysisConfigurationStore _effectiveAnalysisStore = new();
    private readonly ReplayAnalysisSession _replayAnalysisSession = new();
    private EffectiveAnalysisConfiguration _effectiveAnalysisConfiguration = EffectiveAnalysisConfigurationStore.CreateDefault();
    private MainViewModel? _model;
    private CancellationTokenSource? _previewPresentationCancellation;
    private CancellationTokenSource? _overlayGameplayPollCancellation;
    private AnalyzerCoordinator? _analyzerCoordinator;
    private WebViewAnalyzerScriptHost? _analyzerScriptHost;
    private AnalyzerEngineSupervisor? _analyzerSupervisor;
    private TosuBeatmapSource? _tosuBeatmapSource;
    private HttpClient? _tosuBeatmapHttpClient;
    private CancellationTokenSource? _headlessBeatmapPollCancellation;
    private int _headlessBeatmapPollInFlight;
    private string? _lastHeadlessBeatmapKey;
    private AnalyzerEngineSupervisorState? _lastSupervisorState;
    private DateTime _lastOsuNotRunningLogUtc = DateTime.MinValue;
    private WidgetAnalysisRunner? _headlessWidgetRunner;
    private WidgetAnalysisSceneRunner? _headlessSceneRunner;
    private AnalysisRunScope? _headlessSceneScope;
    private string? _lastHeadlessSceneKey;
    private AnalysisSnapshot? _lastAnalyzerSnapshot;
    private bool _initialized;
    private bool _overlayMode;
    private bool _overlayWidgetSized;
    private bool _overlayPlayStateKnown;
    private bool _overlayNativePlayStateKnown;
    private bool _overlayIsPlaying;
    private bool? _overlayIsPaused;
    private bool _overlaySuppressedByPolicy;
    private string _overlayVisibilityPolicy = OverlayVisibilityPolicy.Always;
    private bool _overlayInteractive;
    private bool _suppressOverlayResizeFeedback;
    private bool _overlayResizeScaleUpdateRunning;
    private bool _overlayResizeScaleUpdatePending;
    private bool _overlayNativeResizePending;
    private int _overlayGameplayPollInFlight;
    private bool _componentPreparationFailed;
    private bool _updatingLanguageSelector;
    private readonly Dictionary<string, string> _lastGameplayTraceBySource = new(StringComparer.OrdinalIgnoreCase);
    private int? _overlayExpectedWidgetPhysicalWidth;
    private DateTime _overlayResizeGuardUntilUtc;
    private Size? _ignoredProgrammaticOverlaySize;
    private bool _showingLoggedError;
    private bool _overlayWindowVisible = true;
    private PixelPoint _normalPosition;
    private Size _normalClientSize;

    public MainWindow()
    {
        AppLogger.ErrorRaised += AppLogger_ErrorRaised;
        InitializeComponent();
        _presentation = new OverlayPresentationService(_presetCatalog, _analyzerCatalog);
        _windowsOverlay = new WindowsOverlayController(this);
        _windowsOverlay.ExitRequested += (_, _) => LeaveOverlayMode();
        _windowsOverlay.ClickThroughChanged += enabled => Browser.IsHitTestVisible = !enabled;
        _windowsOverlay.InteractionChanged += interactive =>
        {
            _overlayInteractive = interactive;
            if (_overlayMode)
                CanResize = interactive;
            UpdateOverlayVisibility();
        };
        _windowsOverlay.OsuProcessChanged += running =>
        {
            if (running || !_overlayMode)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                ReturnToLauncherAfterGameExit(
                    "status.osu_closed");
            });
        };
        _overlayResizeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _overlayResizeDebounceTimer.Tick += OverlayResizeDebounceTimer_Tick;
        _overlayGameplayPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _overlayGameplayPollTimer.Tick += OverlayGameplayPollTimer_Tick;
        _headlessBeatmapPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _headlessBeatmapPollTimer.Tick += HeadlessBeatmapPollTimer_Tick;
        SizeChanged += MainWindow_SizeChanged;
        Opened += async (_, _) =>
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception exception)
            {
                AppLogger.Error("Initializing application", exception);
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.ErrorRaised -= AppLogger_ErrorRaised;
        _overlayResizeDebounceTimer.Stop();
        _headlessBeatmapPollTimer.Stop();
        StopOverlayGameplayPolling();
        StopHeadlessBeatmapPolling();
        _previewPresentationCancellation?.Cancel();
        _previewPresentationCancellation?.Dispose();
        _windowsOverlay.Dispose();
        _updates.Dispose();
        _tosuBeatmapHttpClient?.Dispose();
        if (_analyzerCoordinator is not null)
        {
            _analyzerCoordinator.SnapshotChanged -= AnalyzerSnapshotChanged;
        }

        if (_analyzerSupervisor is not null)
        {
            _analyzerSupervisor.StateChanged -= AnalyzerSupervisor_StateChanged;
            _ = _analyzerSupervisor.DisposeAsync().AsTask();
        }

        DisposeHeadlessRunners();
        _analyzerScriptHost?.DisposeAsync().AsTask().ConfigureAwait(false);
        _model?.Dispose();
        base.OnClosed(e);
    }

    private void AppLogger_ErrorRaised(object? sender, AppLogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var statusPrefix = entry.Level == "WARN"
                ? L("status.warning_prefix")
                : L("status.error_prefix");
            var status = statusPrefix + entry.Operation + " — " + entry.Message;
            _model?.SetStatus(status);
            if (!entry.UserVisible || !_initialized || _overlayMode || _showingLoggedError)
                return;

            _showingLoggedError = true;
            try
            {
                ShowMessagePage(
                    L("dialog.error.title"),
                    entry.Operation + Environment.NewLine +
                    entry.Message +
                    (entry.Exception is null
                        ? string.Empty
                        : Environment.NewLine + Environment.NewLine +
                          L("dialog.error.exception_type") + entry.Exception.GetType().FullName) +
                    Environment.NewLine + Environment.NewLine +
                    L("dialog.error.log_path") + AppLogger.LogPath,
                    true);
            }
            finally
            {
                _showingLoggedError = false;
            }
        });
    }

    private async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;
        _model = DataContext as MainViewModel ?? throw new InvalidOperationException("Main view model is unavailable.");
        _analyzerCoordinator = new AnalyzerCoordinator(
            _analyzerCatalog.List().Select(package => package.Adapter),
            _model.Settings.AnalyzerProviderId);
        _analyzerCoordinator.SnapshotChanged += AnalyzerSnapshotChanged;
        ManiaMapAnalyzerOverlay.UiText.Initialize(_model.Settings.Language);
        InitializeLanguageSelector();
        ApplyLanguage();
        if (UiText.LoadError is not null)
        {
            _model.SetStatus(L("dialog.language_resource_error"));
            ShowMessagePage(L("dialog.error.title"), L("dialog.language_resource_error"), true);
        }
        CustomCssService.EnsureExists();
        _model.Tosu.StateChanged += Tosu_StateChanged;
        _windowsOverlay.RegisterHotkeys();
        SetControlsEnabled(false);
        ShowMessagePage(L("dialog.prepare.title"), L("dialog.prepare.message"), false);

        if (!await CheckUpdatesAsync())
            return;
        SynchronizeFullscreenState();
        await _model.StartAsync();
        if (_model.Tosu.IsRunning)
        {
            SetComponentPreparationState(false);
            _model.SetStatus(L("status.tosu_running"), true);
            SetControlsEnabled(true);
            Navigate(AnalysisUrl);
        }
        else
        {
            SetComponentPreparationState(true);
            SetControlsEnabled(false, keepRestart: true);
            ShowMessagePage(L("status.tosu_not_running"), _model.Status, true);
        }

        await InitializeHeadlessEngineAsync();
    }

    private void Tosu_StateChanged(object? sender, TosuStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusDot.Fill = new SolidColorBrush(Color.Parse(e.IsRunning ? "#3DCF8E" : "#FF5F7E"));
            if (e.IsRunning)
            {
                SetControlsEnabled(true);
                if (_analyzerSupervisor is not null)
                {
                    _ = _analyzerSupervisor.NotifyTosuRestartAsync();
                }
            }
            else if (_initialized && _overlayMode)
            {
                ReturnToLauncherAfterGameExit(
                    "status.osu_stopped");
            }
            else if (_initialized)
            {
                SetControlsEnabled(false, keepRestart: true);
            }
        });
    }

    private void ReturnToLauncherAfterGameExit(string statusKey)
    {
        if (!_overlayMode)
            return;
        try
        {
            LeaveOverlayMode();
            _model?.SetStatus(L(statusKey));
        }
        catch (Exception exception)
        {
            AppLogger.Error("Returning to launcher after game exit", exception);
        }
    }

    private async Task InitializeHeadlessEngineAsync()
    {
        try
        {
            if (_analyzerSupervisor is not null)
            {
                _analyzerSupervisor.StateChanged -= AnalyzerSupervisor_StateChanged;
                await _analyzerSupervisor.DisposeAsync();
                _analyzerSupervisor = null;
            }

            _analyzerScriptHost?.DisposeAsync().AsTask().ConfigureAwait(false);
            _analyzerScriptHost = new WebViewAnalyzerScriptHost(Browser);
            _analyzerSupervisor = new AnalyzerEngineSupervisor(
                _analyzerEngineCatalog,
                _analyzerEngineDeployer,
                _analyzerScriptHost);
            _analyzerSupervisor.StateChanged += AnalyzerSupervisor_StateChanged;

            _tosuBeatmapHttpClient?.Dispose();
            _tosuBeatmapHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            _tosuBeatmapSource = new TosuBeatmapSource(_tosuBeatmapHttpClient, TosuBaseUri);
            _effectiveAnalysisConfiguration = _effectiveAnalysisStore.Load();
            AppLogger.Info(
                "Effective analysis configuration",
                $"Loaded effective configuration: engine={_effectiveAnalysisConfiguration.DefaultEngineId} algorithm={_effectiveAnalysisConfiguration.DefaultAlgorithm} widgets={_effectiveAnalysisConfiguration.Widgets.Length}");

            // Ensure the WebView has finished loading the analysis page before
            // bootstrapping the headless runtime. Injecting the runtime too early
            // makes globalThis.location.href point at the previous document and
            // the subsequent navigation resets the bridge, producing engine.runtime_reset.
            await WaitForAnalysisWebViewReadyAsync();

            var preferredEngineId = string.IsNullOrWhiteSpace(_effectiveAnalysisConfiguration.DefaultEngineId)
                ? _analyzerEngineCatalog.Available().FirstOrDefault()?.Id
                : _effectiveAnalysisConfiguration.DefaultEngineId;
            var state = await _analyzerSupervisor.StartAsync(preferredEngineId);
            _lastSupervisorState = state;
            if (state.IsFallback)
            {
                AppLogger.Warning(
                    "Analyzer engine supervisor",
                    $"Headless engine fallback active: {state.Message} Diagnostics: {string.Join("; ", state.Diagnostics.Select(diagnostic => diagnostic.Code))}");
                DisposeHeadlessRunners();
            }
            else if (state.IsReady)
            {
                InitializeHeadlessRunners();
                StartHeadlessBeatmapPolling();
            }

            UpdateHeadlessStatusUi(state);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Initializing headless analyzer engine", exception);
            var fallbackState = new AnalyzerEngineSupervisorState(
                AnalyzerEngineSupervisorStatus.Fallback,
                null,
                null,
                $"Headless engine initialization failed: {exception.Message}. Legacy DOM adapter remains the explicit fallback.",
                [],
                IsFallback: true,
                IsReady: false);
            UpdateHeadlessStatusUi(fallbackState);
        }
    }

    private async Task WaitForAnalysisWebViewReadyAsync()
    {
        try
        {
            if (ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
            {
                await Task.Delay(500);
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, WebViewNavigationCompletedEventArgs e)
            {
                if (e.IsSuccess && ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
                {
                    completion.TrySetResult(true);
                }
            }

            Browser.NavigationCompleted += Handler;
            try
            {
                // Ensure navigation is attempted.
                if (!ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
                {
                    Navigate(AnalysisUrl);
                }

                var completed = await Task.WhenAny(completion.Task, Task.Delay(3000));
                if (completed == completion.Task)
                {
                    try
                    {
                        await completion.Task;
                    }
                    catch (Exception navigationException)
                    {
                        AppLogger.Warning("Waiting for analysis WebView", "WebView navigation task faulted before headless bootstrap.", navigationException);
                    }
                }

                // Give the DOM a moment to settle before injecting the runtime.
                await Task.Delay(400);
            }
            finally
            {
                Browser.NavigationCompleted -= Handler;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Waiting for analysis WebView", $"Could not confirm WebView readiness before bootstrapping headless engine: {exception.Message}", exception);
            await Task.Delay(800);
        }
    }

    private void InitializeHeadlessRunners()
    {
        DisposeHeadlessRunners();
        var coordinator = _analyzerSupervisor?.Coordinator;
        if (coordinator is null)
        {
            AppLogger.Warning("Headless runners", "Cannot initialize widget runners: coordinator is not ready.");
            return;
        }

        try
        {
            _headlessWidgetRunner = new WidgetAnalysisRunner(coordinator);
            _headlessSceneRunner = new WidgetAnalysisSceneRunner(coordinator);
            _headlessSceneScope = new AnalysisRunScope("headless-scene");
            _headlessWidgetRunner.SnapshotComposed += HeadlessWidgetSnapshotComposed;
            _headlessSceneRunner.SnapshotComposed += HeadlessSceneSnapshotComposed;
            AppLogger.Info("Headless runners", $"Initialized widget runners for {_effectiveAnalysisConfiguration.Widgets.Length} widget(s).");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Initializing headless runners", exception);
        }
    }

    private void DisposeHeadlessRunners()
    {
        if (_headlessWidgetRunner is not null)
        {
            _headlessWidgetRunner.SnapshotComposed -= HeadlessWidgetSnapshotComposed;
            _headlessWidgetRunner.Dispose();
            _headlessWidgetRunner = null;
        }

        if (_headlessSceneRunner is not null)
        {
            _headlessSceneRunner.SnapshotComposed -= HeadlessSceneSnapshotComposed;
            _headlessSceneRunner.Dispose();
            _headlessSceneRunner = null;
        }

        _headlessSceneScope?.Dispose();
        _headlessSceneScope = null;
        _lastHeadlessSceneKey = null;
    }

    private void HeadlessWidgetSnapshotComposed(ComposedWidgetSnapshot snapshot)
    {
        AppLogger.Info(
            "Headless widget composition",
            $"Widget '{snapshot.WidgetId}' composed with outcome {snapshot.Outcome} and {snapshot.Metrics.Count} metrics. Diagnostics: {string.Join(", ", snapshot.Diagnostics.Select(diagnostic => diagnostic.Code))}");
    }

    private void HeadlessSceneSnapshotComposed(WidgetAnalysisSceneSnapshot snapshot)
    {
        AppLogger.Info(
            "Headless scene composition",
            $"Scene '{snapshot.SceneId}' generation {snapshot.Generation} composed with {snapshot.OrderedSnapshots.Length} widget(s).");
    }

    private WidgetAnalysisSceneSpec? BuildHeadlessSceneSpec(TosuBeatmapSnapshot snapshot)
    {
        var descriptor = _analyzerSupervisor?.ActiveDescriptor;
        if (descriptor is null)
        {
            AppLogger.Warning("Headless composition", "Cannot build scene spec: no active analyzer descriptor.");
            return null;
        }

        var widgets = new List<WidgetAnalysisSpec>();
        foreach (var effectiveWidget in _effectiveAnalysisConfiguration.Widgets)
        {
            var sources = new List<AnalysisSourceSpec>();
            foreach (var effectiveSource in effectiveWidget.Sources)
            {
                if (!string.Equals(effectiveSource.EngineId, descriptor.Id, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warning(
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
                AppLogger.Warning("Headless composition", $"Widget '{effectiveWidget.WidgetId}' has no usable sources for active engine '{descriptor.Id}'.");
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

    private void AnalyzerSupervisor_StateChanged(object? sender, AnalyzerEngineSupervisorState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _lastSupervisorState = state;
            UpdateHeadlessStatusUi(state);
            if (state.IsReady)
            {
                if (_headlessWidgetRunner is null || _headlessSceneRunner is null)
                {
                    InitializeHeadlessRunners();
                }

                if (_headlessBeatmapPollTimer.IsEnabled == false)
                {
                    StartHeadlessBeatmapPolling();
                }
            }
            else if (state.IsFallback)
            {
                DisposeHeadlessRunners();
                StopHeadlessBeatmapPolling();
            }
        });
    }

    private void UpdateHeadlessStatusUi(AnalyzerEngineSupervisorState state)
    {
        if (_model is null)
        {
            return;
        }

        var prefix = state.IsReady ? "[Headless Ready] " :
                     state.IsFallback ? "[DOM Fallback] " :
                     "[Headless] ";
        var diagnosticsSummary = state.Diagnostics.Count == 0
            ? string.Empty
            : $" Diagnostics: {string.Join(", ", state.Diagnostics.Take(3).Select(diagnostic => diagnostic.Code))}";
        var statusMessage = state.Status switch
        {
            AnalyzerEngineSupervisorStatus.Ready => UiText.Format("status.headless_ready", state.EngineId ?? "unknown") + diagnosticsSummary,
            AnalyzerEngineSupervisorStatus.Fallback => UiText.Format("status.headless_fallback", state.Message) + diagnosticsSummary,
            AnalyzerEngineSupervisorStatus.ProbeFailed => UiText.Format("status.headless_probe_failed", state.Message),
            AnalyzerEngineSupervisorStatus.Error => UiText.Format("status.headless_error", state.Message),
            _ => prefix + state.Message + diagnosticsSummary
        };

        AppLogger.Info("Analyzer engine supervisor state", $"{state.Status} engine={state.EngineId ?? "none"} fallback={state.IsFallback} message={state.Message}");

        if (state.IsReady || state.IsFallback)
        {
            _model.SetStatus(statusMessage);
        }
    }

    private void StartHeadlessBeatmapPolling()
    {
        if (_tosuBeatmapSource is null)
        {
            return;
        }

        StopHeadlessBeatmapPolling();
        _headlessBeatmapPollCancellation = new CancellationTokenSource();
        _headlessBeatmapPollTimer.Start();
        _ = PollHeadlessBeatmapAsync();
    }

    private void StopHeadlessBeatmapPolling()
    {
        _headlessBeatmapPollTimer.Stop();
        _headlessBeatmapPollCancellation?.Cancel();
        _headlessBeatmapPollCancellation?.Dispose();
        _headlessBeatmapPollCancellation = null;
    }

    private async void HeadlessBeatmapPollTimer_Tick(object? sender, EventArgs e) =>
        await PollHeadlessBeatmapAsync();

    private async Task PollHeadlessBeatmapAsync()
    {
        if (_tosuBeatmapSource is null || _analyzerSupervisor is null || Interlocked.Exchange(ref _headlessBeatmapPollInFlight, 1) != 0)
        {
            return;
        }

        var cancellationToken = _headlessBeatmapPollCancellation?.Token ?? CancellationToken.None;

        try
        {
            TosuBeatmapSnapshot snapshot;
            try
            {
                snapshot = await _tosuBeatmapSource.GetCurrentAsync(cancellationToken);
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

                    _lastHeadlessBeatmapKey = null;
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Show a friendly non-error status instead of an exception dialog.
                        // Keep the legacy DOM adapter as fallback while the game is closed.
                        if (_model is not null && !string.Equals(_model.Status, L("status.headless_osu_not_running"), StringComparison.Ordinal))
                        {
                            _model.SetStatus(L("status.headless_osu_not_running"));
                        }
                    });

                    return;
                }

                if (IsNoBeatmapBeatmapException(exception))
                {
                    var now = DateTime.UtcNow;
                    if (now - _lastOsuNotRunningLogUtc > TimeSpan.FromSeconds(5))
                    {
                        _lastOsuNotRunningLogUtc = now;
                        AppLogger.Info("Headless beatmap poll", "No current beatmap is available — osu! is running but no map is selected.");
                    }

                    // Do not treat as error: keep polling and do not overwrite a useful status
                    // with a stale map. Clearing the key forces a re-analysis once a map appears.
                    _lastHeadlessBeatmapKey = null;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_model is not null && string.IsNullOrWhiteSpace(_model.Status))
                        {
                            _model.SetStatus(L("status.headless_no_beatmap"));
                        }
                    });

                    return;
                }

                AppLogger.Warning("Headless beatmap poll", exception.Message, exception);
                return;
            }

            var key = snapshot.Identity.StableKey + "|" + snapshot.Rate.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(",", snapshot.Mods) + "|" + snapshot.RawBeatmap.Length;
            var effectiveKey = _effectiveAnalysisConfiguration.ConfigurationVersion + "|" + _effectiveAnalysisConfiguration.DefaultEngineId + "|" + _effectiveAnalysisConfiguration.DefaultAlgorithm + "|" + _effectiveAnalysisConfiguration.Widgets.Length;
            var combinedKey = key + "|" + effectiveKey;
            var sceneKey = snapshot.Identity.StableKey + "|" + snapshot.Rate.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(",", snapshot.Mods) + "|" + effectiveKey;
            if (string.Equals(combinedKey, _lastHeadlessBeatmapKey, StringComparison.Ordinal) &&
                string.Equals(sceneKey, _lastHeadlessSceneKey, StringComparison.Ordinal))
            {
                return;
            }

            // Scene generation is invalidated when map/rate/mods or effective config changes.
            var isNewSceneGeneration = !string.Equals(sceneKey, _lastHeadlessSceneKey, StringComparison.Ordinal);
            if (isNewSceneGeneration)
            {
                AppLogger.Info("Headless scene", $"Effective scene generation invalidated: newKey={sceneKey}");
            }

            _lastHeadlessBeatmapKey = combinedKey;
            _lastHeadlessSceneKey = sceneKey;
            AppLogger.Info(
                "Headless beatmap poll",
                $"New beatmap {snapshot.Identity.StableKey} title={snapshot.Metadata.Title} version={snapshot.Metadata.Version} rate={snapshot.Rate} mods=[{string.Join(",", snapshot.Mods)}] effective={effectiveKey}");

            // Prefer composed widget/scene execution so shared analyzer results are de-duplicated
            // and rate/mods are execution dimensions per source, not per beatmap generation.
            if (_headlessSceneRunner is not null && _headlessWidgetRunner is not null)
            {
                var sceneSpec = BuildHeadlessSceneSpec(snapshot);
                if (sceneSpec is not null)
                {
                    try
                    {
                        var sceneSnapshot = await _headlessSceneRunner.RunAsync(sceneSpec, cancellationToken);
                        Dispatcher.UIThread.Post(() =>
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

                            var first = sceneSnapshot.OrderedSnapshots.FirstOrDefault();
                            if (first is not null)
                            {
                                if (first.Outcome == AnalysisOutcome.Partial)
                                {
                                    var diag = first.Diagnostics.Length == 0 ? string.Empty : $" {string.Join("; ", first.Diagnostics.Take(2).Select(diagnostic => diagnostic.Code))}";
                                    _model?.SetStatus(L("status.headless_partial") + $" {first.Metrics.Values.FirstOrDefault()?.Provenance.ActualAlgorithm ?? snapshot.Metadata.Version} partial" + diag);
                                }
                                else if (first.Outcome == AnalysisOutcome.Success)
                                {
                                    var star = first.Metrics.TryGetValue("difficulty.star", out var metric) ? metric.Metric.Value.ToString() ?? "n/a" : "n/a";
                                    var algo = first.Metrics.Values.FirstOrDefault()?.Provenance.ActualAlgorithm ?? snapshot.Metadata.Version;
                                    _model?.SetStatus(L("status.headless_success") + $" {snapshot.Metadata.Title} [{snapshot.Metadata.Version}] {algo} star={star}");
                                }
                                else if (first.Outcome == AnalysisOutcome.Failed)
                                {
                                    var diag = first.Diagnostics.Length == 0 ? string.Empty : $" {string.Join("; ", first.Diagnostics.Take(2).Select(diagnostic => diagnostic.Code))}";
                                    _model?.SetStatus(L("status.headless_failed") + diag + " (DOM fallback)");
                                }
                            }
                        });

                        // Push the domain-level snapshot to the WebView so the renderer can consume it without querying DOM selectors.
                        try
                        {
                            var firstWidget = sceneSnapshot.OrderedSnapshots.FirstOrDefault();
                            if (firstWidget is not null)
                            {
                                var headlessSnapshot = HeadlessSnapshotConverter.FromComposed(snapshot, null, firstWidget);
                                await PushHeadlessSnapshotAsync(headlessSnapshot, cancellationToken);
                            }
                        }
                        catch (Exception pushException)
                        {
                            AppLogger.Warning("Headless snapshot push", $"Failed to push scene snapshot: {pushException.Message}", pushException);
                        }

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

            var result = await _analyzerSupervisor.AnalyzeAsync(snapshot, cancellationToken: cancellationToken);
            if (result is null)
            {
                AppLogger.Info("Headless analysis", $"Headless analysis returned no result for {snapshot.Identity.StableKey}. DOM adapter remains the explicit fallback for this beatmap.");
                return;
            }

            Dispatcher.UIThread.Post(() =>
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
                AppLogger.Info(
                    "Headless analysis result",
                    $"Beatmap {snapshot.Identity.StableKey} outcome={outcomeText} metrics=[{metricsSummary}]{diagnosticsSummary}");

                if (result.Outcome == AnalysisOutcome.Partial)
                {
                    _model?.SetStatus(L("status.headless_partial") + $" {result.ActualAlgorithm ?? snapshot.Metadata.Version} partial" + diagnosticsSummary);
                }
                else if (result.Outcome == AnalysisOutcome.Success)
                {
                    _model?.SetStatus(L("status.headless_success") + $" {snapshot.Metadata.Title} [{snapshot.Metadata.Version}] {result.ActualAlgorithm} star={result.Metrics.GetValueOrDefault("difficulty.star")?.Value.ToString() ?? "n/a"}");
                }
                else if (result.Outcome == AnalysisOutcome.Failed)
                {
                    _model?.SetStatus(L("status.headless_failed") + diagnosticsSummary + " (DOM fallback)");
                }
            });

            try
            {
                var headlessSnapshot = HeadlessSnapshotConverter.FromAnalysisResult(snapshot, null, result);
                await PushHeadlessSnapshotAsync(headlessSnapshot, cancellationToken);
            }
            catch (Exception pushException)
            {
                AppLogger.Warning("Headless snapshot push", $"Failed to push single snapshot: {pushException.Message}", pushException);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Error("Polling headless beatmap", exception, userVisible: false);
        }
        finally
        {
            Interlocked.Exchange(ref _headlessBeatmapPollInFlight, 0);
        }
    }

    private async Task PushHeadlessSnapshotAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var script = $"window.dispatchEvent(new CustomEvent('analysis:snapshot', {{detail: {json}}})); if (typeof window._overlayRenderAnalysisSnapshot === 'function') window._overlayRenderAnalysisSnapshot({json});";
            await Browser.InvokeScript(script);
            AppLogger.Info("Headless snapshot push", $"Pushed headless snapshot for beatmap {snapshot.Beatmap.Title} [{snapshot.Beatmap.Version}] to WebView.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Pushing headless snapshot", $"Could not push headless snapshot to WebView: {exception.Message}", exception);
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

    private async Task<bool> CheckUpdatesAsync()
    {
        if (_model is null)
            return false;
        _model.SetStatus(L("status.checking_updates"));
        try
        {
            var progress = new Progress<UpdateProgress>(update =>
                _model.SetStatus(LocalizeUpdateMessage(update.Message)));
            var result = await _updates.CheckComponentsAsync(progress: progress);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? L("status.update_failed"));
            if (result.LauncherUpdateAvailable)
            {
                var accept = await ConfirmAsync(L("dialog.update_available.title"),
                    UiText.Format("dialog.update_available.message", result.LatestLauncherVersion));
                if (accept && _updates.StartSelfUpdate())
                {
                    Close();
                    return false;
                }
            }
            if (result.UpdatedTosu || result.UpdatedAddon)
                _model.SetStatus(L("status.components_updated"));
            else if (string.Equals(result.Compatibility, "unsupported", StringComparison.OrdinalIgnoreCase))
                await InfoAsync(L("dialog.compatibility.title"), UiText.Format("dialog.compatibility.message", result.LazerVersion));
            if (!string.IsNullOrWhiteSpace(result.Warning))
                _model.SetStatus(LocalizeResourceOrText(result.Warning));

            SetComponentPreparationState(false);
            return true;
        }
        catch (Exception exception)
        {
            SetComponentPreparationState(true);
            var title = L("dialog.components_error.title");
            var retry = L("dialog.components_error.message");
            var details = exception.Message.Trim();
            _model.SetStatus(title);
            SetControlsEnabled(false, keepRestart: true);
            ShowMessagePage(title, string.IsNullOrWhiteSpace(details) ? retry : retry + "\n\n" + details, true);
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "startup-update-error.log"),
                    DateTime.Now + Environment.NewLine + exception);
            }
            catch (Exception logException)
            {
                AppLogger.Warning("Writing startup update error details", "Could not persist the startup error details.", logException);
            }
            return false;
        }
    }

    private void SetComponentPreparationState(bool failed)
    {
        _componentPreparationFailed = failed;
        RestartButton.Content = failed
            ? L("status.retry_preparation")
            : L("status.restart");
    }

    private string LocalizeUpdateMessage(string message)
    {
        if (message.StartsWith("status.", StringComparison.Ordinal))
        {
            var separator = message.IndexOf('|');
            return separator > 0
                ? UiText.Format(message[..separator], message[(separator + 1)..])
                : L(message);
        }
        return message switch
        {
            "Checking component releases…" => L("status.update_checking"),
            "Downloading tosu…" => L("status.update_tosu_download"),
            "Downloading ManiaMapAnalyser…" => L("status.update_analyser_download"),
            "Components are ready." => L("status.update_ready"),
            "Components are up to date." => L("status.update_current"),
            "Component preparation failed." => L("status.update_failed"),
            _ when message.StartsWith("Downloading tosu ", StringComparison.Ordinal) => L("status.update_tosu_download"),
            _ when message.StartsWith("Downloading ManiaMapAnalyser ", StringComparison.Ordinal) => L("status.update_analyser_download"),
            _ => message
        };
    }

    private void SynchronizeFullscreenState()
    {
        if (_model is null)
            return;
        var enabled = _fullscreen.ReadEnabled(_model.Settings.FullscreenOverlayEnabled);
        if (enabled && !ActiveAnalyzer.Descriptor.SupportsFullscreen)
        {
            if (_fullscreen.IsSupported)
                _fullscreen.SetEnabled(false);
            enabled = false;
        }
        _model.Settings.FullscreenOverlayEnabled = enabled;
        if (enabled)
        {
            _fullscreen.EnsureProfile(
                _model.Settings,
                ActiveAnalyzer.Descriptor,
                _model.Settings.FullscreenOverlayStyleVersion < 1);
            _model.Settings.FullscreenOverlayStyleVersion = 1;
        }
        _model.SaveSettings();
        UpdateFullscreenButton();
    }

    private void ApplyLanguage()
    {
        if (_model is null)
            return;
        Title = L("window.title");
        BrandText.Text = L("app.brand");
        AnalysisButton.Content = L("button.map_analysis");
        AppearanceButton.Content = L("button.appearance");
        ReplayButton.Content = L("button.replay");
        MappingButton.Content = L("button.mapping");
        HelpButton.Content = L("button.help");
        OverlayButton.Content = L("button.overlay");
        DashboardButton.Content = L("button.tosu_panel");
        SetComponentPreparationState(_componentPreparationFailed);
        ExitButton.Content = L("button.exit");
        RefreshLanguageSelector();
        UpdatePreviewScaleText();
        UpdateFullscreenButton();
    }

    private string LocalizeResourceOrText(string value)
    {
        return value.StartsWith("update.", StringComparison.Ordinal)
            ? L(value)
            : value;
    }

    private void InitializeLanguageSelector()
    {
        _updatingLanguageSelector = true;
        try
        {
            LanguageSelector.ItemsSource = UiText.Languages;
            RefreshLanguageSelector();
        }
        finally
        {
            _updatingLanguageSelector = false;
        }
    }

    private void RefreshLanguageSelector()
    {
        if (LanguageSelector is null)
            return;
        _updatingLanguageSelector = true;
        try
        {
            LanguageSelector.SelectedItem = UiText.Languages.FirstOrDefault(language =>
                string.Equals(language.Id, UiText.CurrentLanguage, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _updatingLanguageSelector = false;
        }
    }

    private string L(string key) => ManiaMapAnalyzerOverlay.UiText.Get(key);

    private AnalyzerAdapterPackage ActiveAnalyzer =>
        _presentation.ResolveAnalyzer(_model?.Settings.AnalyzerProviderId);

    private string AnalysisUrl => ActiveAnalyzer.GetAnalysisUri(TosuBaseUri).ToString();

    private void SetControlsEnabled(bool enabled, bool keepRestart = false)
    {
        AnalysisButton.IsEnabled = enabled;
        AppearanceButton.IsEnabled = enabled;
        ReplayButton.IsEnabled = enabled;
        MappingButton.IsEnabled = enabled;
        HelpButton.IsEnabled = enabled;
        PreviewScaleDownButton.IsEnabled = enabled;
        PreviewScaleUpButton.IsEnabled = enabled;
        OverlayButton.IsEnabled = enabled;
        FullscreenButton.IsEnabled = enabled && _fullscreen.IsSupported && ActiveAnalyzer.Descriptor.SupportsFullscreen;
        DashboardButton.IsEnabled = enabled;
        RestartButton.IsEnabled = enabled || keepRestart;
    }

    private void UpdatePreviewScaleText()
    {
        if (PreviewScaleText is not null && _model is not null)
            PreviewScaleText.Content = _model.Settings.OverlayScalePercent + "%";
    }

    private void Navigate(string url)
    {
        try
        {
            Browser.Navigate(new Uri(url));
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Navigating browser to '{url}'", exception);
        }
    }

    private void ShowMessagePage(string title, string message, bool error)
    {
        var accent = error ? "#ff5f7e" : "#8a7dff";
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        var safeMessage = System.Net.WebUtility.HtmlEncode(message).Replace("\n", "<br>");
        var loadingCss = (_presetCatalog.ReadRuntimeAsset("loading.css") ?? string.Empty)
            .Replace("var(--overlay-accent)", accent, StringComparison.Ordinal);
        var html = "<!doctype html><html><head><meta charset='utf-8'><style>" + loadingCss + "</style></head><body><div class='box'><div class='ring" + (error ? " error" : "") + "'></div><h1>" + safeTitle + "</h1><p>" + safeMessage + "</p></div></body></html>";
        try
        {
            Browser.NavigateToString(html, new Uri(BaseUrl));
        }
        catch (Exception exception)
        {
            AppLogger.Error("Showing error page", exception, userVisible: false);
        }
    }

    private async void Browser_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        try
        {
            if (e.IsSuccess && ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
            {
                await ApplyPresentationAsync();
            }

            if (_analyzerSupervisor is not null)
            {
                var status = _analyzerSupervisor.CurrentState.Status;
                if (status == AnalyzerEngineSupervisorStatus.Ready ||
                    status == AnalyzerEngineSupervisorStatus.Fallback ||
                    status == AnalyzerEngineSupervisorStatus.Error)
                {
                    await _analyzerSupervisor.NotifyNavigationAsync();
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Handling browser navigation", exception);
        }
    }

    private void Browser_NewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e) => e.Handled = true;

    private void Browser_WebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            HandleBrowserWebMessage(e);
        }
        catch (Exception exception) { AppLogger.Error("Handling browser overlay message", exception); }
    }

    private void HandleBrowserWebMessage(WebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Body))
            return;
        var message = e.Body;

        if (message.StartsWith(AnalyzerEngineScriptBridge.NativeMessagePrefix, StringComparison.Ordinal))
        {
            // WebViewAnalyzerScriptHost already subscribes to Browser.WebMessageReceived and forwards
            // this message to the bridge. Logging here is sufficient and avoids duplicate delivery.
            if (_analyzerSupervisor is not null && !_analyzerSupervisor.IsReady)
            {
                AppLogger.Info("Analyzer engine bridge", $"Received bridge message while supervisor state={_analyzerSupervisor.CurrentState.Status}.");
            }

            return;
        }

        if (message.StartsWith("overlay:error:", StringComparison.Ordinal))
        {
            AppLogger.Error("Overlay runtime", Uri.UnescapeDataString(message[14..]));
            return;
        }
        if (TryHandleGameplayStateTrace(message))
            return;
        if (TryHandleAnalyzerMessage(message))
            return;
        if (!_overlayMode)
            return;
        if (message == "overlay:drag")
        {
            _windowsOverlay.BeginDrag();
            return;
        }
        const string resizePrefix = "overlay:resize:";
        if (message.StartsWith(resizePrefix, StringComparison.Ordinal))
        {
            _windowsOverlay.BeginResize(message[resizePrefix.Length..]);
            return;
        }
        if (message == "overlay:play:1")
        {
            if (!_overlayNativePlayStateKnown)
                SetOverlaySuppressedByPlay(true, _overlayIsPaused);
            return;
        }
        if (message == "overlay:play:0")
        {
            if (!_overlayNativePlayStateKnown)
                SetOverlaySuppressedByPlay(false, false);
            return;
        }
        if (message == "overlay:pause:1")
        {
            if (!_overlayNativePlayStateKnown && _overlayPlayStateKnown)
                SetOverlaySuppressedByPlay(_overlayIsPlaying, true);
            return;
        }
        if (message == "overlay:pause:0")
        {
            if (!_overlayNativePlayStateKnown && _overlayPlayStateKnown)
                SetOverlaySuppressedByPlay(_overlayIsPlaying, false);
            return;
        }
        if (message == "overlay:focus:1")
        {
            _windowsOverlay.SetOsuFocused(true);
            return;
        }
        if (message == "overlay:focus:0")
        {
            _windowsOverlay.SetOsuFocused(false);
            return;
        }
        const string scalePrefix = "overlay:scale:";
        if (message.StartsWith(scalePrefix, StringComparison.Ordinal) &&
            int.TryParse(message[scalePrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            _ = AdjustScaleAsync(delta);
            return;
        }
        const string sizePrefix = "overlay:size:";
        if (!message.StartsWith(sizePrefix, StringComparison.Ordinal))
            return;
        var values = message[sizePrefix.Length..].Split(',');
        if (values.Length == 3 && int.TryParse(values[0], out var width) && int.TryParse(values[1], out var height) &&
            float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            ResizeOverlayToWidget(width, height);
    }

    private bool TryHandleGameplayStateTrace(string message)
    {
        const string prefix = "overlay:state-debug:";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var payload = Uri.UnescapeDataString(message[prefix.Length..]);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var source = root.TryGetProperty("source", out var sourceElement) &&
                         sourceElement.ValueKind == JsonValueKind.String
                ? sourceElement.GetString() ?? "browser"
                : "browser";
            var name = root.TryGetProperty("name", out var nameElement) &&
                       nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            int? number = root.TryGetProperty("number", out var numberElement) &&
                          numberElement.TryGetInt32(out var parsedNumber)
                ? parsedNumber
                : null;
            bool? isPlaying = root.TryGetProperty("isPlaying", out var playingElement) &&
                              playingElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? playingElement.GetBoolean()
                : null;
            bool? isPaused = root.TryGetProperty("isPaused", out var pausedElement) &&
                             pausedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? pausedElement.GetBoolean()
                : null;
            bool? isFocused = root.TryGetProperty("focused", out var focusedElement) &&
                              focusedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? focusedElement.GetBoolean()
                : null;
            TraceGameplayState(source, name, number, isPlaying, isPaused, isFocused);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Reading gameplay state trace", exception, userVisible: false);
        }

        return true;
    }

    private void TraceGameplayState(
        string source,
        string name,
        int? number,
        bool? isPlaying,
        bool? isPaused,
        bool? isFocused)
    {
        if (!_overlayMode)
            return;

        var signature = string.Join(
            '|',
            name,
            number?.ToString(CultureInfo.InvariantCulture) ?? "null",
            isPlaying?.ToString() ?? "null",
            isPaused?.ToString() ?? "null",
            isFocused?.ToString() ?? "null");
        if (_lastGameplayTraceBySource.TryGetValue(source, out var previousSignature) &&
            string.Equals(previousSignature, signature, StringComparison.Ordinal))
            return;

        _lastGameplayTraceBySource[source] = signature;
        AppLogger.Info(
            "Gameplay state trace",
            $"source={source}; name={name}; number={number?.ToString(CultureInfo.InvariantCulture) ?? "null"}; " +
            $"isPlaying={isPlaying?.ToString() ?? "null"}; paused={isPaused?.ToString() ?? "null"}; " +
            $"focused={isFocused?.ToString() ?? "null"}; " +
            $"nativeAuthoritative={_overlayNativePlayStateKnown}; widgetSized={_overlayWidgetSized}; opacity={Opacity:0.##}");
    }

    private bool TryHandleAnalyzerMessage(string message)
    {
        const string prefix = "analysis:";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var payload = message[prefix.Length..];
        var separator = payload.IndexOf(':');
        if (separator <= 0 || separator == payload.Length - 1)
        {
            AppLogger.Error(
                "Handling analyzer message",
                new InvalidDataException("The analyzer bridge sent a malformed analysis message."));
            return true;
        }

        var adapterId = payload[..separator];
        var json = payload[(separator + 1)..];
        _analyzerCoordinator?.TryAccept(adapterId, json, out _);
        return true;
    }

    private void AnalyzerSnapshotChanged(AnalysisSnapshot snapshot)
    {
        _lastAnalyzerSnapshot = snapshot;
        if (!_overlayMode || _overlayNativePlayStateKnown || snapshot.Gameplay.IsPlaying is not bool isPlaying)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_overlayMode)
                SetOverlaySuppressedByPlay(isPlaying, snapshot.Gameplay.IsPaused);
        });
    }

    private async Task ApplyPresentationAsync()
    {
        if (_model is null)
            return;
        await ApplyPresentationAsync(_model.Settings, _overlayMode, updateFullscreen: true, reportErrors: true, CancellationToken.None);
    }

    private async Task ApplyPresentationAsync(
        LauncherSettings settings,
        bool presentationOverlayMode,
        bool updateFullscreen,
        bool reportErrors,
        CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await _presentationGate.WaitAsync(cancellationToken);
            entered = true;
            var analyzer = _presentation.ResolveAnalyzer(settings.AnalyzerProviderId);
            var scripts = _presentation.Build(settings, presentationOverlayMode);
            await Browser.InvokeScript(scripts.SetupScript);
            await Browser.InvokeScript(scripts.ObserverScript);
            var presentationState = await Browser.InvokeScript(
                "JSON.stringify({layout:document.documentElement.className,replayNode:!!document.getElementById('overlay-replay'),card:!!document.querySelector('.main-card')})");
            AppLogger.Info(
                "Overlay presentation state",
                presentationState ?? "The WebView returned no presentation state.");
            await Task.Delay(250);
            var replayLayoutState = await Browser.InvokeScript(
                "(function(){var n=document.getElementById('overlay-replay');if(!n)return 'replayNode=missing';var r=n.getBoundingClientRect(),s=getComputedStyle(n);return JSON.stringify({hidden:n.hidden,display:s.display,visibility:s.visibility,opacity:s.opacity,top:r.top,height:r.height,bottom:r.bottom,offsetParent:!!n.offsetParent,overflow:getComputedStyle(document.querySelector('.main-card')||document.body).overflow});})()");
            AppLogger.Info(
                "Overlay replay layout state",
                replayLayoutState ?? "The WebView returned no replay layout state.");
            if (updateFullscreen && settings.FullscreenOverlayEnabled)
                _fullscreen.WriteRuntime(
                    settings,
                    analyzer.Descriptor,
                    scripts.FullscreenSetupScript,
                    scripts.FullscreenObserverScript);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            AppLogger.Info("Applying overlay presentation", $"Operation canceled: {exception.Message}");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Applying overlay presentation", exception, userVisible: false);
            if (reportErrors && _model is not null)
            {
                _model.SetStatus(L("dialog.configuration_error") + ": " + exception.Message);
                if (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    ShowMessagePage(
                        L("appearance.resources_missing"),
                        exception.Message,
                        true);
                }
            }
        }
        finally
        {
            if (entered)
                _presentationGate.Release();
        }
    }

    private async Task RequestOverlayWidgetSizeReportAsync()
    {
        var entered = false;
        try
        {
            await _presentationGate.WaitAsync();
            entered = true;
            await Browser.InvokeScript("window.dispatchEvent(new Event('resize'));");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Requesting overlay size report", exception, userVisible: false);
        }
        finally
        {
            if (entered)
                _presentationGate.Release();
        }
    }

    private async Task AdjustScaleAsync(int delta)
    {
        try
        {
            if (_model is null)
                return;
            _overlayResizeDebounceTimer.Stop();
            _overlayResizeScaleUpdatePending = false;
            _overlayNativeResizePending = false;
            _overlayExpectedWidgetPhysicalWidth = null;
            _overlayResizeGuardUntilUtc = default;
            var next = Math.Clamp(_model.Settings.OverlayScalePercent + delta, 50, 180);
            if (next == _model.Settings.OverlayScalePercent)
                return;
            _model.Settings.OverlayScalePercent = next;
            _model.SaveSettings();
            UpdatePreviewScaleText();
            await ApplyPresentationAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Adjusting overlay scale", exception);
        }
    }

    private async void PreviewScaleDown_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await AdjustScaleAsync(-5);
        }
        catch (Exception exception) { AppLogger.Error("Decreasing preview scale", exception); }
    }

    private async void PreviewScaleUp_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await AdjustScaleAsync(5);
        }
        catch (Exception exception) { AppLogger.Error("Increasing preview scale", exception); }
    }

    private async void Analysis_Click(object? sender, RoutedEventArgs e)
    {
        Navigate(AnalysisUrl);
        await Task.CompletedTask;
    }

    private async void Replay_Click(object? sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("replay.import.title"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(L("replay.import.file_type"))
                    {
                        Patterns = ["*.osr"]
                    }
                ]
            });
            var file = files.FirstOrDefault();
            if (file is null)
            {
                return;
            }

            await using var input = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            _replayAnalysisSession.Import(buffer.ToArray(), file.Name);
            _model.SetStatus(UiText.Format("replay.import.selected", file.Name));

            if (_tosuBeatmapSource is null)
            {
                _model.SetStatus(L("replay.import.no_beatmap_source"));
                return;
            }

            var beatmap = await _tosuBeatmapSource.GetCurrentAsync();
            var result = await _replayAnalysisSession.AnalyzeAsync(beatmap);
            var baseSnapshot = _lastAnalyzerSnapshot;
            if (baseSnapshot is null)
            {
                baseSnapshot = HeadlessSnapshotConverter.FromComposed(
                    beatmap,
                    null,
                    new ComposedWidgetSnapshot("replay-base", AnalysisOutcome.Success, [], []));
            }

            var replaySnapshot = HeadlessSnapshotConverter.WithReplayAnalysis(baseSnapshot, result);
            await PushHeadlessSnapshotAsync(replaySnapshot, CancellationToken.None);
            AppLogger.Info(
                "Replay import",
                $"file={file.Name}; outcome={result.Outcome}; metrics={result.Metrics.Count}; " +
                $"replayData={replaySnapshot.Replay?.HasData.ToString() ?? "false"}; " +
                $"columns={replaySnapshot.Replay?.Columns.Count.ToString() ?? "0"}");
            var diagnostic = result.Diagnostics.FirstOrDefault();
            if (result.Outcome == AnalysisOutcome.Success)
            {
                _model.SetStatus(UiText.Format("replay.import.success", file.Name));
            }
            else
            {
                _model.SetStatus(
                    UiText.Format(
                        "replay.import.failed",
                        diagnostic?.Message ?? result.Outcome.ToString()));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ReplayAnalysisException exception)
        {
            AppLogger.Warning("Importing replay", exception.Message, exception);
            _model.SetStatus(UiText.Format("replay.import.failed", exception.Message));
        }
        catch (Exception exception)
        {
            AppLogger.Error("Importing replay", exception);
            _model.SetStatus(UiText.Format("replay.import.failed", exception.Message));
        }
    }

    private void Dashboard_Click(object? sender, RoutedEventArgs e) => Navigate(BaseUrl + "/");

    private async void Appearance_Click(object? sender, RoutedEventArgs e)
    {
        if (_model is null)
            return;
        var dialog = new AppearanceDialog(_model.Settings);
        dialog.PreviewChanged += AppearancePreviewChanged;
        bool accepted;
        try
        {
            accepted = await dialog.ShowDialog<bool>(this);
        }
        catch (Exception exception)
        {
            StopAppearancePreview();
            AppLogger.Error("Opening overlay appearance dialog", exception);
            return;
        }
        finally
        {
            dialog.PreviewChanged -= AppearancePreviewChanged;
        }
        StopAppearancePreview();
        if (!accepted)
        {
            await ApplyPresentationAsync();
            return;
        }
        if (dialog.OpenAnalyzerSettings)
        {
            var selectedAnalyzer = _presentation.ResolveAnalyzer(dialog.AnalyzerProviderId);
            var settingsUri = selectedAnalyzer.GetSettingsUri(TosuBaseUri);
            if (settingsUri is not null)
                Navigate(settingsUri.ToString());
            return;
        }
        var analyzerChanged = !string.Equals(
            _model.Settings.AnalyzerProviderId,
            dialog.AnalyzerProviderId,
            StringComparison.OrdinalIgnoreCase);
        _model.Settings.AnalyzerProviderId = dialog.AnalyzerProviderId;
        if (analyzerChanged)
            _analyzerCoordinator?.Switch(_model.Settings.AnalyzerProviderId);
        _model.Settings.OverlayLayoutMode = dialog.LayoutMode;
        _model.Settings.OverlayPresetId = dialog.PresetId;
        _model.Settings.OverlayScalePercent = dialog.ScalePercent;
        UpdatePreviewScaleText();
        var restartForFullscreen = false;
        if (_model.Settings.FullscreenOverlayEnabled && !ActiveAnalyzer.Descriptor.SupportsFullscreen)
        {
            if (_fullscreen.IsSupported)
                _fullscreen.SetEnabled(false);
            _model.Settings.FullscreenOverlayEnabled = false;
            restartForFullscreen = true;
        }
        else if (_model.Settings.FullscreenOverlayEnabled)
        {
            _fullscreen.EnsureProfile(_model.Settings, ActiveAnalyzer.Descriptor, true);
            restartForFullscreen = true;
        }
        _model.SaveSettings();
        if (restartForFullscreen)
            await _model.RestartAsync();
        Navigate(AnalysisUrl);
    }

    private void AppearancePreviewChanged(LauncherSettings previewSettings)
    {
        if (_model is null || !ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
            return;
        _previewPresentationCancellation?.Cancel();
        _previewPresentationCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _previewPresentationCancellation = cancellation;
        _ = ApplyAppearancePreviewAsync(previewSettings, cancellation.Token);
    }

    private async Task ApplyAppearancePreviewAsync(LauncherSettings previewSettings, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyPresentationAsync(
                previewSettings,
                presentationOverlayMode: false,
                updateFullscreen: false,
                reportErrors: false,
                cancellationToken);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Applying live appearance preview", exception, userVisible: false);
        }
    }

    private void StopAppearancePreview()
    {
        _previewPresentationCancellation?.Cancel();
        _previewPresentationCancellation?.Dispose();
        _previewPresentationCancellation = null;
    }

    private async void Mapping_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new AnalysisMappingDialog();
            var accepted = await dialog.ShowDialog<bool>(this);
            if (!accepted)
            {
                return;
            }

            _effectiveAnalysisConfiguration = _effectiveAnalysisStore.Load();
            AppLogger.Info(
                "Effective analysis mapping",
                $"Reloaded mapping: {_effectiveAnalysisConfiguration.Widgets.Length} widget(s), engine={_effectiveAnalysisConfiguration.DefaultEngineId}, algorithm={_effectiveAnalysisConfiguration.DefaultAlgorithm}");
            InitializeHeadlessRunners();
            _lastHeadlessSceneKey = null;
            _lastHeadlessBeatmapKey = null;
            if (_analyzerSupervisor?.IsReady == true)
            {
                _ = PollHeadlessBeatmapAsync();
            }

            _model?.SetStatus(L("mapping.title") + ": " + _effectiveAnalysisConfiguration.Widgets.Length + " widget(s)");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Opening analysis mapping dialog", exception);
        }
    }

    private async void Help_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new DocumentationDialog("overview");
            await dialog.ShowDialog(this);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Opening documentation", exception);
        }
    }

    private async void Restart_Click(object? sender, RoutedEventArgs e)
    {
        if (_model is null)
            return;
        SetComponentPreparationState(_componentPreparationFailed);
        SetControlsEnabled(false);
        ShowMessagePage(L("dialog.prepare_tosu.title"), L("dialog.prepare_tosu.message"), false);
        if (!await CheckUpdatesAsync())
            return;
        await _model.RestartAsync();
        var running = _model.Tosu.IsRunning;
        if (running)
        {
            SetComponentPreparationState(false);
            _model.SetStatus(L("status.tosu_running"), true);
        }
        else
        {
            SetComponentPreparationState(true);
            ShowMessagePage(L("status.tosu_not_running"), L("dialog.components_error.message"), true);
        }
        SetControlsEnabled(running, keepRestart: !running);
        if (running)
        {
            Navigate(AnalysisUrl);
            await InitializeHeadlessEngineAsync();
        }

        if (!running && _analyzerSupervisor is not null)
        {
            await _analyzerSupervisor.NotifyTosuRestartAsync();
        }
    }

    private void LanguageSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_model is null || _updatingLanguageSelector || LanguageSelector.SelectedItem is not LanguageOption selected)
            return;
        UiText.Initialize(selected.Id);
        _model.Settings.Language = UiText.CurrentLanguage;
        _model.SaveSettings();
        ApplyLanguage();
        _model.SetStatus(L(_model.Tosu.IsRunning ? "status.tosu_running" : "status.tosu_not_running"), _model.Tosu.IsRunning);
        if (ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
            Browser.Refresh();
    }

    private async void Overlay_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await EnterOverlayModeAsync();
        }
        catch (Exception exception) { AppLogger.Error("Entering overlay mode", exception); }
    }

    private async Task EnterOverlayModeAsync()
    {
        if (_model is null || _overlayMode)
            return;
        if (OperatingSystem.IsWindows() && !_windowsOverlay.RegisterHotkeys())
        {
            await InfoAsync(L("dialog.hotkey.title"), L("dialog.hotkey.message"));
            return;
        }
        if (_model.Settings.OverlayHintVersion < 3)
        {
            await InfoAsync(L("dialog.overlay.title"), L("dialog.overlay.message"));
            _model.Settings.OverlayHintVersion = 3;
            _model.SaveSettings();
        }

        _normalPosition = Position;
        _normalClientSize = ClientSize;
        _overlayMode = true;
        SetOverlayWindowVisibility(false);
        _overlayWidgetSized = false;
        _overlayPlayStateKnown = false;
        _overlayNativePlayStateKnown = false;
        _lastGameplayTraceBySource.Clear();
        _overlayIsPlaying = false;
        _overlayIsPaused = null;
        _overlaySuppressedByPolicy = false;
        _overlayVisibilityPolicy = ResolveOverlayVisibilityPolicy();
        _overlayInteractive = false;
        _suppressOverlayResizeFeedback = false;
        _overlayResizeScaleUpdatePending = false;
        _overlayNativeResizePending = false;
        _overlayExpectedWidgetPhysicalWidth = null;
        _overlayResizeGuardUntilUtc = default;
        _ignoredProgrammaticOverlaySize = null;
        _overlayResizeDebounceTimer.Stop();
        Opacity = 1;
        Toolbar.IsVisible = false;
        RootGrid.RowDefinitions[0].Height = new GridLength(0);
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        // The normal launcher has a much larger minimum size. In overlay
        // editing mode keep the widget's native resize range independent of
        // that launcher constraint.
        MinWidth = 120;
        MinHeight = 80;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        Browser.Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var layout = OverlayPresentationService.NormalizeLayout(_model.Settings.OverlayLayoutMode);
        var scale = Math.Clamp(_model.Settings.OverlayScalePercent, 50, 180) / 100d;
        var width = (layout == "horizontal" ? 920 : layout is "companella" or "companella-replay" ? 760 : 475) * scale;
        var height = (layout == "horizontal" ? 360 : layout is "companella" or "companella-replay" ? 340 : 540) * scale;
        ClientSize = new Size(width, height);
        var working = Screens.ScreenFromWindow(this)?.WorkingArea ?? Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var savedVisible = _model.Settings.OverlayX > -30000 && _model.Settings.OverlayY > -30000;
        Position = savedVisible
            ? new PixelPoint(_model.Settings.OverlayX, _model.Settings.OverlayY)
            : new PixelPoint(working.Right - (int)Math.Ceiling(width * RenderScaling) - 18, working.Y + 18);
        _windowsOverlay.Enter();
        Navigate(AnalysisUrl);
        StartOverlayGameplayPolling();
    }

    private void LeaveOverlayMode()
    {
        if (!_overlayMode || _model is null)
            return;
        SaveOverlayBounds();
        _overlayInteractive = false;
        StopOverlayGameplayPolling();
        _overlayResizeDebounceTimer.Stop();
        _overlayResizeScaleUpdatePending = false;
        _overlayNativeResizePending = false;
        _overlayExpectedWidgetPhysicalWidth = null;
        _overlayResizeGuardUntilUtc = default;
        _ignoredProgrammaticOverlaySize = null;
        _windowsOverlay.Leave();
        _overlayMode = false;
        _overlayWidgetSized = false;
        _overlayPlayStateKnown = false;
        _overlayNativePlayStateKnown = false;
        _lastGameplayTraceBySource.Clear();
        _overlayIsPlaying = false;
        _overlayIsPaused = null;
        _overlaySuppressedByPolicy = false;
        _overlayVisibilityPolicy = OverlayVisibilityPolicy.Always;
        Opacity = 1;
        Toolbar.IsVisible = true;
        RootGrid.RowDefinitions[0].Height = new GridLength(150);
        SystemDecorations = SystemDecorations.Full;
        CanResize = true;
        Topmost = false;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.Parse("#0E1016"));
        Browser.Background = new SolidColorBrush(Color.Parse("#0E1016"));
        MinWidth = 650;
        MinHeight = 740;
        Position = _normalPosition;
        ClientSize = _normalClientSize;
        SetOverlayWindowVisibility(true);
        Navigate(AnalysisUrl);
        Activate();
    }

    private void ResizeOverlayToWidget(int physicalWidth, int physicalHeight)
    {
        if (!_overlayMode || physicalWidth is < 120 or > 2400 || physicalHeight is < 80 or > 3200)
            return;
        if (_overlayInteractive)
        {
            if (_overlayExpectedWidgetPhysicalWidth is int expectedWidth)
            {
                var matchesExpectedWidth = IsCloseToPhysicalWidth(physicalWidth, expectedWidth);
                if (!matchesExpectedWidth &&
                    (_overlayNativeResizePending || _overlayResizeScaleUpdateRunning ||
                     DateTime.UtcNow < _overlayResizeGuardUntilUtc))
                    return;
                if (!matchesExpectedWidth || DateTime.UtcNow >= _overlayResizeGuardUntilUtc)
                {
                    _overlayExpectedWidgetPhysicalWidth = null;
                    _overlayResizeGuardUntilUtc = default;
                }
            }
            else if (_overlayNativeResizePending || _overlayResizeDebounceTimer.IsEnabled)
            {
                // The browser reports its old fixed-size card while a native
                // resize is still being dragged. Let the debounced scale
                // update establish the new content size first.
                return;
            }
        }
        var position = Position;
        var targetSize = new Size(physicalWidth / RenderScaling, physicalHeight / RenderScaling);
        var sizeChanged = !IsCloseToSize(ClientSize, targetSize);
        if (sizeChanged)
            _ignoredProgrammaticOverlaySize = targetSize;
        else
            _ignoredProgrammaticOverlaySize = null;
        _suppressOverlayResizeFeedback = true;
        try
        {
            ClientSize = targetSize;
            Position = position;
        }
        finally
        {
            _suppressOverlayResizeFeedback = false;
        }
        _overlayWidgetSized = true;
        UpdateOverlayVisibility();
        SaveOverlayBounds();
    }

    private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_overlayMode)
            return;
        if (!_overlayInteractive || _suppressOverlayResizeFeedback)
            return;
        if (_ignoredProgrammaticOverlaySize is Size programmaticSize && IsCloseToSize(ClientSize, programmaticSize))
        {
            _ignoredProgrammaticOverlaySize = null;
            return;
        }
        _ignoredProgrammaticOverlaySize = null;
        _overlayNativeResizePending = true;
        _overlayExpectedWidgetPhysicalWidth = null;
        QueueOverlayScaleUpdate();
    }

    private void QueueOverlayScaleUpdate()
    {
        if (!_overlayMode || !_overlayInteractive || _suppressOverlayResizeFeedback)
            return;
        _overlayResizeDebounceTimer.Stop();
        _overlayResizeDebounceTimer.Start();
    }

    private async void OverlayResizeDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _overlayResizeDebounceTimer.Stop();
        if (_overlayResizeScaleUpdateRunning)
        {
            _overlayResizeScaleUpdatePending = true;
            return;
        }

        _overlayResizeScaleUpdateRunning = true;
        try
        {
            await ApplyOverlayScaleFromWindowAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Applying overlay scale from window", exception);
        }
        finally
        {
            _overlayResizeScaleUpdateRunning = false;
            if (_overlayResizeScaleUpdatePending)
            {
                _overlayResizeScaleUpdatePending = false;
                QueueOverlayScaleUpdate();
            }
        }
    }

    private async Task ApplyOverlayScaleFromWindowAsync()
    {
        if (!_overlayMode || !_overlayInteractive || _suppressOverlayResizeFeedback || _model is null)
            return;
        var baseWidth = GetOverlayBaseWidth(_model.Settings.OverlayLayoutMode);
        if (baseWidth <= 0 || ClientSize.Width <= 0)
            return;

        var next = Math.Clamp((int)Math.Round(ClientSize.Width / baseWidth * 100d), 50, 180);
        if (next == _model.Settings.OverlayScalePercent)
        {
            _overlayNativeResizePending = false;
            _overlayExpectedWidgetPhysicalWidth = null;
            _overlayResizeGuardUntilUtc = default;
            await RequestOverlayWidgetSizeReportAsync();
            return;
        }
        _overlayExpectedWidgetPhysicalWidth = (int)Math.Round(baseWidth * next / 100d * RenderScaling);
        _overlayResizeGuardUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
        _model.Settings.OverlayScalePercent = next;
        _model.SaveSettings();
        try
        {
            await ApplyPresentationAsync();
            await RequestOverlayWidgetSizeReportAsync();
        }
        finally
        {
            _overlayNativeResizePending = false;
        }
    }

    private static double GetOverlayBaseWidth(string? layout) =>
        OverlayPresentationService.NormalizeLayout(layout) switch
        {
            "horizontal" => 920,
            "companella" => 760,
            "companella-replay" => 760,
            _ => 475
        };

    private static bool IsCloseToPhysicalWidth(int actual, int expected) => Math.Abs(actual - expected) <= 3;

    private static bool IsCloseToSize(Size actual, Size expected) =>
        Math.Abs(actual.Width - expected.Width) <= 1.5 && Math.Abs(actual.Height - expected.Height) <= 1.5;

    private void SetOverlaySuppressedByPlay(bool isPlaying, bool? isPaused)
    {
        var visibilityPolicy = _overlayVisibilityPolicy;
        var shouldShow = OverlayVisibilityPolicy.ShouldShow(visibilityPolicy, isPlaying, isPaused);
        var suppressed = !shouldShow;
        var stateChanged = !_overlayPlayStateKnown ||
                           _overlayIsPlaying != isPlaying ||
                           _overlayIsPaused != isPaused ||
                           _overlaySuppressedByPolicy != suppressed;
        _overlayPlayStateKnown = true;
        _overlayIsPlaying = isPlaying;
        _overlayIsPaused = isPaused;
        _overlaySuppressedByPolicy = suppressed;
        UpdateOverlayVisibility();
        if (stateChanged)
            LogOverlayGameplayState(visibilityPolicy, isPlaying, isPaused);
    }

    private void StartOverlayGameplayPolling()
    {
        StopOverlayGameplayPolling();
        if (_model is null)
            return;

        _overlayGameplayPollCancellation = new CancellationTokenSource();
        _overlayGameplayPollTimer.Start();
        _ = PollOverlayGameplayStateAsync();
    }

    private void StopOverlayGameplayPolling()
    {
        _overlayGameplayPollTimer.Stop();
        _overlayGameplayPollCancellation?.Cancel();
        _overlayGameplayPollCancellation?.Dispose();
        _overlayGameplayPollCancellation = null;
    }

    private async void OverlayGameplayPollTimer_Tick(object? sender, EventArgs e) =>
        await PollOverlayGameplayStateAsync();

    private async Task PollOverlayGameplayStateAsync()
    {
        if (!_overlayMode || _model is null || Interlocked.Exchange(ref _overlayGameplayPollInFlight, 1) != 0)
            return;

        var cancellationToken = _overlayGameplayPollCancellation?.Token ?? CancellationToken.None;
        try
        {
            var state = await _model.Tosu.GetGameplayStateAsync(cancellationToken);
            if (state is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_overlayMode)
                    {
                        _overlayNativePlayStateKnown = true;
                        if (state.IsPlaying is bool isPlaying)
                            SetOverlaySuppressedByPlay(isPlaying, state.IsPaused);
                        TraceGameplayState("native-http", state.Name, state.Number, state.IsPlaying, state.IsPaused, null);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving overlay mode cancels the in-flight request.
        }
        catch (Exception exception)
        {
            AppLogger.Error("Polling tosu gameplay state", exception, userVisible: false);
        }
        finally
        {
            Interlocked.Exchange(ref _overlayGameplayPollInFlight, 0);
        }
    }

    private void UpdateOverlayVisibility()
    {
        if (!_overlayMode)
            return;
        // A size report is an optimization for synchronizing the native
        // window bounds, not a prerequisite for visibility. If WebView has
        // not reported its first measurement yet, the saved/default client
        // size is still a valid widget surface and must be shown in menu.
        var visible = _overlayPlayStateKnown && !_overlaySuppressedByPolicy;
        SetOverlayWindowVisibility(visible);
    }

    private void SetOverlayWindowVisibility(bool visible)
    {
        if (_overlayWindowVisible == visible)
            return;

        try
        {
            if (visible)
                Opacity = 1;

            if (OperatingSystem.IsWindows())
            {
                _windowsOverlay.SetWindowVisible(visible);
            }
            else if (visible)
            {
                Show();
            }
            else
            {
                Hide();
            }

            if (!visible)
                Opacity = 0;
            _overlayWindowVisible = visible;
        }
        catch (Exception exception)
        {
            AppLogger.Error(
                visible ? "Showing overlay window" : "Hiding overlay window",
                exception);
        }
    }

    private void LogOverlayGameplayState(string visibilityPolicy, bool isPlaying, bool? isPaused)
    {
        var nativeVisible = OperatingSystem.IsWindows()
            ? _windowsOverlay.IsWindowShown
            : IsVisible;
        AppLogger.Info(
            "Overlay gameplay state",
            $"visibilityPolicy={visibilityPolicy}; " +
            $"isPlaying={isPlaying}; paused={isPaused?.ToString() ?? "null"}; " +
            $"requestedVisible={_overlayWindowVisible}; " +
            $"nativeVisible={nativeVisible}; opacity={Opacity:0.##}");
    }

    private string ResolveOverlayVisibilityPolicy()
    {
        if (_model is null)
            return OverlayVisibilityPolicy.Always;

        var requestedPreset = string.IsNullOrWhiteSpace(_model.Settings.OverlayPresetId) ||
                              (_model.Settings.OverlayPresetId == "default" && _model.Settings.OverlayLayoutMode != "default")
            ? _model.Settings.OverlayLayoutMode
            : _model.Settings.OverlayPresetId;
        return OverlayVisibilityPolicy.Normalize(_presetCatalog.Get(requestedPreset).VisibilityPolicy);
    }

    private void SaveOverlayBounds()
    {
        if (!_overlayMode || _model is null)
            return;
        _model.Settings.OverlayX = Position.X;
        _model.Settings.OverlayY = Position.Y;
        _model.Settings.OverlayWidth = (int)Math.Ceiling(ClientSize.Width * RenderScaling);
        _model.Settings.OverlayHeight = (int)Math.Ceiling(ClientSize.Height * RenderScaling);
        _model.SaveSettings();
    }

    private async void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        if (_model is null || !_fullscreen.IsSupported || !ActiveAnalyzer.Descriptor.SupportsFullscreen)
            return;
        var enable = !_fullscreen.ReadEnabled(_model.Settings.FullscreenOverlayEnabled);
        var confirmed = await ConfirmAsync(L("dialog.fullscreen.title"),
            enable
                ? L("dialog.fullscreen.enable")
                : L("dialog.fullscreen.disable"));
        if (!confirmed)
            return;
        try
        {
            _fullscreen.SetEnabled(enable);
            _model.Settings.FullscreenOverlayEnabled = enable;
            if (enable)
            {
                _model.Settings.FullscreenOverlayStyleVersion = 1;
                _fullscreen.EnsureProfile(_model.Settings, ActiveAnalyzer.Descriptor, true);
            }
            _model.SaveSettings();
            UpdateFullscreenButton();
            await _model.RestartAsync();
            if (enable)
            {
                Navigate(FullscreenEditorUrl);
                await InfoAsync(L("dialog.fullscreen.enabled"),
                    UiText.Format("dialog.fullscreen.enabled_message", ActiveAnalyzer.Descriptor.Name));
            }
            else
                Navigate(AnalysisUrl);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Configuring fullscreen overlay", exception);
            await InfoAsync(L("dialog.configuration_error"), exception.Message);
        }
    }

    private void UpdateFullscreenButton()
    {
        var enabled = _model?.Settings.FullscreenOverlayEnabled == true;
        FullscreenButton.Content = enabled ? L("button.fullscreen_on") : L("button.fullscreen_off");
        FullscreenButton.Background = new SolidColorBrush(Color.Parse(enabled ? "#2A7E5B" : "#59432A"));
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new MessageDialog(title, message, L("button.yes"), L("button.no"));
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task InfoAsync(string title, string message)
    {
        var dialog = new MessageDialog(title, message, L("button.ok"));
        await dialog.ShowDialog<bool>(this);
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();
}
