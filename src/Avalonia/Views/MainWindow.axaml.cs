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
using Avalonia.Threading;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Avalonia.Platform;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Avalonia.ViewModels;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Views;

public partial class MainWindow : Window
{
    private const string BaseUrl = "http://127.0.0.1:24050";
    private const string FullscreenEditorUrl = BaseUrl + "/api/ingame?edit=true";
    private static readonly Uri TosuBaseUri = new(BaseUrl);

    private readonly OverlayPresetCatalog presetCatalog = new();
    private readonly AnalyzerAdapterCatalog analyzerCatalog = new();
    private readonly OverlayPresentationService presentation;
    private readonly FullscreenOverlayService fullscreen = new();
    private readonly UpdateService updates = new();
    private readonly WindowsOverlayController windowsOverlay;
    private readonly DispatcherTimer overlayResizeDebounceTimer;
    private readonly DispatcherTimer overlayGameplayPollTimer;
    private readonly DispatcherTimer headlessBeatmapPollTimer;
    private readonly SemaphoreSlim presentationGate = new(1, 1);
    private readonly AnalyzerEngineCatalog analyzerEngineCatalog = new();
    private readonly AnalyzerEnginePackageDeployer analyzerEngineDeployer = new();
    private readonly EffectiveAnalysisConfigurationStore effectiveAnalysisStore = new();
    private EffectiveAnalysisConfiguration effectiveAnalysisConfiguration = EffectiveAnalysisConfigurationStore.CreateDefault();
    private MainViewModel? model;
    private CancellationTokenSource? previewPresentationCancellation;
    private CancellationTokenSource? overlayGameplayPollCancellation;
    private AnalyzerCoordinator? analyzerCoordinator;
    private WebViewAnalyzerScriptHost? analyzerScriptHost;
    private AnalyzerEngineSupervisor? analyzerSupervisor;
    private TosuBeatmapSource? tosuBeatmapSource;
    private HttpClient? tosuBeatmapHttpClient;
    private CancellationTokenSource? headlessBeatmapPollCancellation;
    private int headlessBeatmapPollInFlight;
    private string? lastHeadlessBeatmapKey;
    private AnalyzerEngineSupervisorState? lastSupervisorState;
    private DateTime lastOsuNotRunningLogUtc = DateTime.MinValue;
    private WidgetAnalysisRunner? headlessWidgetRunner;
    private WidgetAnalysisSceneRunner? headlessSceneRunner;
    private AnalysisRunScope? headlessSceneScope;
    private string? lastHeadlessSceneKey;
    private bool initialized;
    private bool overlayMode;
    private bool overlayWidgetSized;
    private bool overlayPlayStateKnown;
    private bool overlayNativePlayStateKnown;
    private bool overlayIsPlaying;
    private bool? overlayIsPaused;
    private bool overlaySuppressedByPolicy;
    private string overlayVisibilityPolicy = OverlayVisibilityPolicy.Always;
    private bool overlayInteractive;
    private bool suppressOverlayResizeFeedback;
    private bool overlayResizeScaleUpdateRunning;
    private bool overlayResizeScaleUpdatePending;
    private bool overlayNativeResizePending;
    private int overlayGameplayPollInFlight;
    private bool componentPreparationFailed;
    private bool updatingLanguageSelector;
    private readonly Dictionary<string, string> lastGameplayTraceBySource = new(StringComparer.OrdinalIgnoreCase);
    private int? overlayExpectedWidgetPhysicalWidth;
    private DateTime overlayResizeGuardUntilUtc;
    private Size? ignoredProgrammaticOverlaySize;
    private bool showingLoggedError;
    private bool overlayWindowVisible = true;
    private PixelPoint normalPosition;
    private Size normalClientSize;

    public MainWindow()
    {
        AppLogger.ErrorRaised += AppLogger_ErrorRaised;
        InitializeComponent();
        presentation = new OverlayPresentationService(presetCatalog, analyzerCatalog);
        windowsOverlay = new WindowsOverlayController(this);
        windowsOverlay.ExitRequested += (_, _) => LeaveOverlayMode();
        windowsOverlay.ClickThroughChanged += enabled => Browser.IsHitTestVisible = !enabled;
        windowsOverlay.InteractionChanged += interactive =>
        {
            overlayInteractive = interactive;
            if (overlayMode)
                CanResize = interactive;
            UpdateOverlayVisibility();
        };
        windowsOverlay.OsuProcessChanged += running =>
        {
            if (running || !overlayMode)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                ReturnToLauncherAfterGameExit(
                    "status.osu_closed");
            });
        };
        overlayResizeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        overlayResizeDebounceTimer.Tick += OverlayResizeDebounceTimer_Tick;
        overlayGameplayPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        overlayGameplayPollTimer.Tick += OverlayGameplayPollTimer_Tick;
        headlessBeatmapPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        headlessBeatmapPollTimer.Tick += HeadlessBeatmapPollTimer_Tick;
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
        overlayResizeDebounceTimer.Stop();
        headlessBeatmapPollTimer.Stop();
        StopOverlayGameplayPolling();
        StopHeadlessBeatmapPolling();
        previewPresentationCancellation?.Cancel();
        previewPresentationCancellation?.Dispose();
        windowsOverlay.Dispose();
        updates.Dispose();
        tosuBeatmapHttpClient?.Dispose();
        if (analyzerCoordinator is not null)
        {
            analyzerCoordinator.SnapshotChanged -= AnalyzerSnapshotChanged;
        }

        if (analyzerSupervisor is not null)
        {
            analyzerSupervisor.StateChanged -= AnalyzerSupervisor_StateChanged;
            _ = analyzerSupervisor.DisposeAsync().AsTask();
        }

        DisposeHeadlessRunners();
        analyzerScriptHost?.DisposeAsync().AsTask().ConfigureAwait(false);
        model?.Dispose();
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
            model?.SetStatus(status);
            if (!entry.UserVisible || !initialized || overlayMode || showingLoggedError)
                return;

            showingLoggedError = true;
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
                showingLoggedError = false;
            }
        });
    }

    private async Task InitializeAsync()
    {
        if (initialized)
            return;
        initialized = true;
        model = DataContext as MainViewModel ?? throw new InvalidOperationException("Main view model is unavailable.");
        analyzerCoordinator = new AnalyzerCoordinator(
            analyzerCatalog.List().Select(package => package.Adapter),
            model.Settings.AnalyzerProviderId);
        analyzerCoordinator.SnapshotChanged += AnalyzerSnapshotChanged;
        ManiaMapAnalyzerOverlay.UiText.Initialize(model.Settings.Language);
        InitializeLanguageSelector();
        ApplyLanguage();
        if (UiText.LoadError is not null)
        {
            model.SetStatus(L("dialog.language_resource_error"));
            ShowMessagePage(L("dialog.error.title"), L("dialog.language_resource_error"), true);
        }
        CustomCssService.EnsureExists();
        model.Tosu.StateChanged += Tosu_StateChanged;
        windowsOverlay.RegisterHotkeys();
        SetControlsEnabled(false);
        ShowMessagePage(L("dialog.prepare.title"), L("dialog.prepare.message"), false);

        if (!await CheckUpdatesAsync())
            return;
        SynchronizeFullscreenState();
        await model.StartAsync();
        if (model.Tosu.IsRunning)
        {
            SetComponentPreparationState(false);
            model.SetStatus(L("status.tosu_running"), true);
            SetControlsEnabled(true);
            Navigate(AnalysisUrl);
        }
        else
        {
            SetComponentPreparationState(true);
            SetControlsEnabled(false, keepRestart: true);
            ShowMessagePage(L("status.tosu_not_running"), model.Status, true);
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
                if (analyzerSupervisor is not null)
                {
                    _ = analyzerSupervisor.NotifyTosuRestartAsync();
                }
            }
            else if (initialized && overlayMode)
            {
                ReturnToLauncherAfterGameExit(
                    "status.osu_stopped");
            }
            else if (initialized)
            {
                SetControlsEnabled(false, keepRestart: true);
            }
        });
    }

    private void ReturnToLauncherAfterGameExit(string statusKey)
    {
        if (!overlayMode)
            return;
        try
        {
            LeaveOverlayMode();
            model?.SetStatus(L(statusKey));
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
            if (analyzerSupervisor is not null)
            {
                analyzerSupervisor.StateChanged -= AnalyzerSupervisor_StateChanged;
                await analyzerSupervisor.DisposeAsync();
                analyzerSupervisor = null;
            }

            analyzerScriptHost?.DisposeAsync().AsTask().ConfigureAwait(false);
            analyzerScriptHost = new WebViewAnalyzerScriptHost(Browser);
            analyzerSupervisor = new AnalyzerEngineSupervisor(
                analyzerEngineCatalog,
                analyzerEngineDeployer,
                analyzerScriptHost);
            analyzerSupervisor.StateChanged += AnalyzerSupervisor_StateChanged;

            tosuBeatmapHttpClient?.Dispose();
            tosuBeatmapHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            tosuBeatmapSource = new TosuBeatmapSource(tosuBeatmapHttpClient, TosuBaseUri);
            effectiveAnalysisConfiguration = effectiveAnalysisStore.Load();
            AppLogger.Info(
                "Effective analysis configuration",
                $"Loaded effective configuration: engine={effectiveAnalysisConfiguration.DefaultEngineId} algorithm={effectiveAnalysisConfiguration.DefaultAlgorithm} widgets={effectiveAnalysisConfiguration.Widgets.Length}");

            // Ensure the WebView has finished loading the analysis page before
            // bootstrapping the headless runtime. Injecting the runtime too early
            // makes globalThis.location.href point at the previous document and
            // the subsequent navigation resets the bridge, producing engine.runtime_reset.
            await WaitForAnalysisWebViewReadyAsync();

            var preferredEngineId = string.IsNullOrWhiteSpace(effectiveAnalysisConfiguration.DefaultEngineId)
                ? analyzerEngineCatalog.Available().FirstOrDefault()?.Id
                : effectiveAnalysisConfiguration.DefaultEngineId;
            var state = await analyzerSupervisor.StartAsync(preferredEngineId);
            lastSupervisorState = state;
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
        var coordinator = analyzerSupervisor?.Coordinator;
        if (coordinator is null)
        {
            AppLogger.Warning("Headless runners", "Cannot initialize widget runners: coordinator is not ready.");
            return;
        }

        try
        {
            headlessWidgetRunner = new WidgetAnalysisRunner(coordinator);
            headlessSceneRunner = new WidgetAnalysisSceneRunner(coordinator);
            headlessSceneScope = new AnalysisRunScope("headless-scene");
            headlessWidgetRunner.SnapshotComposed += HeadlessWidgetSnapshotComposed;
            headlessSceneRunner.SnapshotComposed += HeadlessSceneSnapshotComposed;
            AppLogger.Info("Headless runners", $"Initialized widget runners for {effectiveAnalysisConfiguration.Widgets.Length} widget(s).");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Initializing headless runners", exception);
        }
    }

    private void DisposeHeadlessRunners()
    {
        if (headlessWidgetRunner is not null)
        {
            headlessWidgetRunner.SnapshotComposed -= HeadlessWidgetSnapshotComposed;
            headlessWidgetRunner.Dispose();
            headlessWidgetRunner = null;
        }

        if (headlessSceneRunner is not null)
        {
            headlessSceneRunner.SnapshotComposed -= HeadlessSceneSnapshotComposed;
            headlessSceneRunner.Dispose();
            headlessSceneRunner = null;
        }

        headlessSceneScope?.Dispose();
        headlessSceneScope = null;
        lastHeadlessSceneKey = null;
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
        var descriptor = analyzerSupervisor?.ActiveDescriptor;
        if (descriptor is null)
        {
            AppLogger.Warning("Headless composition", "Cannot build scene spec: no active analyzer descriptor.");
            return null;
        }

        var widgets = new List<WidgetAnalysisSpec>();
        foreach (var effectiveWidget in effectiveAnalysisConfiguration.Widgets)
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
            lastSupervisorState = state;
            UpdateHeadlessStatusUi(state);
            if (state.IsReady)
            {
                if (headlessWidgetRunner is null || headlessSceneRunner is null)
                {
                    InitializeHeadlessRunners();
                }

                if (headlessBeatmapPollTimer.IsEnabled == false)
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
        if (model is null)
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
            model.SetStatus(statusMessage);
        }
    }

    private void StartHeadlessBeatmapPolling()
    {
        if (tosuBeatmapSource is null)
        {
            return;
        }

        StopHeadlessBeatmapPolling();
        headlessBeatmapPollCancellation = new CancellationTokenSource();
        headlessBeatmapPollTimer.Start();
        _ = PollHeadlessBeatmapAsync();
    }

    private void StopHeadlessBeatmapPolling()
    {
        headlessBeatmapPollTimer.Stop();
        headlessBeatmapPollCancellation?.Cancel();
        headlessBeatmapPollCancellation?.Dispose();
        headlessBeatmapPollCancellation = null;
    }

    private async void HeadlessBeatmapPollTimer_Tick(object? sender, EventArgs e) =>
        await PollHeadlessBeatmapAsync();

    private async Task PollHeadlessBeatmapAsync()
    {
        if (tosuBeatmapSource is null || analyzerSupervisor is null || Interlocked.Exchange(ref headlessBeatmapPollInFlight, 1) != 0)
        {
            return;
        }

        var cancellationToken = headlessBeatmapPollCancellation?.Token ?? CancellationToken.None;

        try
        {
            TosuBeatmapSnapshot snapshot;
            try
            {
                snapshot = await tosuBeatmapSource.GetCurrentAsync(cancellationToken);
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
                    if (now - lastOsuNotRunningLogUtc > TimeSpan.FromSeconds(5))
                    {
                        lastOsuNotRunningLogUtc = now;
                        AppLogger.Info("Headless beatmap poll", "osu! client is not running — headless beatmap fetch skipped (tosu HTTP 500).");
                    }

                    lastHeadlessBeatmapKey = null;
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Show a friendly non-error status instead of an exception dialog.
                        // Keep the legacy DOM adapter as fallback while the game is closed.
                        if (model is not null && !string.Equals(model.Status, L("status.headless_osu_not_running"), StringComparison.Ordinal))
                        {
                            model.SetStatus(L("status.headless_osu_not_running"));
                        }
                    });

                    return;
                }

                if (IsNoBeatmapBeatmapException(exception))
                {
                    var now = DateTime.UtcNow;
                    if (now - lastOsuNotRunningLogUtc > TimeSpan.FromSeconds(5))
                    {
                        lastOsuNotRunningLogUtc = now;
                        AppLogger.Info("Headless beatmap poll", "No current beatmap is available — osu! is running but no map is selected.");
                    }

                    // Do not treat as error: keep polling and do not overwrite a useful status
                    // with a stale map. Clearing the key forces a re-analysis once a map appears.
                    lastHeadlessBeatmapKey = null;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (model is not null && string.IsNullOrWhiteSpace(model.Status))
                        {
                            model.SetStatus(L("status.headless_no_beatmap"));
                        }
                    });

                    return;
                }

                AppLogger.Warning("Headless beatmap poll", exception.Message, exception);
                return;
            }

            var key = snapshot.Identity.StableKey + "|" + snapshot.Rate.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(",", snapshot.Mods) + "|" + snapshot.RawBeatmap.Length;
            var effectiveKey = effectiveAnalysisConfiguration.ConfigurationVersion + "|" + effectiveAnalysisConfiguration.DefaultEngineId + "|" + effectiveAnalysisConfiguration.DefaultAlgorithm + "|" + effectiveAnalysisConfiguration.Widgets.Length;
            var combinedKey = key + "|" + effectiveKey;
            var sceneKey = snapshot.Identity.StableKey + "|" + snapshot.Rate.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(",", snapshot.Mods) + "|" + effectiveKey;
            if (string.Equals(combinedKey, lastHeadlessBeatmapKey, StringComparison.Ordinal) &&
                string.Equals(sceneKey, lastHeadlessSceneKey, StringComparison.Ordinal))
            {
                return;
            }

            // Scene generation is invalidated when map/rate/mods or effective config changes.
            var isNewSceneGeneration = !string.Equals(sceneKey, lastHeadlessSceneKey, StringComparison.Ordinal);
            if (isNewSceneGeneration)
            {
                AppLogger.Info("Headless scene", $"Effective scene generation invalidated: newKey={sceneKey}");
            }

            lastHeadlessBeatmapKey = combinedKey;
            lastHeadlessSceneKey = sceneKey;
            AppLogger.Info(
                "Headless beatmap poll",
                $"New beatmap {snapshot.Identity.StableKey} title={snapshot.Metadata.Title} version={snapshot.Metadata.Version} rate={snapshot.Rate} mods=[{string.Join(",", snapshot.Mods)}] effective={effectiveKey}");

            // Prefer composed widget/scene execution so shared analyzer results are de-duplicated
            // and rate/mods are execution dimensions per source, not per beatmap generation.
            if (headlessSceneRunner is not null && headlessWidgetRunner is not null)
            {
                var sceneSpec = BuildHeadlessSceneSpec(snapshot);
                if (sceneSpec is not null)
                {
                    try
                    {
                        var sceneSnapshot = await headlessSceneRunner.RunAsync(sceneSpec, cancellationToken);
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
                                    model?.SetStatus(L("status.headless_partial") + $" {first.Metrics.Values.FirstOrDefault()?.Provenance.ActualAlgorithm ?? snapshot.Metadata.Version} partial" + diag);
                                }
                                else if (first.Outcome == AnalysisOutcome.Success)
                                {
                                    var star = first.Metrics.TryGetValue("difficulty.star", out var metric) ? metric.Metric.Value.ToString() ?? "n/a" : "n/a";
                                    var algo = first.Metrics.Values.FirstOrDefault()?.Provenance.ActualAlgorithm ?? snapshot.Metadata.Version;
                                    model?.SetStatus(L("status.headless_success") + $" {snapshot.Metadata.Title} [{snapshot.Metadata.Version}] {algo} star={star}");
                                }
                                else if (first.Outcome == AnalysisOutcome.Failed)
                                {
                                    var diag = first.Diagnostics.Length == 0 ? string.Empty : $" {string.Join("; ", first.Diagnostics.Take(2).Select(diagnostic => diagnostic.Code))}";
                                    model?.SetStatus(L("status.headless_failed") + diag + " (DOM fallback)");
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

            var result = await analyzerSupervisor.AnalyzeAsync(snapshot, cancellationToken: cancellationToken);
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
                    model?.SetStatus(L("status.headless_partial") + $" {result.ActualAlgorithm ?? snapshot.Metadata.Version} partial" + diagnosticsSummary);
                }
                else if (result.Outcome == AnalysisOutcome.Success)
                {
                    model?.SetStatus(L("status.headless_success") + $" {snapshot.Metadata.Title} [{snapshot.Metadata.Version}] {result.ActualAlgorithm} star={result.Metrics.GetValueOrDefault("difficulty.star")?.Value.ToString() ?? "n/a"}");
                }
                else if (result.Outcome == AnalysisOutcome.Failed)
                {
                    model?.SetStatus(L("status.headless_failed") + diagnosticsSummary + " (DOM fallback)");
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
            Interlocked.Exchange(ref headlessBeatmapPollInFlight, 0);
        }
    }

    private async Task PushHeadlessSnapshotAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var script = $"window.dispatchEvent(new CustomEvent('analysis:snapshot', {{detail: {json}}})); if (typeof window.__overlayRenderAnalysisSnapshot === 'function') window.__overlayRenderAnalysisSnapshot({json});";
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
        if (model is null)
            return false;
        model.SetStatus(L("status.checking_updates"));
        try
        {
            var progress = new Progress<UpdateProgress>(update =>
                model.SetStatus(LocalizeUpdateMessage(update.Message)));
            var result = await updates.CheckComponentsAsync(progress: progress);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? L("status.update_failed"));
            if (result.LauncherUpdateAvailable)
            {
                var accept = await ConfirmAsync(L("dialog.update_available.title"),
                    UiText.Format("dialog.update_available.message", result.LatestLauncherVersion));
                if (accept && updates.StartSelfUpdate())
                {
                    Close();
                    return false;
                }
            }
            if (result.UpdatedTosu || result.UpdatedAddon)
                model.SetStatus(L("status.components_updated"));
            else if (string.Equals(result.Compatibility, "unsupported", StringComparison.OrdinalIgnoreCase))
                await InfoAsync(L("dialog.compatibility.title"), UiText.Format("dialog.compatibility.message", result.LazerVersion));
            if (!string.IsNullOrWhiteSpace(result.Warning))
                model.SetStatus(LocalizeResourceOrText(result.Warning));

            SetComponentPreparationState(false);
            return true;
        }
        catch (Exception exception)
        {
            SetComponentPreparationState(true);
            var title = L("dialog.components_error.title");
            var retry = L("dialog.components_error.message");
            var details = exception.Message.Trim();
            model.SetStatus(title);
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
        componentPreparationFailed = failed;
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
        if (model is null)
            return;
        var enabled = fullscreen.ReadEnabled(model.Settings.FullscreenOverlayEnabled);
        if (enabled && !ActiveAnalyzer.Descriptor.SupportsFullscreen)
        {
            if (fullscreen.IsSupported)
                fullscreen.SetEnabled(false);
            enabled = false;
        }
        model.Settings.FullscreenOverlayEnabled = enabled;
        if (enabled)
        {
            fullscreen.EnsureProfile(
                model.Settings,
                ActiveAnalyzer.Descriptor,
                model.Settings.FullscreenOverlayStyleVersion < 1);
            model.Settings.FullscreenOverlayStyleVersion = 1;
        }
        model.SaveSettings();
        UpdateFullscreenButton();
    }

    private void ApplyLanguage()
    {
        if (model is null)
            return;
        Title = L("window.title");
        BrandText.Text = L("app.brand");
        AnalysisButton.Content = L("button.map_analysis");
        AppearanceButton.Content = L("button.appearance");
        MappingButton.Content = L("button.mapping");
        HelpButton.Content = L("button.help");
        OverlayButton.Content = L("button.overlay");
        DashboardButton.Content = L("button.tosu_panel");
        SetComponentPreparationState(componentPreparationFailed);
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
        updatingLanguageSelector = true;
        try
        {
            LanguageSelector.ItemsSource = UiText.Languages;
            RefreshLanguageSelector();
        }
        finally
        {
            updatingLanguageSelector = false;
        }
    }

    private void RefreshLanguageSelector()
    {
        if (LanguageSelector is null)
            return;
        updatingLanguageSelector = true;
        try
        {
            LanguageSelector.SelectedItem = UiText.Languages.FirstOrDefault(language =>
                string.Equals(language.Id, UiText.CurrentLanguage, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            updatingLanguageSelector = false;
        }
    }

    private string L(string key) => ManiaMapAnalyzerOverlay.UiText.Get(key);

    private AnalyzerAdapterPackage ActiveAnalyzer =>
        presentation.ResolveAnalyzer(model?.Settings.AnalyzerProviderId);

    private string AnalysisUrl => ActiveAnalyzer.GetAnalysisUri(TosuBaseUri).ToString();

    private void SetControlsEnabled(bool enabled, bool keepRestart = false)
    {
        AnalysisButton.IsEnabled = enabled;
        AppearanceButton.IsEnabled = enabled;
        MappingButton.IsEnabled = enabled;
        HelpButton.IsEnabled = enabled;
        PreviewScaleDownButton.IsEnabled = enabled;
        PreviewScaleUpButton.IsEnabled = enabled;
        OverlayButton.IsEnabled = enabled;
        FullscreenButton.IsEnabled = enabled && fullscreen.IsSupported && ActiveAnalyzer.Descriptor.SupportsFullscreen;
        DashboardButton.IsEnabled = enabled;
        RestartButton.IsEnabled = enabled || keepRestart;
    }

    private void UpdatePreviewScaleText()
    {
        if (PreviewScaleText is not null && model is not null)
            PreviewScaleText.Content = model.Settings.OverlayScalePercent + "%";
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
        var loadingCss = (presetCatalog.ReadRuntimeAsset("loading.css") ?? string.Empty)
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

            if (analyzerSupervisor is not null)
            {
                var status = analyzerSupervisor.CurrentState.Status;
                if (status == AnalyzerEngineSupervisorStatus.Ready ||
                    status == AnalyzerEngineSupervisorStatus.Fallback ||
                    status == AnalyzerEngineSupervisorStatus.Error)
                {
                    await analyzerSupervisor.NotifyNavigationAsync();
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
            if (analyzerSupervisor is not null && !analyzerSupervisor.IsReady)
            {
                AppLogger.Info("Analyzer engine bridge", $"Received bridge message while supervisor state={analyzerSupervisor.CurrentState.Status}.");
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
        if (!overlayMode)
            return;
        if (message == "overlay:drag")
        {
            windowsOverlay.BeginDrag();
            return;
        }
        const string resizePrefix = "overlay:resize:";
        if (message.StartsWith(resizePrefix, StringComparison.Ordinal))
        {
            windowsOverlay.BeginResize(message[resizePrefix.Length..]);
            return;
        }
        if (message == "overlay:play:1")
        {
            if (!overlayNativePlayStateKnown)
                SetOverlaySuppressedByPlay(true, overlayIsPaused);
            return;
        }
        if (message == "overlay:play:0")
        {
            if (!overlayNativePlayStateKnown)
                SetOverlaySuppressedByPlay(false, false);
            return;
        }
        if (message == "overlay:pause:1")
        {
            if (!overlayNativePlayStateKnown && overlayPlayStateKnown)
                SetOverlaySuppressedByPlay(overlayIsPlaying, true);
            return;
        }
        if (message == "overlay:pause:0")
        {
            if (!overlayNativePlayStateKnown && overlayPlayStateKnown)
                SetOverlaySuppressedByPlay(overlayIsPlaying, false);
            return;
        }
        if (message == "overlay:focus:1")
        {
            windowsOverlay.SetOsuFocused(true);
            return;
        }
        if (message == "overlay:focus:0")
        {
            windowsOverlay.SetOsuFocused(false);
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
        if (!overlayMode)
            return;

        var signature = string.Join(
            '|',
            name,
            number?.ToString(CultureInfo.InvariantCulture) ?? "null",
            isPlaying?.ToString() ?? "null",
            isPaused?.ToString() ?? "null",
            isFocused?.ToString() ?? "null");
        if (lastGameplayTraceBySource.TryGetValue(source, out var previousSignature) &&
            string.Equals(previousSignature, signature, StringComparison.Ordinal))
            return;

        lastGameplayTraceBySource[source] = signature;
        AppLogger.Info(
            "Gameplay state trace",
            $"source={source}; name={name}; number={number?.ToString(CultureInfo.InvariantCulture) ?? "null"}; " +
            $"isPlaying={isPlaying?.ToString() ?? "null"}; paused={isPaused?.ToString() ?? "null"}; " +
            $"focused={isFocused?.ToString() ?? "null"}; " +
            $"nativeAuthoritative={overlayNativePlayStateKnown}; widgetSized={overlayWidgetSized}; opacity={Opacity:0.##}");
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
        analyzerCoordinator?.TryAccept(adapterId, json, out _);
        return true;
    }

    private void AnalyzerSnapshotChanged(AnalysisSnapshot snapshot)
    {
        if (!overlayMode || overlayNativePlayStateKnown || snapshot.Gameplay.IsPlaying is not bool isPlaying)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (overlayMode)
                SetOverlaySuppressedByPlay(isPlaying, snapshot.Gameplay.IsPaused);
        });
    }

    private async Task ApplyPresentationAsync()
    {
        if (model is null)
            return;
        await ApplyPresentationAsync(model.Settings, overlayMode, updateFullscreen: true, reportErrors: true, CancellationToken.None);
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
            await presentationGate.WaitAsync(cancellationToken);
            entered = true;
            var analyzer = presentation.ResolveAnalyzer(settings.AnalyzerProviderId);
            var scripts = presentation.Build(settings, presentationOverlayMode);
            await Browser.InvokeScript(scripts.SetupScript);
            await Browser.InvokeScript(scripts.ObserverScript);
            if (updateFullscreen && settings.FullscreenOverlayEnabled)
                fullscreen.WriteRuntime(
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
            if (reportErrors && model is not null)
            {
                model.SetStatus(L("dialog.configuration_error") + ": " + exception.Message);
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
                presentationGate.Release();
        }
    }

    private async Task RequestOverlayWidgetSizeReportAsync()
    {
        var entered = false;
        try
        {
            await presentationGate.WaitAsync();
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
                presentationGate.Release();
        }
    }

    private async Task AdjustScaleAsync(int delta)
    {
        try
        {
            if (model is null)
                return;
            overlayResizeDebounceTimer.Stop();
            overlayResizeScaleUpdatePending = false;
            overlayNativeResizePending = false;
            overlayExpectedWidgetPhysicalWidth = null;
            overlayResizeGuardUntilUtc = default;
            var next = Math.Clamp(model.Settings.OverlayScalePercent + delta, 50, 180);
            if (next == model.Settings.OverlayScalePercent)
                return;
            model.Settings.OverlayScalePercent = next;
            model.SaveSettings();
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
    private void Dashboard_Click(object? sender, RoutedEventArgs e) => Navigate(BaseUrl + "/");

    private async void Appearance_Click(object? sender, RoutedEventArgs e)
    {
        if (model is null)
            return;
        var dialog = new AppearanceDialog(model.Settings);
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
            var selectedAnalyzer = presentation.ResolveAnalyzer(dialog.AnalyzerProviderId);
            var settingsUri = selectedAnalyzer.GetSettingsUri(TosuBaseUri);
            if (settingsUri is not null)
                Navigate(settingsUri.ToString());
            return;
        }
        var analyzerChanged = !string.Equals(
            model.Settings.AnalyzerProviderId,
            dialog.AnalyzerProviderId,
            StringComparison.OrdinalIgnoreCase);
        model.Settings.AnalyzerProviderId = dialog.AnalyzerProviderId;
        if (analyzerChanged)
            analyzerCoordinator?.Switch(model.Settings.AnalyzerProviderId);
        model.Settings.OverlayLayoutMode = dialog.LayoutMode;
        model.Settings.OverlayPresetId = dialog.PresetId;
        model.Settings.OverlayScalePercent = dialog.ScalePercent;
        UpdatePreviewScaleText();
        var restartForFullscreen = false;
        if (model.Settings.FullscreenOverlayEnabled && !ActiveAnalyzer.Descriptor.SupportsFullscreen)
        {
            if (fullscreen.IsSupported)
                fullscreen.SetEnabled(false);
            model.Settings.FullscreenOverlayEnabled = false;
            restartForFullscreen = true;
        }
        else if (model.Settings.FullscreenOverlayEnabled)
        {
            fullscreen.EnsureProfile(model.Settings, ActiveAnalyzer.Descriptor, true);
            restartForFullscreen = true;
        }
        model.SaveSettings();
        if (restartForFullscreen)
            await model.RestartAsync();
        Navigate(AnalysisUrl);
    }

    private void AppearancePreviewChanged(LauncherSettings previewSettings)
    {
        if (model is null || !ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
            return;
        previewPresentationCancellation?.Cancel();
        previewPresentationCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        previewPresentationCancellation = cancellation;
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
        previewPresentationCancellation?.Cancel();
        previewPresentationCancellation?.Dispose();
        previewPresentationCancellation = null;
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

            effectiveAnalysisConfiguration = effectiveAnalysisStore.Load();
            AppLogger.Info(
                "Effective analysis mapping",
                $"Reloaded mapping: {effectiveAnalysisConfiguration.Widgets.Length} widget(s), engine={effectiveAnalysisConfiguration.DefaultEngineId}, algorithm={effectiveAnalysisConfiguration.DefaultAlgorithm}");
            InitializeHeadlessRunners();
            lastHeadlessSceneKey = null;
            lastHeadlessBeatmapKey = null;
            if (analyzerSupervisor?.IsReady == true)
            {
                _ = PollHeadlessBeatmapAsync();
            }

            model?.SetStatus(L("mapping.title") + ": " + effectiveAnalysisConfiguration.Widgets.Length + " widget(s)");
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
        if (model is null)
            return;
        SetComponentPreparationState(componentPreparationFailed);
        SetControlsEnabled(false);
        ShowMessagePage(L("dialog.prepare_tosu.title"), L("dialog.prepare_tosu.message"), false);
        if (!await CheckUpdatesAsync())
            return;
        await model.RestartAsync();
        var running = model.Tosu.IsRunning;
        if (running)
        {
            SetComponentPreparationState(false);
            model.SetStatus(L("status.tosu_running"), true);
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

        if (!running && analyzerSupervisor is not null)
        {
            await analyzerSupervisor.NotifyTosuRestartAsync();
        }
    }

    private void LanguageSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (model is null || updatingLanguageSelector || LanguageSelector.SelectedItem is not LanguageOption selected)
            return;
        UiText.Initialize(selected.Id);
        model.Settings.Language = UiText.CurrentLanguage;
        model.SaveSettings();
        ApplyLanguage();
        model.SetStatus(L(model.Tosu.IsRunning ? "status.tosu_running" : "status.tosu_not_running"), model.Tosu.IsRunning);
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
        if (model is null || overlayMode)
            return;
        if (OperatingSystem.IsWindows() && !windowsOverlay.RegisterHotkeys())
        {
            await InfoAsync(L("dialog.hotkey.title"), L("dialog.hotkey.message"));
            return;
        }
        if (model.Settings.OverlayHintVersion < 3)
        {
            await InfoAsync(L("dialog.overlay.title"), L("dialog.overlay.message"));
            model.Settings.OverlayHintVersion = 3;
            model.SaveSettings();
        }

        normalPosition = Position;
        normalClientSize = ClientSize;
        overlayMode = true;
        SetOverlayWindowVisibility(false);
        overlayWidgetSized = false;
        overlayPlayStateKnown = false;
        overlayNativePlayStateKnown = false;
        lastGameplayTraceBySource.Clear();
        overlayIsPlaying = false;
        overlayIsPaused = null;
        overlaySuppressedByPolicy = false;
        overlayVisibilityPolicy = ResolveOverlayVisibilityPolicy();
        overlayInteractive = false;
        suppressOverlayResizeFeedback = false;
        overlayResizeScaleUpdatePending = false;
        overlayNativeResizePending = false;
        overlayExpectedWidgetPhysicalWidth = null;
        overlayResizeGuardUntilUtc = default;
        ignoredProgrammaticOverlaySize = null;
        overlayResizeDebounceTimer.Stop();
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

        var layout = OverlayPresentationService.NormalizeLayout(model.Settings.OverlayLayoutMode);
        var scale = Math.Clamp(model.Settings.OverlayScalePercent, 50, 180) / 100d;
        var width = (layout == "horizontal" ? 920 : layout is "companella" or "companella-replay" ? 760 : 475) * scale;
        var height = (layout == "horizontal" ? 360 : layout is "companella" or "companella-replay" ? 340 : 540) * scale;
        ClientSize = new Size(width, height);
        var working = Screens.ScreenFromWindow(this)?.WorkingArea ?? Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var savedVisible = model.Settings.OverlayX > -30000 && model.Settings.OverlayY > -30000;
        Position = savedVisible
            ? new PixelPoint(model.Settings.OverlayX, model.Settings.OverlayY)
            : new PixelPoint(working.Right - (int)Math.Ceiling(width * RenderScaling) - 18, working.Y + 18);
        windowsOverlay.Enter();
        Navigate(AnalysisUrl);
        StartOverlayGameplayPolling();
    }

    private void LeaveOverlayMode()
    {
        if (!overlayMode || model is null)
            return;
        SaveOverlayBounds();
        overlayInteractive = false;
        StopOverlayGameplayPolling();
        overlayResizeDebounceTimer.Stop();
        overlayResizeScaleUpdatePending = false;
        overlayNativeResizePending = false;
        overlayExpectedWidgetPhysicalWidth = null;
        overlayResizeGuardUntilUtc = default;
        ignoredProgrammaticOverlaySize = null;
        windowsOverlay.Leave();
        overlayMode = false;
        overlayWidgetSized = false;
        overlayPlayStateKnown = false;
        overlayNativePlayStateKnown = false;
        lastGameplayTraceBySource.Clear();
        overlayIsPlaying = false;
        overlayIsPaused = null;
        overlaySuppressedByPolicy = false;
        overlayVisibilityPolicy = OverlayVisibilityPolicy.Always;
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
        Position = normalPosition;
        ClientSize = normalClientSize;
        SetOverlayWindowVisibility(true);
        Navigate(AnalysisUrl);
        Activate();
    }

    private void ResizeOverlayToWidget(int physicalWidth, int physicalHeight)
    {
        if (!overlayMode || physicalWidth is < 120 or > 2400 || physicalHeight is < 80 or > 3200)
            return;
        if (overlayInteractive)
        {
            if (overlayExpectedWidgetPhysicalWidth is int expectedWidth)
            {
                var matchesExpectedWidth = IsCloseToPhysicalWidth(physicalWidth, expectedWidth);
                if (!matchesExpectedWidth &&
                    (overlayNativeResizePending || overlayResizeScaleUpdateRunning ||
                     DateTime.UtcNow < overlayResizeGuardUntilUtc))
                    return;
                if (!matchesExpectedWidth || DateTime.UtcNow >= overlayResizeGuardUntilUtc)
                {
                    overlayExpectedWidgetPhysicalWidth = null;
                    overlayResizeGuardUntilUtc = default;
                }
            }
            else if (overlayNativeResizePending || overlayResizeDebounceTimer.IsEnabled)
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
            ignoredProgrammaticOverlaySize = targetSize;
        else
            ignoredProgrammaticOverlaySize = null;
        suppressOverlayResizeFeedback = true;
        try
        {
            ClientSize = targetSize;
            Position = position;
        }
        finally
        {
            suppressOverlayResizeFeedback = false;
        }
        overlayWidgetSized = true;
        UpdateOverlayVisibility();
        SaveOverlayBounds();
    }

    private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!overlayMode)
            return;
        if (!overlayInteractive || suppressOverlayResizeFeedback)
            return;
        if (ignoredProgrammaticOverlaySize is Size programmaticSize && IsCloseToSize(ClientSize, programmaticSize))
        {
            ignoredProgrammaticOverlaySize = null;
            return;
        }
        ignoredProgrammaticOverlaySize = null;
        overlayNativeResizePending = true;
        overlayExpectedWidgetPhysicalWidth = null;
        QueueOverlayScaleUpdate();
    }

    private void QueueOverlayScaleUpdate()
    {
        if (!overlayMode || !overlayInteractive || suppressOverlayResizeFeedback)
            return;
        overlayResizeDebounceTimer.Stop();
        overlayResizeDebounceTimer.Start();
    }

    private async void OverlayResizeDebounceTimer_Tick(object? sender, EventArgs e)
    {
        overlayResizeDebounceTimer.Stop();
        if (overlayResizeScaleUpdateRunning)
        {
            overlayResizeScaleUpdatePending = true;
            return;
        }

        overlayResizeScaleUpdateRunning = true;
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
            overlayResizeScaleUpdateRunning = false;
            if (overlayResizeScaleUpdatePending)
            {
                overlayResizeScaleUpdatePending = false;
                QueueOverlayScaleUpdate();
            }
        }
    }

    private async Task ApplyOverlayScaleFromWindowAsync()
    {
        if (!overlayMode || !overlayInteractive || suppressOverlayResizeFeedback || model is null)
            return;
        var baseWidth = GetOverlayBaseWidth(model.Settings.OverlayLayoutMode);
        if (baseWidth <= 0 || ClientSize.Width <= 0)
            return;

        var next = Math.Clamp((int)Math.Round(ClientSize.Width / baseWidth * 100d), 50, 180);
        if (next == model.Settings.OverlayScalePercent)
        {
            overlayNativeResizePending = false;
            overlayExpectedWidgetPhysicalWidth = null;
            overlayResizeGuardUntilUtc = default;
            await RequestOverlayWidgetSizeReportAsync();
            return;
        }
        overlayExpectedWidgetPhysicalWidth = (int)Math.Round(baseWidth * next / 100d * RenderScaling);
        overlayResizeGuardUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
        model.Settings.OverlayScalePercent = next;
        model.SaveSettings();
        try
        {
            await ApplyPresentationAsync();
            await RequestOverlayWidgetSizeReportAsync();
        }
        finally
        {
            overlayNativeResizePending = false;
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
        var visibilityPolicy = overlayVisibilityPolicy;
        var shouldShow = OverlayVisibilityPolicy.ShouldShow(visibilityPolicy, isPlaying, isPaused);
        var suppressed = !shouldShow;
        var stateChanged = !overlayPlayStateKnown ||
                           overlayIsPlaying != isPlaying ||
                           overlayIsPaused != isPaused ||
                           overlaySuppressedByPolicy != suppressed;
        overlayPlayStateKnown = true;
        overlayIsPlaying = isPlaying;
        overlayIsPaused = isPaused;
        overlaySuppressedByPolicy = suppressed;
        UpdateOverlayVisibility();
        if (stateChanged)
            LogOverlayGameplayState(visibilityPolicy, isPlaying, isPaused);
    }

    private void StartOverlayGameplayPolling()
    {
        StopOverlayGameplayPolling();
        if (model is null)
            return;

        overlayGameplayPollCancellation = new CancellationTokenSource();
        overlayGameplayPollTimer.Start();
        _ = PollOverlayGameplayStateAsync();
    }

    private void StopOverlayGameplayPolling()
    {
        overlayGameplayPollTimer.Stop();
        overlayGameplayPollCancellation?.Cancel();
        overlayGameplayPollCancellation?.Dispose();
        overlayGameplayPollCancellation = null;
    }

    private async void OverlayGameplayPollTimer_Tick(object? sender, EventArgs e) =>
        await PollOverlayGameplayStateAsync();

    private async Task PollOverlayGameplayStateAsync()
    {
        if (!overlayMode || model is null || Interlocked.Exchange(ref overlayGameplayPollInFlight, 1) != 0)
            return;

        var cancellationToken = overlayGameplayPollCancellation?.Token ?? CancellationToken.None;
        try
        {
            var state = await model.Tosu.GetGameplayStateAsync(cancellationToken);
            if (state is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (overlayMode)
                    {
                        overlayNativePlayStateKnown = true;
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
            Interlocked.Exchange(ref overlayGameplayPollInFlight, 0);
        }
    }

    private void UpdateOverlayVisibility()
    {
        if (!overlayMode)
            return;
        // A size report is an optimization for synchronizing the native
        // window bounds, not a prerequisite for visibility. If WebView has
        // not reported its first measurement yet, the saved/default client
        // size is still a valid widget surface and must be shown in menu.
        var visible = overlayPlayStateKnown && !overlaySuppressedByPolicy;
        SetOverlayWindowVisibility(visible);
    }

    private void SetOverlayWindowVisibility(bool visible)
    {
        if (overlayWindowVisible == visible)
            return;

        try
        {
            if (visible)
                Opacity = 1;

            if (OperatingSystem.IsWindows())
            {
                windowsOverlay.SetWindowVisible(visible);
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
            overlayWindowVisible = visible;
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
            ? windowsOverlay.IsWindowShown
            : IsVisible;
        AppLogger.Info(
            "Overlay gameplay state",
            $"visibilityPolicy={visibilityPolicy}; " +
            $"isPlaying={isPlaying}; paused={isPaused?.ToString() ?? "null"}; " +
            $"requestedVisible={overlayWindowVisible}; " +
            $"nativeVisible={nativeVisible}; opacity={Opacity:0.##}");
    }

    private string ResolveOverlayVisibilityPolicy()
    {
        if (model is null)
            return OverlayVisibilityPolicy.Always;

        var requestedPreset = string.IsNullOrWhiteSpace(model.Settings.OverlayPresetId) ||
                              (model.Settings.OverlayPresetId == "default" && model.Settings.OverlayLayoutMode != "default")
            ? model.Settings.OverlayLayoutMode
            : model.Settings.OverlayPresetId;
        return OverlayVisibilityPolicy.Normalize(presetCatalog.Get(requestedPreset).VisibilityPolicy);
    }

    private void SaveOverlayBounds()
    {
        if (!overlayMode || model is null)
            return;
        model.Settings.OverlayX = Position.X;
        model.Settings.OverlayY = Position.Y;
        model.Settings.OverlayWidth = (int)Math.Ceiling(ClientSize.Width * RenderScaling);
        model.Settings.OverlayHeight = (int)Math.Ceiling(ClientSize.Height * RenderScaling);
        model.SaveSettings();
    }

    private async void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        if (model is null || !fullscreen.IsSupported || !ActiveAnalyzer.Descriptor.SupportsFullscreen)
            return;
        var enable = !fullscreen.ReadEnabled(model.Settings.FullscreenOverlayEnabled);
        var confirmed = await ConfirmAsync(L("dialog.fullscreen.title"),
            enable
                ? L("dialog.fullscreen.enable")
                : L("dialog.fullscreen.disable"));
        if (!confirmed)
            return;
        try
        {
            fullscreen.SetEnabled(enable);
            model.Settings.FullscreenOverlayEnabled = enable;
            if (enable)
            {
                model.Settings.FullscreenOverlayStyleVersion = 1;
                fullscreen.EnsureProfile(model.Settings, ActiveAnalyzer.Descriptor, true);
            }
            model.SaveSettings();
            UpdateFullscreenButton();
            await model.RestartAsync();
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
        var enabled = model?.Settings.FullscreenOverlayEnabled == true;
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
