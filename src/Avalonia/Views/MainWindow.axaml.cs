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
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;
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
    private static readonly Uri _tosuBaseUri = new(BaseUrl);

    private readonly OverlayPresetCatalog _presetCatalog = new();
    private readonly AnalyzerAdapterCatalog _analyzerCatalog = new();
    private readonly OverlayPresentationService _presentation;
    private readonly FullscreenOverlayService _fullscreen = new();
    private readonly UpdateService _updates = new();
    private readonly WindowsOverlayController _windowsOverlay;
    private DispatcherTimer? _overlayGameplayPollTimer;
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly SemaphoreSlim _overlayScaleGate = new(1, 1);
    private readonly AnalyzerEngineCatalog _analyzerEngineCatalog = new();
    private readonly AnalyzerEnginePackageDeployer _analyzerEngineDeployer = new();
    private readonly EffectiveAnalysisConfigurationStore _effectiveAnalysisStore = new();
    private readonly ReplayAnalysisSession _replayAnalysisSession = new();
    private readonly OverlayDragSession _overlayDragSession = new();
    private readonly IRealtimeTelemetrySource _overlayRealtimeSource;
    private readonly IOverlayWindow _overlayWindow;
    private readonly OverlayRuntimeCoordinator _runtimeCoordinator = new();
    private readonly LatestWinsSnapshotPublisher<OverlayViewState> _nativePauseCoachPublisher;
    private readonly LatestWinsSnapshotPublisher<OverlayViewState> _fullscreenViewStatePublisher;
    private static readonly JsonSerializerOptions _overlaySnapshotJsonOptions = new(JsonSerializerDefaults.Web);
    private NativeWebView Browser { get; set; } = null!;
    private MainViewModel? _model;
    private CancellationTokenSource? _previewPresentationCancellation;
    private CancellationTokenSource? _overlayGameplayPollCancellation;
    private AnalyzerCoordinator? _analyzerCoordinator;
    private HeadlessAnalysisController? _headlessAnalysisController;
    private AnalyzerEngineSupervisorState? _lastSupervisorState;
    private AnalysisSnapshot? _lastAnalyzerSnapshot;
    private bool _initialized;
    private bool _overlayMode;
    private bool _overlayWidgetSized;
    private bool _overlayUsesAuthoritativeSize;
    private double? _overlayRenderedBaseHeight;
    private bool _overlayScaleUpdateInProgress;
    private int _queuedOverlayScaleDelta;
    private int _overlayScaleQueueRunning;
    private bool _overlayPlayStateKnown;
    private bool _overlayNativePlayStateKnown;
    private bool _overlayIsPlaying;
    private bool? _overlayIsPaused;
    private bool _overlaySuppressedByPolicy;
    private string _overlayVisibilityPolicy = OverlayVisibilityPolicy.Always;
    private bool _overlayInteractive;
    // WebView callbacks can arrive while the native window is processing an
    // input message. Keep native drag startup serialized and defer it to the
    // Avalonia UI queue so WM_NCLBUTTONDOWN is never entered re-entrantly.
    private int _overlayNativeDragPending;
    private int _overlayGameplayPollInFlight;
    private long _overlayGameplayPollGeneration;
    private bool _shadowPresentationReady;
    private bool _shadowPresentationVisible;
    private string _lastShadowRuntimeDiagnostic = string.Empty;
    private DateTimeOffset _lastShadowRuntimeDiagnosticAt;
    private string _lastShadowRuntimeMismatch = string.Empty;
    private DateTimeOffset _lastShadowRuntimeMismatchAt;
    private string _lastNativePauseCoachDiagnostic = string.Empty;
    private DateTimeOffset _lastNativePauseCoachDiagnosticAt;
    private string _lastNativePollBoundaryDiagnostic = string.Empty;
    private DateTimeOffset _lastNativePollBoundaryDiagnosticAt;
    private bool _componentPreparationFailed;
    private bool _updatingLanguageSelector;
    private bool _isClosing;
    private readonly Dictionary<string, string> _lastGameplayTraceBySource = new(StringComparer.OrdinalIgnoreCase);
    private bool _showingLoggedError;
    private bool _overlayWindowVisible = true;
    private PixelPoint _normalPosition;
    private Size _normalClientSize;
    private NativeWebView? _headlessBrowser;
    private TaskCompletionSource<bool>? _headlessBrowserReady;
    private CancellationTokenSource? _overlayLayoutReconciliationCancellation;
    private long _overlayLayoutReconciliationGeneration;

    public MainWindow()
    {
        AppLogger.ErrorRaised += AppLogger_ErrorRaised;
        InitializeComponent();
        _overlayRealtimeSource = new TosuRealtimeTelemetrySource(
            cancellationToken => _model?.Tosu.GetGameplayPayloadAsync(cancellationToken)
                ?? Task.FromResult<JsonElement?>(null),
            "native-http");
        _runtimeCoordinator.TransitionApplied += ShadowRuntimeCoordinator_TransitionApplied;
        _runtimeCoordinator.ViewStateChanged += RuntimeCoordinator_ViewStateChanged;
        // The launcher is an ordinary opaque window. Keep its WebView2 on the
        // regular child-HWND path so the first document paint is not dependent
        // on the offscreen compositor being attached during Window.Opened.
        // Transparent overlay and headless surfaces opt into offscreen mode
        // explicitly below.
        Browser = CreateBrowser(new SolidColorBrush(Color.Parse("#0E1016")), offscreen: false);
        BrowserHost.Child = Browser;
        _presentation = new OverlayPresentationService(_presetCatalog, _analyzerCatalog);
        _nativePauseCoachPublisher = new LatestWinsSnapshotPublisher<OverlayViewState>(
            PublishNativePauseCoachSnapshotToBrowserAsync,
            exception => AppLogger.Error("Publishing native Pause Coach snapshot", exception, userVisible: false));
        _fullscreenViewStatePublisher = new LatestWinsSnapshotPublisher<OverlayViewState>(
            WriteFullscreenViewStateAsync,
            exception => AppLogger.Error("Publishing fullscreen view state", exception, userVisible: false));
        _windowsOverlay = new WindowsOverlayController(this);
        _overlayWindow = new WindowsOverlayWindowAdapter(_windowsOverlay);
        _windowsOverlay.ExitRequested += (_, _) => LeaveOverlayMode();
        _windowsOverlay.ClickThroughChanged += enabled => Browser.IsHitTestVisible = !enabled;
        _windowsOverlay.InteractionChanged += interactive =>
        {
            _overlayInteractive = interactive;
            if (!interactive)
            {
                CancelOverlayGestures();
            }

            if (_overlayMode)
            {
                // Mouse edge/corner resizing is intentionally disabled for
                // the overlay. Its size is controlled by Ctrl+wheel scale
                // changes and by the rendered widget's own size reports.
                CanResize = false;
            }

            UpdateOverlayVisibility();
        };
        _windowsOverlay.OsuProcessChanged += running =>
        {
            if (running || !_overlayMode)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                ReturnToLauncherAfterGameExit(
                    "status.osu_closed");
            });
        };
        _windowsOverlay.OsuWindowPresenceChanged += present =>
        {
            if (present || !_overlayMode)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                ReturnToLauncherAfterGameExit(
                    "status.osu_closed");
            });
        };
        _windowsOverlay.OsuWindowMinimizedChanged += minimized =>
        {
            PostShadowRuntimeEvent(sequence => new OsuWindowStateChanged(sequence, minimized));
            if (_overlayMode)
            {
                ApplyOverlayWindowAppearance(minimized);
                // Minimized osu! is an explicit presentation state: the
                // widget must remain visible and the latest native snapshot
                // must be flushed even when the reducer/UI queue is still
                // processing the preceding gameplay frame. Waiting for the
                // queued shadow event here can leave the publisher marked
                // hidden until the next navigation/reload.
                if (minimized)
                {
                    SetOverlayWindowVisibility(true);
                    SetNativePresentationVisible(true);
                    if (_overlayGameplayPollTimer is null && _model?.Tosu.IsRunning == true)
                    {
                        StartOverlayGameplayPolling();
                    }
                }
                else
                {
                    UpdateOverlayVisibility();
                }
            }
        };
        Deactivated += (_, _) => CancelOverlayGestures();
        Opened += async (_, _) =>
        {
            try
            {
                await InitializeAsync();
                // Navigation can complete before the first native snapshot
                // arrives. Store the visible launcher state independently so
                // the publisher can flush that snapshot as soon as the
                // document reports ready, even if the initial navigation
                // happened during the Opened lifecycle transition.
                if (!_overlayMode && IsVisible)
                {
                    SetNativePresentationVisible(true);
                }
            }
            catch (Exception exception)
            {
                AppLogger.Error("Initializing application", exception);
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        AppLogger.ErrorRaised -= AppLogger_ErrorRaised;
        BeginNativePresentationSession();
        SetFullscreenViewStatePresentation(false);
        StopOverlayGameplayPolling();
        _previewPresentationCancellation?.Cancel();
        _previewPresentationCancellation?.Dispose();
        _windowsOverlay.Dispose();
        _updates.Dispose();
        if (_analyzerCoordinator is not null)
        {
            _analyzerCoordinator.SnapshotChanged -= AnalyzerSnapshotChanged;
        }

        if (_headlessAnalysisController is not null)
        {
            _headlessAnalysisController.StateChanged -= HeadlessAnalysisController_StateChanged;
            _headlessAnalysisController.ResultProduced -= HeadlessAnalysisController_ResultProduced;
            _headlessAnalysisController.BeatmapSourceStateChanged -= HeadlessAnalysisController_BeatmapSourceStateChanged;
            var disposeTask = _headlessAnalysisController.DisposeAsync().AsTask();
            _ = ObserveControllerDisposeAsync(disposeTask);
        }

        if (_headlessBrowser is not null)
        {
            _headlessBrowser.NavigationCompleted -= HeadlessBrowser_NavigationCompleted;
            HeadlessBrowserHost.Child = null;
            _headlessBrowser = null;
        }

        CancelOverlayLayoutReconciliation();
        _model?.Dispose();
        _runtimeCoordinator.TransitionApplied -= ShadowRuntimeCoordinator_TransitionApplied;
        _runtimeCoordinator.ViewStateChanged -= RuntimeCoordinator_ViewStateChanged;
        _ = ObserveControllerDisposeAsync(_runtimeCoordinator.StopAsync());
        base.OnClosed(e);
    }

    private NativeWebView CreateBrowser(IBrush background, bool offscreen)
    {
        var browser = new NativeWebView { Background = background };
        browser.EnvironmentRequested += (_, args) =>
        {
            if (offscreen && args is WindowsWebView2EnvironmentRequestedEventArgs webView2)
            {
                // A WebView2 child HWND cannot be composed reliably into a
                // transparent, click-through top-level window. Only the
                // overlay needs this offscreen path; using it for the opaque
                // launcher can leave a valid DOM on an unpainted surface.
                webView2.ExperimentalOffscreen = true;
            }
        };
        browser.NavigationCompleted += Browser_NavigationCompleted;
        browser.WebMessageReceived += Browser_WebMessageReceived;
        browser.NewWindowRequested += Browser_NewWindowRequested;
        return browser;
    }

    private NativeWebView CreateHeadlessBrowser()
    {
        var browser = new NativeWebView
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        browser.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs webView2)
            {
                // Headless analysis has no visible native surface and must
                // always use the offscreen WebView2 path.
                webView2.ExperimentalOffscreen = true;
            }
        };
        browser.NavigationCompleted += HeadlessBrowser_NavigationCompleted;
        HeadlessBrowserHost.Child = browser;
        return browser;
    }

    private void ReplaceBrowser(IBrush background, bool offscreen)
    {
        BeginNativePresentationSession();
        var previous = Browser;
        previous.NavigationCompleted -= Browser_NavigationCompleted;
        previous.WebMessageReceived -= Browser_WebMessageReceived;
        previous.NewWindowRequested -= Browser_NewWindowRequested;
        BrowserHost.Child = null;
        Browser = CreateBrowser(background, offscreen);
    }

    private void RecreateOverlayBrowser()
    {
        // The offscreen WebView2 compositor can retain the last launcher frame
        // when the existing NativeWebView is moved into the transparent overlay
        // HWND. Recreating the control gives the overlay a fresh composition
        // surface, while the providers created by the headless controller keep
        // resolving the current Browser instance. Keep it detached until the
        // overlay HWND has its final size/styles; attaching a WebView2 surface
        // while Avalonia is still resizing the transparent window can fail in
        // SizeChangedCore and leave the first page visually blank until reload.
        ReplaceBrowser(Brushes.Transparent, offscreen: true);
    }

    private static async Task ObserveControllerDisposeAsync(Task disposeTask)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Disposing headless analysis controller", exception, userVisible: false);
        }
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
            {
                return;
            }

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
        {
            return;
        }

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
        {
            return;
        }

        SynchronizeFullscreenState();
        await _model.StartAsync();
        if (_model.Tosu.IsRunning)
        {
            SetComponentPreparationState(false);
            _model.SetStatus(L("status.tosu_running"), true);
            SetControlsEnabled(true);
            Navigate(AnalysisUrl);
            // Keep the normal launcher preview fed by the same native Tosu
            // realtime stream as the overlay. The preview is a visible
            // presentation surface too; otherwise it only changes after a
            // navigation/reload while the overlay updates continuously.
            StartOverlayGameplayPolling();
        }
        else
        {
            SetComponentPreparationState(true);
            SetControlsEnabled(false, keepRestart: true);
            ShowMessagePage(L("status.tosu_not_running"), _model.Status, true);
        }

        await InitializeHeadlessAnalysisControllerAsync();
    }

    private async Task InitializeHeadlessAnalysisControllerAsync()
    {
        try
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var beatmapSource = new TosuBeatmapSource(httpClient, _tosuBaseUri);
            _headlessBrowser ??= CreateHeadlessBrowser();
            var scriptHostFactory = () => new WebViewAnalyzerScriptHost(() =>
                _headlessBrowser ?? throw new InvalidOperationException("The headless analyzer WebView is unavailable."));
            // Headless analysis has its own offscreen WebView. Presentation
            // navigation and overlay recreation therefore cannot reset the
            // analyzer runtime or deliver messages to a detached document.
            var presenter = new RuntimeAnalysisSnapshotPresenter(snapshot =>
                PostShadowRuntimeEvent(sequence => new AnalysisSnapshotReceived(sequence, snapshot)));

            _headlessAnalysisController = new HeadlessAnalysisController(
                new HeadlessEngineServices(_analyzerEngineCatalog, _analyzerEngineDeployer, scriptHostFactory),
                httpClient,
                beatmapSource,
                presenter,
                _effectiveAnalysisStore,
                TimeSpan.FromMilliseconds(900));

            _headlessAnalysisController.StateChanged += HeadlessAnalysisController_StateChanged;
            _headlessAnalysisController.ResultProduced += HeadlessAnalysisController_ResultProduced;
            _headlessAnalysisController.BeatmapSourceStateChanged += HeadlessAnalysisController_BeatmapSourceStateChanged;

            // Ensure the WebView has finished loading the analysis page before
            // bootstrapping the headless runtime. Injecting the runtime too early
            // makes globalThis.location.href point at the previous document and
            // the subsequent navigation resets the bridge, producing engine.runtime_reset.
            await EnsureHeadlessBrowserReadyAsync();
            await _headlessAnalysisController.StartAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Initializing headless analysis controller", exception);
        }
    }

    private void Tosu_StateChanged(object? sender, TosuStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusDot.Fill = new SolidColorBrush(Color.Parse(e.IsRunning ? "#3DCF8E" : "#FF5F7E"));
            if (e.IsRunning)
            {
                SetControlsEnabled(true);
                StartOverlayGameplayPolling();
                if (_headlessAnalysisController is not null)
                {
                    _ = _headlessAnalysisController.NotifyTosuRestartAsync();
                }
            }
            else
            {
                StopOverlayGameplayPolling();
                if (_initialized && _overlayMode)
                {
                    ReturnToLauncherAfterGameExit(
                        "status.osu_stopped");
                }
                else if (_initialized)
                {
                    SetControlsEnabled(false, keepRestart: true);
                }
            }
        });
    }

    private void ReturnToLauncherAfterGameExit(string statusKey)
    {
        if (!_overlayMode)
        {
            return;
        }

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

    private async Task EnsureHeadlessBrowserReadyAsync()
    {
        try
        {
            _headlessBrowser ??= CreateHeadlessBrowser();
            if (ActiveAnalyzer.MatchesAnalysisUri(_headlessBrowser.Source))
            {
                await Task.Delay(400);
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _headlessBrowserReady = completion;
            try
            {
                _headlessBrowser.Navigate(new Uri(AnalysisUrl));

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
                if (ReferenceEquals(_headlessBrowserReady, completion))
                {
                    _headlessBrowserReady = null;
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Waiting for analysis WebView", $"Could not confirm WebView readiness before bootstrapping headless engine: {exception.Message}", exception);
            await Task.Delay(800);
        }
    }

    private async void HeadlessBrowser_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        try
        {
            if (!ReferenceEquals(sender, _headlessBrowser))
            {
                return;
            }

            if (e.IsSuccess && ActiveAnalyzer.MatchesAnalysisUri(_headlessBrowser.Source))
            {
                _headlessBrowserReady?.TrySetResult(true);
            }

            if (_headlessAnalysisController is null)
            {
                return;
            }

            var status = _headlessAnalysisController.CurrentState.Status;
            if (status is not (AnalyzerEngineSupervisorStatus.Ready
                or AnalyzerEngineSupervisorStatus.Fallback
                or AnalyzerEngineSupervisorStatus.Error))
            {
                return;
            }

            await _headlessAnalysisController.NotifyNavigationAsync();
            await _headlessAnalysisController.RepublishLastSnapshotAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Handling headless WebView navigation", exception, userVisible: false);
        }
    }

    private void HeadlessAnalysisController_StateChanged(object? sender, AnalyzerEngineSupervisorState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _lastSupervisorState = state;
            UpdateHeadlessStatusUi(state);
        });
    }

    private void HeadlessAnalysisController_ResultProduced(object? sender, HeadlessAnalysisResultEventArgs e)
    {
        PostShadowRuntimeEvent(sequence => new AnalysisSnapshotReceived(sequence, e.Snapshot));
        Dispatcher.UIThread.Post(() =>
        {
            var status = FormatHeadlessResultStatus(e);
            if (status is not null)
            {
                _model?.SetStatus(status);
            }
        });
    }

    private void HeadlessAnalysisController_BeatmapSourceStateChanged(object? sender, HeadlessBeatmapSourceStateEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var status = e.State switch
            {
                HeadlessBeatmapSourceState.OsuNotRunning => L("status.headless_osu_not_running"),
                HeadlessBeatmapSourceState.NoBeatmap => L("status.headless_no_beatmap"),
                HeadlessBeatmapSourceState.UnsupportedMode => L("status.headless_unsupported_mode"),
                _ => null
            };

            if (status is not null && _model is not null && !string.Equals(_model.Status, status, StringComparison.Ordinal))
            {
                _model.SetStatus(status);
            }
        });
    }

    private string? FormatHeadlessResultStatus(HeadlessAnalysisResultEventArgs e)
    {
        var diagnosticsSummary = e.Diagnostics.Count == 0
            ? string.Empty
            : $" {string.Join("; ", e.Diagnostics.Take(2).Select(diagnostic => diagnostic.Code))}";

        if (e.Outcome == AnalysisOutcome.Partial)
        {
            return L("status.headless_partial") + $" {e.ActualAlgorithm ?? e.Beatmap.Metadata.Version} partial" + diagnosticsSummary;
        }

        if (e.Outcome == AnalysisOutcome.Success)
        {
            var star = e.Snapshot.Difficulty.StarRating?.ToString() ?? "n/a";
            var algo = e.ActualAlgorithm ?? e.Beatmap.Metadata.Version;
            return L("status.headless_success") + $" {e.Beatmap.Metadata.Title} [{e.Beatmap.Metadata.Version}] {algo} star={star}";
        }

        if (e.Outcome == AnalysisOutcome.Failed)
        {
            return L("status.headless_failed") + diagnosticsSummary + " (DOM fallback)";
        }

        return null;
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

    private async Task<bool> CheckUpdatesAsync()
    {
        if (_model is null)
        {
            return false;
        }

        _model.SetStatus(L("status.checking_updates"));
        try
        {
            var progress = new Progress<UpdateProgress>(update =>
                _model.SetStatus(LocalizeUpdateMessage(update.Message)));
            var result = await _updates.CheckComponentsAsync(progress: progress);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? L("status.update_failed"));
            }

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
            {
                _model.SetStatus(L("status.components_updated"));
            }
            else if (string.Equals(result.Compatibility, "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                await InfoAsync(L("dialog.compatibility.title"), UiText.Format("dialog.compatibility.message", result.LazerVersion));
            }

            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                _model.SetStatus(LocalizeResourceOrText(result.Warning));
            }

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
        {
            return;
        }

        var enabled = _fullscreen.ReadEnabled(_model.Settings.FullscreenOverlayEnabled);
        if (enabled && !ActiveAnalyzer.Descriptor.SupportsFullscreen)
        {
            if (_fullscreen.IsSupported)
            {
                _fullscreen.SetEnabled(false);
            }

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
        {
            return;
        }

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
        {
            return;
        }

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

    private string AnalysisUrl => ActiveAnalyzer.GetAnalysisUri(_tosuBaseUri).ToString();

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
        {
            PreviewScaleText.Content = _model.Settings.OverlayScalePercent + "%";
        }
    }

    private void Navigate(string url)
    {
        try
        {
            // A navigation/reinjection invalidates any call targeting the
            // previous document. Keep the latest native snapshot so the new
            // document can replay it after NavigationCompleted in either
            // launcher-preview or overlay mode.
            SetNativeBrowserReady(false);

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
            if (!ReferenceEquals(sender, Browser))
            {
                return;
            }

            var analysisDocumentReady = e.IsSuccess && ActiveAnalyzer.MatchesAnalysisUri(Browser.Source);
            if (analysisDocumentReady)
            {
                var presentationApplied = await ApplyPresentationAsync(
                    _model?.Settings ?? new LauncherSettings(),
                    _overlayMode,
                    updateFullscreen: true,
                    reportErrors: false,
                    CancellationToken.None);
                if (!presentationApplied)
                {
                    // WebView2 can complete navigation while its composition
                    // controller is still replacing the root visual target
                    // (notably during osu! minimize/restore). Do not mark the
                    // document ready in that interval: a publisher call would
                    // otherwise be accepted by a page with no renderer.
                    SetNativeBrowserReady(false);
                    _ = RetryBrowserPresentationAsync();
                    return;
                }
                if (_overlayMode)
                {
                    await FitOverlayWindowToRenderedWidgetAsync();
                    ScheduleOverlayLayoutReconciliation();
                }
            }

            if (analysisDocumentReady)
            {
                // Mark readiness only after the fresh renderer and any cached
                // headless snapshot have been injected. The coalescer then
                // flushes its newest native frame when this document is
                // visible, including the normal launcher preview.
                SetNativeBrowserReady(true);
                // Avalonia's IsVisible describes the top-level control, not
                // the native overlay HWND. The HWND may be SW_HIDE/opacity 0
                // while the WebView document remains loaded; do not flush
                // realtime frames into that hidden presentation surface.
                SetNativePresentationVisible(
                    _overlayMode ? _overlayWindowVisible && Opacity > 0 : IsVisible);
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
        {
            return;
        }

        var message = e.Body;

        if (string.Equals(message, "overlay:native-drag", StringComparison.Ordinal))
        {
            HandleNativeOverlayDrag();
            return;
        }

        if (message.StartsWith("overlay:runtime-ready:", StringComparison.Ordinal) ||
            message.StartsWith("overlay:pointerdown:", StringComparison.Ordinal))
        {
            HandleOverlayDiagnostic(message);
            return;
        }

        if (message.StartsWith(AnalyzerEngineScriptBridge.NativeMessagePrefix, StringComparison.Ordinal))
        {
            // WebViewAnalyzerScriptHost already subscribes to Browser.WebMessageReceived and forwards
            // this message to the bridge. Logging here is sufficient and avoids duplicate delivery.
            if (_headlessAnalysisController is not null && !_headlessAnalysisController.CurrentState.IsReady)
            {
                AppLogger.Info("Analyzer engine bridge", $"Received bridge message while supervisor state={_headlessAnalysisController.CurrentState.Status}.");
            }

            return;
        }

        if (message.StartsWith("overlay:error:", StringComparison.Ordinal))
        {
            AppLogger.Error("Overlay runtime", Uri.UnescapeDataString(message[14..]));
            return;
        }
        if (TryHandleGameplayStateTrace(message))
        {
            return;
        }
        if (TryHandlePauseCoachTrace(message))
        {
            return;
        }

        if (TryHandleAnalyzerMessage(message))
        {
            return;
        }

        if (string.Equals(message, "overlay:bridge-self-test", StringComparison.Ordinal))
        {
            AppLogger.Info("Overlay bridge self-test", "Received overlay bridge self-test message.");
            return;
        }

        if (!_overlayMode)
        {
            return;
        }

        const string dragPrefix = "overlay:drag:";
        if (message.StartsWith(dragPrefix, StringComparison.Ordinal))
        {
            HandleOverlayDragMessage(message[dragPrefix.Length..]);
            return;
        }
        if (message == "overlay:play:1")
        {
            if (!_overlayNativePlayStateKnown)
            {
                SetOverlaySuppressedByPlay(true, _overlayIsPaused);
            }

            return;
        }
        if (message == "overlay:play:0")
        {
            if (!_overlayNativePlayStateKnown)
            {
                SetOverlaySuppressedByPlay(false, false);
            }

            return;
        }
        if (message == "overlay:pause:1")
        {
            if (!_overlayNativePlayStateKnown && _overlayPlayStateKnown)
            {
                SetOverlaySuppressedByPlay(_overlayIsPlaying, true);
            }

            return;
        }
        if (message == "overlay:pause:0")
        {
            if (!_overlayNativePlayStateKnown && _overlayPlayStateKnown)
            {
                SetOverlaySuppressedByPlay(_overlayIsPlaying, false);
            }

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
            QueueOverlayScaleAdjustment(delta);
            return;
        }
        const string sizePrefix = "overlay:size:";
        if (!message.StartsWith(sizePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var values = message[sizePrefix.Length..].Split(',');
        if (values.Length == 3 && int.TryParse(values[0], out var width) && int.TryParse(values[1], out var height) &&
            float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            ResizeOverlayToWidget(width, height);
        }
    }

    private void HandleOverlayDragMessage(string message)
    {
        var separator = message.IndexOf(':');
        if (separator <= 0 || separator == message.Length - 1)
        {
            AppLogger.Warning(
                "Reading overlay drag message",
                "The overlay drag bridge sent a message without a valid action or payload.");
            return;
        }

        var action = message[..separator];
        if (action is not ("start" or "move" or "end"))
        {
            AppLogger.Warning("Reading overlay drag message", $"Unknown overlay drag action '{action}'.");
            return;
        }

        if (!TryReadOverlayDragPayload(message[(separator + 1)..], out var gestureId, out var pointerId,
                out var sequence, out var screenX, out var screenY))
        {
            return;
        }

        AppLogger.Debug(
            "Overlay drag bridge",
            $"Received {action} gesture={gestureId} pointer={pointerId} sequence={sequence} screen=({screenX:0.##},{screenY:0.##}) active={_overlayDragSession.IsActive}.");

        switch (action)
        {
            case "start":
                if (!_windowsOverlay.IsInteractionAllowed)
                {
                    CancelOverlayGestures();
                    return;
                }

                if (!_overlayDragSession.Start(gestureId, pointerId, sequence, screenX, screenY, Position, RenderScaling))
                {
                    AppLogger.Warning("Overlay drag bridge", $"Rejected start gesture={gestureId} pointer={pointerId} sequence={sequence}.");
                }
                break;
            case "move":
                if (!_windowsOverlay.IsInteractionAllowed)
                {
                    CancelOverlayGestures();
                    return;
                }

                if (_overlayDragSession.TryMove(
                        gestureId,
                        pointerId,
                        sequence,
                        screenX,
                        screenY,
                        out var position))
                {
                    // Deliberately update Position only. Changing ClientSize
                    // here would feed the browser's size observer back into
                    // the overlay scale debounce path.
                    Position = position;
                }
                else
                {
                    AppLogger.Debug("Overlay drag bridge", $"Ignored move gesture={gestureId} pointer={pointerId} sequence={sequence}.");
                }

                break;
            case "end":
                if (_overlayDragSession.End(gestureId, pointerId, sequence))
                {
                    SaveOverlayBounds();
                }
                else
                {
                    AppLogger.Debug("Overlay drag bridge", $"Ignored end gesture={gestureId} pointer={pointerId} sequence={sequence}.");
                }

                break;
        }
    }

    private void HandleNativeOverlayDrag()
    {
        if (Volatile.Read(ref _overlayNativeDragPending) != 0 ||
            Interlocked.Exchange(ref _overlayNativeDragPending, 1) != 0)
        {
            return;
        }

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // The pointer event may have been queued just before the
                    // overlay was left or input protection was re-enabled.
                    // Re-check both states on the UI thread before touching
                    // the native HWND.
                    if (!_overlayMode || !_windowsOverlay.IsInteractionAllowed)
                    {
                        return;
                    }

                    CancelOverlayGestures();
                    _windowsOverlay.BeginDrag();
                }
                catch (Exception exception)
                {
                    AppLogger.Error("Starting native overlay drag", exception, userVisible: false);
                }
                finally
                {
                    Volatile.Write(ref _overlayNativeDragPending, 0);
                }
            });
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _overlayNativeDragPending, 0);
            AppLogger.Error("Queueing native overlay drag", exception, userVisible: false);
        }
    }

    private static void HandleOverlayDiagnostic(string message)
    {
        var separator = message.IndexOf(':', "overlay:".Length);
        if (separator < 0 || separator == message.Length - 1)
        {
            return;
        }

        var kind = message["overlay:".Length..separator];
        try
        {
            using var document = JsonDocument.Parse(message[(separator + 1)..]);
            AppLogger.Info("Overlay runtime diagnostic", $"{kind}: {document.RootElement}");
        }
        catch (JsonException exception)
        {
            AppLogger.Warning("Overlay runtime diagnostic", $"Malformed {kind} diagnostic.", exception);
        }
    }

    private static bool TryReadOverlayDragPayload(
        string payload,
        out long gestureId,
        out long pointerId,
        out long sequence,
        out double screenX,
        out double screenY)
    {
        gestureId = 0;
        pointerId = 0;
        sequence = 0;
        screenX = 0;
        screenY = 0;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("gestureId", out var gestureElement) && gestureElement.TryGetInt64(out gestureId) &&
                   root.TryGetProperty("pointerId", out var pointerElement) && pointerElement.TryGetInt64(out pointerId) &&
                   root.TryGetProperty("sequence", out var sequenceElement) && sequenceElement.TryGetInt64(out sequence) &&
                   root.TryGetProperty("screenX", out var screenXElement) && screenXElement.TryGetDouble(out screenX) &&
                   root.TryGetProperty("screenY", out var screenYElement) && screenYElement.TryGetDouble(out screenY);
        }
        catch (JsonException exception)
        {
            AppLogger.Warning("Reading overlay drag message", "The overlay drag bridge sent malformed JSON.", exception);
            return false;
        }
    }

    private void CancelOverlayGestures()
    {
        _overlayDragSession.Cancel();
    }

    private bool TryHandleGameplayStateTrace(string message)
    {
        const string prefix = "overlay:state-debug:";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

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

    private static bool TryHandlePauseCoachTrace(string message)
    {
        const string adapterPrefix = "overlay:pause-coach-debug:";
        const string renderPrefix = "overlay:pause-coach-render-debug:";
        string? sourcePrefix = message.StartsWith(adapterPrefix, StringComparison.Ordinal)
            ? adapterPrefix
            : message.StartsWith(renderPrefix, StringComparison.Ordinal)
                ? renderPrefix
                : null;
        if (sourcePrefix is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Uri.UnescapeDataString(message[sourcePrefix.Length..]));
            var root = document.RootElement;
            string GetString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
            string GetNumber(string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.String
                ? value.ToString()
                : "null";
            string GetBool(string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean().ToString()
                : "null";

            AppLogger.Info(
                sourcePrefix == adapterPrefix ? "PauseCoach adapter telemetry" : "PauseCoach renderer telemetry",
                $"source={GetString("source")}; rawState={GetString("rawState")}; rawStateNumber={GetNumber("rawStateNumber")}; " +
                $"rawPaused={GetBool("rawPaused")}; normalizedIsPlaying={GetBool("normalizedIsPlaying")}; " +
                $"normalizedIsPaused={GetBool("normalizedIsPaused")}; runtimeState={GetString("runtimeState")}; " +
                $"beatmap={GetString("beatmapId")}; mapTimeMs={GetNumber("mapTimeMs")}; score={GetNumber("score")}; " +
                $"accuracy={GetNumber("accuracy")}; judgementCount={GetNumber("judgementCount")}; " +
                $"hitErrorArrayLength={GetNumber("hitErrorArrayLength")}; sessionId={GetString("sessionId")}; " +
                $"coachState={GetString("coachState")}");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Reading Pause Coach trace", exception, userVisible: false);
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
        {
            return;
        }

        var signature = string.Join(
            '|',
            name,
            number?.ToString(CultureInfo.InvariantCulture) ?? "null",
            isPlaying?.ToString() ?? "null",
            isPaused?.ToString() ?? "null",
            isFocused?.ToString() ?? "null");
        if (_lastGameplayTraceBySource.TryGetValue(source, out var previousSignature) &&
            string.Equals(previousSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

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
        {
            return false;
        }

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
        PostShadowRuntimeEvent(sequence => new AnalysisSnapshotReceived(sequence, snapshot));
        if (_headlessAnalysisController is not { IsHeadlessActive: true })
        {
            _lastAnalyzerSnapshot = snapshot;
        }
        if (!_overlayMode || _overlayNativePlayStateKnown || snapshot.Gameplay.IsPlaying is not bool isPlaying)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_overlayMode)
            {
                SetOverlaySuppressedByPlay(isPlaying, snapshot.Gameplay.IsPaused);
            }
        });
    }

    private async Task ApplyPresentationAsync()
    {
        if (_model is null)
        {
            return;
        }

        await ApplyPresentationAsync(_model.Settings, _overlayMode, updateFullscreen: true, reportErrors: true, CancellationToken.None);
    }

    private async Task<bool> ApplyPresentationAsync(
        LauncherSettings settings,
        bool presentationOverlayMode,
        bool updateFullscreen,
        bool reportErrors,
        CancellationToken cancellationToken)
    {
        var entered = false;
        var applied = false;
        try
        {
            await _presentationGate.WaitAsync(cancellationToken);
            entered = true;
            var analyzer = _presentation.ResolveAnalyzer(settings.AnalyzerProviderId);
            var scripts = _presentation.Build(settings, presentationOverlayMode);
            await Browser.InvokeScript(scripts.SetupScript);
            await Browser.InvokeScript(scripts.ObserverScript);
            if (presentationOverlayMode && _overlayMode)
            {
                await ApplyOverlayDocumentAppearanceScriptAsync(_overlayWindow.IsMinimized);
            }
            try
            {
                var bridgeCapability = await Browser.InvokeScript(
                    "JSON.stringify({chrome:typeof chrome !== 'undefined',webview:typeof chrome !== 'undefined' && !!chrome.webview,postMessage:typeof chrome !== 'undefined' && !!chrome.webview && typeof chrome.webview.postMessage === 'function',hostRuntime:!!window.__overlayHostRuntime,hostSend:typeof window.__overlayHostSend === 'function',resizeHandles:document.querySelectorAll('.overlay-resize-handle').length,pointerEvents:typeof window.PointerEvent === 'function'})");
                AppLogger.Debug(
                    "Overlay bridge capability",
                    bridgeCapability ?? "The WebView returned no bridge capability.");
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Probing overlay bridge capability", exception.Message, exception);
            }

            try
            {
                await Browser.InvokeScript(
                    "if (typeof chrome !== 'undefined' && chrome.webview && typeof chrome.webview.postMessage === 'function') { chrome.webview.postMessage('overlay:bridge-self-test'); }");
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Invoking overlay bridge self-test", exception.Message, exception);
            }

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
            {
                _fullscreen.WriteRuntime(
                    settings,
                    analyzer.Descriptor,
                    scripts.FullscreenSetupScript,
                    scripts.FullscreenObserverScript);
                SetFullscreenViewStatePresentation(true);
                SubmitCurrentFullscreenViewState();
            }
            else if (updateFullscreen)
            {
                SetFullscreenViewStatePresentation(false);
            }
            applied = true;
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
            {
                _presentationGate.Release();
            }
        }

        return applied;
    }

    private async Task RetryBrowserPresentationAsync()
    {
        // A failed composition-controller attach is transient. Retry a few
        // times without navigating again so the current document keeps its
        // native collector and the latest snapshot can be replayed when the
        // renderer becomes ready.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)));
                if (_isClosing || !ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
                {
                    return;
                }

                if (!await ApplyPresentationAsync(
                        _model?.Settings ?? new LauncherSettings(),
                        _overlayMode,
                        updateFullscreen: true,
                        reportErrors: false,
                        CancellationToken.None))
                {
                    continue;
                }

                SetNativeBrowserReady(true);
                SetNativePresentationVisible(
                    _overlayMode ? _overlayWindowVisible && Opacity > 0 : IsVisible);
                if (_overlayMode)
                {
                    await FitOverlayWindowToRenderedWidgetAsync();
                    ScheduleOverlayLayoutReconciliation();
                }

                return;
            }
            catch (Exception exception)
            {
                AppLogger.Debug("Retrying browser presentation", exception.Message);
            }
        }

        AppLogger.Warning(
            "Retrying browser presentation",
            "The WebView document did not become ready after transient composition failures.");
    }

    private void ApplyOverlayWindowAppearance(bool osuMinimized)
    {
        if (!_overlayMode)
        {
            return;
        }

        // Keep the top-level surface transparent so only the widget card is
        // visible over osu!. The WebView uses the offscreen composition mode
        // requested in CreateBrowser, which keeps its transparent backing
        // surface paintable instead of exposing a solid HWND rectangle.
        IBrush background = Brushes.Transparent;
        Background = background;
        BrowserHost.Background = background;
        Browser.Background = background;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Opacity = _overlayWindowVisible ? GetOverlayOpacity() : 0;

        _ = ApplyOverlayDocumentAppearanceScriptAsync(osuMinimized);
        Dispatcher.UIThread.Post(() =>
        {
            if (_overlayMode && _overlayWindow.IsMinimized == osuMinimized)
            {
                try
                {
                    _windowsOverlay.ReapplyNativeState(_overlayWindowVisible);
                }
                catch (Exception exception)
                {
                    AppLogger.Error("Reapplying overlay native state after osu! window change", exception, userVisible: false);
                }
            }
        });
    }

    private async Task ApplyOverlayDocumentAppearanceScriptAsync(bool osuMinimized)
    {
        if (!_overlayMode)
        {
            return;
        }

        try
        {
            await Browser.InvokeScript(
                "(function(){var root=document.documentElement;root.classList.toggle('launcher-osu-minimized'," +
                (osuMinimized ? "true" : "false") + ");})();");
        }
        catch (Exception exception)
        {
            // Navigation can recreate the WebView between the native state
            // change and this script. ApplyPresentationAsync retries the class
            // after the next successful navigation.
            AppLogger.Debug("Applying minimized overlay document appearance", exception.Message);
        }
    }

    private async Task AdjustScaleAsync(int delta)
    {
        var entered = false;
        try
        {
            await _overlayScaleGate.WaitAsync();
            entered = true;
            _overlayScaleUpdateInProgress = true;
            if (_model is null)
            {
                return;
            }

            var currentPercent = Math.Clamp(_model.Settings.OverlayScalePercent, 50, 180);
            var next = Math.Clamp(currentPercent + delta, 50, 180);
            if (next == currentPercent)
            {
                return;
            }

            _model.Settings.OverlayScalePercent = next;
            _model.SaveSettings();
            UpdatePreviewScaleText();
            if (_overlayMode)
            {
                // Give WebView the target viewport before replacing the
                // presentation scripts. Otherwise CSS zoom is applied while
                // the old viewport is still active and the first layout pass
                // can clip the rightmost column.
                PrepareOverlayClientSizeForScale(currentPercent, next);
            }
            await ApplyPresentationAsync();
            if (_overlayMode)
            {
                await FitOverlayWindowToRenderedWidgetAsync();
                SaveOverlayBounds();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Adjusting overlay scale", exception);
        }
        finally
        {
            _overlayScaleUpdateInProgress = false;
            if (entered)
            {
                _overlayScaleGate.Release();
            }
        }
    }

    private void QueueOverlayScaleAdjustment(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        Interlocked.Add(ref _queuedOverlayScaleDelta, delta);
        if (Interlocked.Exchange(ref _overlayScaleQueueRunning, 1) == 0)
        {
            _ = DrainOverlayScaleAdjustmentsAsync();
        }
    }

    private async Task DrainOverlayScaleAdjustmentsAsync()
    {
        try
        {
            while (_overlayMode)
            {
                var delta = Interlocked.Exchange(ref _queuedOverlayScaleDelta, 0);
                if (delta == 0)
                {
                    break;
                }

                await AdjustScaleAsync(delta);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Applying queued overlay scale", exception);
        }
        finally
        {
            Interlocked.Exchange(ref _overlayScaleQueueRunning, 0);
            if (_overlayMode && Volatile.Read(ref _queuedOverlayScaleDelta) != 0 &&
                Interlocked.Exchange(ref _overlayScaleQueueRunning, 1) == 0)
            {
                _ = DrainOverlayScaleAdjustmentsAsync();
            }
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

            if (_headlessAnalysisController is null)
            {
                _model.SetStatus(L("replay.import.no_beatmap_source"));
                return;
            }

            var beatmap = await _headlessAnalysisController.BeatmapSource.GetCurrentAsync();
            var result = await _replayAnalysisSession.AnalyzeAsync(beatmap);
            var baseSnapshot = _headlessAnalysisController.LastSnapshot;
            if (baseSnapshot is null)
            {
                baseSnapshot = HeadlessSnapshotConverter.FromComposed(
                    beatmap,
                    null,
                    new ComposedWidgetSnapshot("replay-base", AnalysisOutcome.Success, [], []));
            }

            var replaySnapshot = HeadlessSnapshotConverter.WithReplayAnalysis(baseSnapshot, result);
            await _headlessAnalysisController.PushSnapshotAsync(replaySnapshot, CancellationToken.None);
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
        {
            return;
        }

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
            var settingsUri = selectedAnalyzer.GetSettingsUri(_tosuBaseUri);
            if (settingsUri is not null)
            {
                Navigate(settingsUri.ToString());
            }

            return;
        }
        var analyzerChanged = !string.Equals(
            _model.Settings.AnalyzerProviderId,
            dialog.AnalyzerProviderId,
            StringComparison.OrdinalIgnoreCase);
        _model.Settings.AnalyzerProviderId = dialog.AnalyzerProviderId;
        if (analyzerChanged)
        {
            _analyzerCoordinator?.Switch(_model.Settings.AnalyzerProviderId);
        }

        _model.Settings.OverlayLayoutMode = dialog.LayoutMode;
        _model.Settings.OverlayPresetId = dialog.PresetId;
        _model.Settings.OverlayScalePercent = dialog.ScalePercent;
        _model.Settings.OverlayOpacityPercent = dialog.OpacityPercent;
        UpdatePreviewScaleText();
        var restartForFullscreen = false;
        if (_model.Settings.FullscreenOverlayEnabled && !ActiveAnalyzer.Descriptor.SupportsFullscreen)
        {
            if (_fullscreen.IsSupported)
            {
                _fullscreen.SetEnabled(false);
            }

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
        {
            await _model.RestartAsync();
        }

        Navigate(AnalysisUrl);
    }

    private void AppearancePreviewChanged(LauncherSettings previewSettings)
    {
        if (_model is null || !ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
        {
            return;
        }

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

            if (_headlessAnalysisController is null)
            {
                return;
            }

            await _headlessAnalysisController.ReloadConfigurationAsync();
            var widgetCount = _headlessAnalysisController.CurrentConfiguration.Widgets.Length;
            _model?.SetStatus(L("mapping.title") + ": " + widgetCount + " widget(s)");
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
        {
            return;
        }

        SetComponentPreparationState(_componentPreparationFailed);
        SetControlsEnabled(false);
        ShowMessagePage(L("dialog.prepare_tosu.title"), L("dialog.prepare_tosu.message"), false);
        if (!await CheckUpdatesAsync())
        {
            return;
        }

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
        if (running && _headlessAnalysisController is not null)
        {
            Navigate(AnalysisUrl);
            await EnsureHeadlessBrowserReadyAsync();
            await _headlessAnalysisController.RestartAsync();
        }

        if (!running && _headlessAnalysisController is not null)
        {
            await _headlessAnalysisController.NotifyTosuRestartAsync();
        }
    }

    private void LanguageSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_model is null || _updatingLanguageSelector || LanguageSelector.SelectedItem is not LanguageOption selected)
        {
            return;
        }

        UiText.Initialize(selected.Id);
        _model.Settings.Language = UiText.CurrentLanguage;
        _model.SaveSettings();
        ApplyLanguage();
        _model.SetStatus(L(_model.Tosu.IsRunning ? "status.tosu_running" : "status.tosu_not_running"), _model.Tosu.IsRunning);
        if (ActiveAnalyzer.MatchesAnalysisUri(Browser.Source))
        {
            SetNativeBrowserReady(false);
            Browser.Refresh();
        }
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
        {
            return;
        }

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
        CancelOverlayLayoutReconciliation();
        _overlayWidgetSized = false;
        _overlayRenderedBaseHeight = null;
        _overlayPlayStateKnown = false;
        _overlayNativePlayStateKnown = false;
        _lastGameplayTraceBySource.Clear();
        _overlayIsPlaying = false;
        _overlayIsPaused = null;
        // The native collector is presentation-independent and has already
        // accumulated the current attempt in launcher preview. Keep that
        // session across the WebView swap so opening the overlay does not
        // briefly replace a complete window with a one-sample session.
        _lastNativePauseCoachDiagnostic = string.Empty;
        _lastNativePauseCoachDiagnosticAt = default;
        _lastNativePollBoundaryDiagnostic = string.Empty;
        _lastNativePollBoundaryDiagnosticAt = default;
        _overlaySuppressedByPolicy = false;
        _overlayScaleUpdateInProgress = false;
        Interlocked.Exchange(ref _queuedOverlayScaleDelta, 0);
        _overlayVisibilityPolicy = ResolveOverlayVisibilityPolicy();
        PostShadowRuntimeEvent(sequence => new VisibilityPolicyChanged(sequence, _overlayVisibilityPolicy));
        PostShadowRuntimeEvent(sequence => new OverlayModeChanged(sequence, true));
        _overlayInteractive = false;
        CancelOverlayGestures();
        Opacity = 1;
        Toolbar.IsVisible = false;
        RootGrid.RowDefinitions[0].Height = new GridLength(0);
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        // The normal launcher has a much larger minimum size. In overlay
        // editing mode keep the widget's programmatic size range independent
        // of that launcher constraint.
        MinWidth = 120;
        MinHeight = 80;
        Topmost = true;
        // Keep Avalonia from permanently forcing WS_EX_TOOLWINDOW. The native
        // controller adds that style while osu! is protected and removes it in
        // the safe edit state, where an activatable/task-switchable HWND is
        // required for reliable WebView2 pointer input.
        ShowInTaskbar = true;
        RecreateOverlayBrowser();
        ApplyOverlayWindowAppearance(_overlayWindow.IsMinimized);

        var requestedPreset = string.IsNullOrWhiteSpace(_model.Settings.OverlayPresetId) ||
                              (_model.Settings.OverlayPresetId == "default" && _model.Settings.OverlayLayoutMode != "default")
            ? _model.Settings.OverlayLayoutMode
            : _model.Settings.OverlayPresetId;
        var layout = OverlayPresentationService.NormalizeLayout(requestedPreset);
        _overlayUsesAuthoritativeSize = layout != "custom";
        var scale = Math.Clamp(_model.Settings.OverlayScalePercent, 50, 180) / 100d;
        var baseWidth = layout switch
        {
            "horizontal" => 920d,
            "companella" or "companella-replay" => 760d,
            "pause-coach-card" => 620d,
            _ => 475d
        };
        var baseHeight = layout switch
        {
            "horizontal" => 360d,
            "companella" or "companella-replay" => 340d,
            "pause-coach-card" => 300d,
            _ => 540d
        };
        var width = baseWidth * scale;
        var height = baseHeight * scale;
        ClientSize = new Size(width, height);
        _overlayWidgetSized = _overlayUsesAuthoritativeSize;
        var working = Screens.ScreenFromWindow(this)?.WorkingArea ?? Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var savedVisible = _model.Settings.OverlayX > -30000 && _model.Settings.OverlayY > -30000;
        Position = savedVisible
            ? new PixelPoint(_model.Settings.OverlayX, _model.Settings.OverlayY)
            : new PixelPoint(working.Right - (int)Math.Ceiling(width * RenderScaling) - 18, working.Y + 18);
        // Keep the native HWND alive while applying Avalonia chrome/size changes
        // that may recreate it. Synchronize visibility and native state only on
        // the final handle so a hidden window is not lost during recreation.
        _windowsOverlay.Enter();
        // Keep the final HWND visible until a real gameplay state says the
        // selected preset should be hidden. Hiding here can leave a permanently
        // invisible overlay when tosu is unavailable or still starting.
        _windowsOverlay.ReapplyNativeState(visible: true);
        UpdateOverlayVisibility();
        BrowserHost.Child = Browser;
        Navigate(AnalysisUrl);
        // The first WebView2 composition/layout pass can complete after
        // NavigationCompleted (especially while the transparent HWND is being
        // recreated). Keep a small, cancellable reconciliation window so the
        // initial overlay does not depend on a user resize to receive the
        // final card bounds.
        ScheduleOverlayLayoutReconciliation();
        StartOverlayGameplayPolling();
    }

    private void LeaveOverlayMode()
    {
        if (!_overlayMode || _model is null)
        {
            return;
        }

        SaveOverlayBounds();
        CancelOverlayLayoutReconciliation();
        _overlayInteractive = false;
        StopOverlayGameplayPolling();
        CancelOverlayGestures();
        // Mark the Avalonia side as launcher mode before native teardown so
        // controller callbacks from clearing minimized/focus state cannot
        // re-enter overlay visibility or transparency logic.
        _overlayMode = false;
        // Release the native click-through/disabled state before detaching the
        // overlay WebView. Detaching a protected child HWND can otherwise leave
        // the restored launcher top-level HWND disabled until the next native
        // state transition.
        _windowsOverlay.Leave();
        Browser.IsHitTestVisible = true;
        BrowserHost.IsHitTestVisible = true;
        var launcherBackground = new SolidColorBrush(Color.Parse("#0E1016"));
        Background = launcherBackground;
        BrowserHost.Background = launcherBackground;
        // NativeWebView retains its composition adapter across a normal visual
        // detach. Replace the control itself so the launcher cannot inherit the
        // transparent WebView2 adapter used by overlay mode.
        ReplaceBrowser(launcherBackground, offscreen: false);
        Browser.IsHitTestVisible = true;
        BrowserHost.IsHitTestVisible = true;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
        _overlayWidgetSized = false;
        _overlayUsesAuthoritativeSize = false;
        _overlayRenderedBaseHeight = null;
        _overlayPlayStateKnown = false;
        _overlayNativePlayStateKnown = false;
        _lastGameplayTraceBySource.Clear();
        _overlayIsPlaying = false;
        _overlayIsPaused = null;
        // Keep the collector session when returning to the launcher. The
        // next Tosu frame handles map/retry transitions, while resetting here
        // would make the preview briefly show a freshly empty window.
        _lastNativePauseCoachDiagnostic = string.Empty;
        _lastNativePauseCoachDiagnosticAt = default;
        _lastNativePollBoundaryDiagnostic = string.Empty;
        _lastNativePollBoundaryDiagnosticAt = default;
        _overlaySuppressedByPolicy = false;
        _overlayScaleUpdateInProgress = false;
        Interlocked.Exchange(ref _queuedOverlayScaleDelta, 0);
        Volatile.Write(ref _overlayNativeDragPending, 0);
        _overlayVisibilityPolicy = OverlayVisibilityPolicy.Always;
        PostShadowRuntimeEvent(sequence => new VisibilityPolicyChanged(sequence, _overlayVisibilityPolicy));
        PostShadowRuntimeEvent(sequence => new OverlayModeChanged(sequence, false));
        Opacity = 1;
        Toolbar.IsVisible = true;
        RootGrid.RowDefinitions[0].Height = new GridLength(150);
        SystemDecorations = SystemDecorations.Full;
        CanResize = true;
        Topmost = false;
        ShowInTaskbar = true;
        MinWidth = 650;
        MinHeight = 740;
        Position = _normalPosition;
        ClientSize = _normalClientSize;
        SetOverlayWindowVisibility(true);
        // Overlay mode owns the polling lifetime while the native HWND is
        // active. Restart it when returning to the launcher so the preview
        // keeps receiving the same realtime stream without requiring reload.
        if (_model.Tosu.IsRunning)
        {
            StartOverlayGameplayPolling();
        }
        SetNativePresentationVisible(IsVisible);
        Dispatcher.UIThread.Post(() =>
        {
            if (_overlayMode || BrowserHost.Child is not null)
            {
                return;
            }

            try
            {
                BrowserHost.Child = Browser;
                Navigate(AnalysisUrl);
            }
            catch (Exception exception)
            {
                // A WebView recreation must not prevent the native launcher
                // HWND from being restored or focused after osu! exits.
                AppLogger.Error("Restoring launcher WebView", exception, userVisible: false);
            }
            finally
            {
                Browser.IsHitTestVisible = true;
                BrowserHost.IsHitTestVisible = true;
                try
                {
                    // Avalonia may have recreated the HWND while restoring
                    // decorations. Reapply the non-overlay styles to that
                    // final handle, otherwise a stale disabled/click-through
                    // state can survive on the launcher window.
                    _windowsOverlay.ReapplyNativeState(visible: true);
                }
                catch (Exception exception)
                {
                    AppLogger.Error("Restoring launcher native input", exception, userVisible: false);
                }

                try
                {
                    Activate();
                }
                catch (Exception exception)
                {
                    AppLogger.Error("Activating launcher window", exception, userVisible: false);
                }
            }
        });
    }

    private void ResizeOverlayToWidget(int physicalWidth, int physicalHeight)
    {
        if (!_overlayMode || _overlayScaleUpdateInProgress ||
            physicalWidth is < 120 or > 2400 || physicalHeight is < 80 or > 3200)
        {
            return;
        }

        if (_overlayInteractive)
        {
            // In the interactive overlay the size report can briefly contain
            // the previous width while the browser is reflowing. Do not let
            // that stale width change the native window; a report with the
            // current width can still carry a real height change.
            var currentPhysicalWidth = ClientSize.Width * RenderScaling;
            if (Math.Abs(physicalWidth - currentPhysicalWidth) > 8)
            {
                return;
            }

            physicalWidth = (int)Math.Round(currentPhysicalWidth);
        }

        var position = Position;
        var targetSize = new Size(physicalWidth / RenderScaling, physicalHeight / RenderScaling);
        if (_model is not null)
        {
            var scale = Math.Clamp(_model.Settings.OverlayScalePercent, 50, 180) / 100d;
            _overlayRenderedBaseHeight = targetSize.Height / scale;
        }

        const double sizeTolerance = 2.0;
        if (Math.Abs(ClientSize.Width - targetSize.Width) <= sizeTolerance &&
            Math.Abs(ClientSize.Height - targetSize.Height) <= sizeTolerance)
        {
            _overlayWidgetSized = true;
            UpdateOverlayVisibility();
            return;
        }

        ClientSize = targetSize;
        Position = position;
        _overlayWidgetSized = true;
        UpdateOverlayVisibility();
        SaveOverlayBounds();
    }

    private void PrepareOverlayClientSizeForScale(int currentScalePercent, int nextScalePercent)
    {
        if (!_overlayMode || _model is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var currentScale = Math.Clamp(currentScalePercent, 50, 180) / 100d;
        var nextScale = Math.Clamp(nextScalePercent, 50, 180) / 100d;
        var requestedPreset = string.IsNullOrWhiteSpace(_model.Settings.OverlayPresetId) ||
                              (_model.Settings.OverlayPresetId == "default" && _model.Settings.OverlayLayoutMode != "default")
            ? _model.Settings.OverlayLayoutMode
            : _model.Settings.OverlayPresetId;
        var layout = OverlayPresentationService.NormalizeLayout(requestedPreset);
        var baseWidth = layout switch
        {
            "horizontal" => 920d,
            "companella" or "companella-replay" => 760d,
            "pause-coach-card" => 620d,
            "default" => 475d,
            _ => ClientSize.Width / currentScale
        };
        var baseHeight = _overlayRenderedBaseHeight ?? ClientSize.Height / currentScale;
        var targetSize = new Size(
            Math.Ceiling(baseWidth * nextScale),
            Math.Ceiling(baseHeight * nextScale));
        const double sizeTolerance = 2.0;
        if (Math.Abs(ClientSize.Width - targetSize.Width) <= sizeTolerance &&
            Math.Abs(ClientSize.Height - targetSize.Height) <= sizeTolerance)
        {
            return;
        }

        var position = Position;
        ClientSize = targetSize;
        Position = position;
    }

    private void ScheduleOverlayLayoutReconciliation()
    {
        if (!_overlayMode || _isClosing)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(
            ref _overlayLayoutReconciliationCancellation,
            cancellation);
        previous?.Cancel();
        var generation = Interlocked.Increment(ref _overlayLayoutReconciliationGeneration);
        _ = ReconcileOverlayLayoutAsync(cancellation, generation);
    }

    private void CancelOverlayLayoutReconciliation()
    {
        Interlocked.Increment(ref _overlayLayoutReconciliationGeneration);
        var cancellation = Interlocked.Exchange(
            ref _overlayLayoutReconciliationCancellation,
            null);
        cancellation?.Cancel();
    }

    private async Task ReconcileOverlayLayoutAsync(
        CancellationTokenSource cancellation,
        long generation)
    {
        try
        {
            // Use a few settled compositor frames instead of resizing on every
            // telemetry update. The later passes cover slow WebView2 startup
            // and the native HWND recreation that can happen on overlay entry.
            foreach (var delay in new[] { 120, 420, 900, 1_600 })
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellation.Token);
                if (cancellation.IsCancellationRequested ||
                    generation != Volatile.Read(ref _overlayLayoutReconciliationGeneration) ||
                    !_overlayMode ||
                    _isClosing)
                {
                    return;
                }

                await FitOverlayWindowToRenderedWidgetAsync();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Expected when leaving/re-entering overlay or replacing its WebView.
        }
        catch (Exception exception)
        {
            AppLogger.Debug("Reconciling initial overlay layout", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(
                    Volatile.Read(ref _overlayLayoutReconciliationCancellation),
                    cancellation))
            {
                Interlocked.CompareExchange(
                    ref _overlayLayoutReconciliationCancellation,
                    null,
                    cancellation);
            }

            cancellation.Dispose();
        }
    }

    private async Task FitOverlayWindowToRenderedWidgetAsync()
    {
        if (!_overlayMode || _model is null)
        {
            return;
        }

        var entered = false;
        try
        {
            await _presentationGate.WaitAsync();
            entered = true;
            // A scale change can leave the browser one layout frame behind the
            // Avalonia SizeChanged event. Force a reflow and measure twice: the
            // second pass observes the final width after the first ClientSize
            // update, preventing the rightmost widget from being clipped.
            for (var pass = 0; pass < 2; pass++)
            {
                await Browser.InvokeScript("window.dispatchEvent(new Event('resize'));");
                await Task.Delay(pass == 0 ? 50 : 40);
                var result = await Browser.InvokeScript(
                    "(function(){var card=document.querySelector('[data-overlay-host-root]');" +
                    "if(!card)return null;var r=card.getBoundingClientRect();" +
                    "return JSON.stringify({width:r.width,height:r.height});})()");
                if (!TryReadRenderedOverlaySize(result, out var targetSize))
                {
                    return;
                }

                var scale = Math.Clamp(_model.Settings.OverlayScalePercent, 50, 180) / 100d;
                _overlayRenderedBaseHeight = targetSize.Height / scale;
                var position = Position;
                const double sizeTolerance = 2.0;
                if (Math.Abs(ClientSize.Width - targetSize.Width) > sizeTolerance ||
                    Math.Abs(ClientSize.Height - targetSize.Height) > sizeTolerance)
                {
                    ClientSize = targetSize;
                    Position = position;
                }

                _overlayWidgetSized = true;
                UpdateOverlayVisibility();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Fitting overlay window to rendered widget", exception, userVisible: false);
        }
        finally
        {
            if (entered)
            {
                _presentationGate.Release();
            }
        }
    }

    private static bool TryReadRenderedOverlaySize(string? json, out Size size)
    {
        size = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var outer = JsonDocument.Parse(json);
            if (outer.RootElement.ValueKind == JsonValueKind.String)
            {
                var innerJson = outer.RootElement.GetString();
                if (string.IsNullOrWhiteSpace(innerJson))
                {
                    return false;
                }

                using var inner = JsonDocument.Parse(innerJson);
                return TryReadRenderedOverlaySize(inner.RootElement, out size);
            }

            return TryReadRenderedOverlaySize(outer.RootElement, out size);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadRenderedOverlaySize(JsonElement root, out Size size)
    {
        size = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("width", out var widthElement) ||
            !root.TryGetProperty("height", out var heightElement) ||
            !widthElement.TryGetDouble(out var width) ||
            !heightElement.TryGetDouble(out var height) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            width is < 120 or > 2400 || height is < 80 or > 3200)
        {
            return false;
        }

        size = new Size(Math.Ceiling(width), Math.Ceiling(height));
        return true;
    }

    private static bool IsCloseToPhysicalWidth(int actual, int expected) => Math.Abs(actual - expected) <= 3;

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
        {
            LogOverlayGameplayState(visibilityPolicy, isPlaying, isPaused);
        }
    }

    private void StartOverlayGameplayPolling()
    {
        StopOverlayGameplayPolling();
        if (_model is null)
        {
            LogNativePollBoundary("not-started:model-null");
            return;
        }

        LogNativePollBoundary("started");
        var pollingCancellation = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _overlayGameplayPollGeneration);
        _overlayGameplayPollCancellation = pollingCancellation;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => _ = PollOverlayGameplayStateAsync(generation, pollingCancellation);
        _overlayGameplayPollTimer = timer;
        timer.Start();
        _ = PollOverlayGameplayStateAsync(generation, pollingCancellation);
    }

    private void StopOverlayGameplayPolling()
    {
        Interlocked.Increment(ref _overlayGameplayPollGeneration);
        var timer = _overlayGameplayPollTimer;
        _overlayGameplayPollTimer = null;
        timer?.Stop();
        var pollingCancellation = _overlayGameplayPollCancellation;
        _overlayGameplayPollCancellation = null;
        pollingCancellation?.Cancel();
        pollingCancellation?.Dispose();
    }

    private async Task PollOverlayGameplayStateAsync(long generation, CancellationTokenSource pollingCancellation)
    {
        if (_model is null || !IsActiveOverlayGameplayPoll(generation, pollingCancellation) ||
            Interlocked.Exchange(ref _overlayGameplayPollInFlight, 1) != 0)
        {
            return;
        }

        var cancellationToken = pollingCancellation.Token;
        try
        {
            TosuRealtimeTelemetry? telemetry = await _overlayRealtimeSource.ReadAsync(cancellationToken);
            if (telemetry is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // The request may finish after overlay exit/re-entry. Do
                    // not deliver a stale response into the new WebView
                    // session, but do feed both visible launcher and overlay
                    // presentations from the same collector.
                    if (!cancellationToken.IsCancellationRequested &&
                        IsActiveOverlayGameplayPoll(generation, pollingCancellation))
                    {
                        ApplyNativeRealtimeTelemetry(telemetry);
                    }
                });
            }
            else
            {
                LogNativePollBoundary("payload-null-or-normalization-failed");
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

    private bool IsActiveOverlayGameplayPoll(long generation, CancellationTokenSource pollingCancellation) =>
        generation == Volatile.Read(ref _overlayGameplayPollGeneration) &&
        ReferenceEquals(_overlayGameplayPollCancellation, pollingCancellation) &&
        !pollingCancellation.IsCancellationRequested;

    private void LogNativePollBoundary(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(reason, _lastNativePollBoundaryDiagnostic, StringComparison.Ordinal) &&
            now - _lastNativePollBoundaryDiagnosticAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastNativePollBoundaryDiagnostic = reason;
        _lastNativePollBoundaryDiagnosticAt = now;
        AppLogger.Info(
            "PauseCoach native polling",
            $"source=native-http; boundary={reason}; overlayMode={_overlayMode}; modelReady={_model is not null}; " +
            $"pollInFlight={Volatile.Read(ref _overlayGameplayPollInFlight)}; cancellation={_overlayGameplayPollCancellation?.IsCancellationRequested ?? false}");
    }

    private void PostShadowRuntimeEvent(Func<long, OverlayRuntimeEvent> createEvent)
    {
        _runtimeCoordinator.TryPost(createEvent);
    }

    private void BeginNativePresentationSession()
    {
        _nativePauseCoachPublisher.BeginPresentationSession();
        bool changed = _shadowPresentationReady || _shadowPresentationVisible;
        _shadowPresentationReady = false;
        _shadowPresentationVisible = false;
        if (changed)
        {
            PostShadowRuntimeEvent(sequence => new PresentationAvailabilityChanged(sequence, false, false));
        }
    }

    private void SetNativeBrowserReady(bool ready)
    {
        _nativePauseCoachPublisher.SetBrowserReady(ready);
        if (_shadowPresentationReady == ready)
        {
            return;
        }

        _shadowPresentationReady = ready;
        PostShadowRuntimeEvent(sequence => new PresentationAvailabilityChanged(
            sequence,
            _shadowPresentationReady,
            _shadowPresentationVisible));
    }

    private void SetNativePresentationVisible(bool visible)
    {
        _nativePauseCoachPublisher.SetPresentationVisible(visible);
        if (_shadowPresentationVisible == visible)
        {
            return;
        }

        _shadowPresentationVisible = visible;
        PostShadowRuntimeEvent(sequence => new PresentationAvailabilityChanged(
            sequence,
            _shadowPresentationReady,
            _shadowPresentationVisible));
    }

    private void ShadowRuntimeCoordinator_TransitionApplied(
        object? sender,
        OverlayRuntimeTransition transition)
    {
        if (!transition.Accepted)
        {
            return;
        }

        OverlayRuntimeState previous = transition.Previous;
        OverlayRuntimeState next = transition.Next;
        bool important = previous.GameplayState != next.GameplayState
            || !string.Equals(previous.BeatmapId, next.BeatmapId, StringComparison.Ordinal)
            || !string.Equals(previous.SessionId, next.SessionId, StringComparison.Ordinal)
            || previous.OverlayMode != next.OverlayMode
            || !string.Equals(previous.VisibilityPolicy, next.VisibilityPolicy, StringComparison.Ordinal)
            || previous.OsuWindowMinimized != next.OsuWindowMinimized
            || previous.PresentationReady != next.PresentationReady
            || previous.PresentationVisible != next.PresentationVisible;
        bool affectsVisibility = transition.Event is RealtimeTelemetryReceived
            or OverlayModeChanged
            or OsuWindowStateChanged
            or VisibilityPolicyChanged;
        // The coordinator is the authoritative native realtime boundary.
        // Submit the reducer's accepted snapshot before posting the UI
        // visibility effect so a paused/results frame is the first frame
        // flushed when a hidden overlay becomes visible.
        if (important)
        {
            Dispatcher.UIThread.Post(() => ApplyCoordinatorRuntimeState(next, affectsVisibility));
        }

        if (!important)
        {
            return;
        }

        RealtimeAnalysisSnapshot? realtime = next.LatestRealtime;
        string signature = $"{transition.Event.GetType().Name}|{next.GameplayState}|{next.BeatmapId}|{next.SessionId}|{next.OverlayMode}|{next.OsuWindowMinimized}|{next.PresentationReady}|{next.PresentationVisible}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.Equals(signature, _lastShadowRuntimeDiagnostic, StringComparison.Ordinal)
            && now - _lastShadowRuntimeDiagnosticAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastShadowRuntimeDiagnostic = signature;
        _lastShadowRuntimeDiagnosticAt = now;
        AppLogger.Debug(
            "Overlay runtime shadow transition",
            $"eventType={transition.Event.GetType().Name}; accepted=true; runtimeVersion={next.Version}; "
            + $"beatmapId={next.BeatmapId}; attemptId={next.SessionId}; mapTimeMs={realtime?.MapTimeMs}; "
            + $"gameplayState={next.GameplayState}; isPlaying={next.IsPlaying}; isPaused={next.IsPaused}; "
            + $"overlayMode={next.OverlayMode}; osuWindowMinimized={next.OsuWindowMinimized}; "
            + $"presentationReady={next.PresentationReady}; presentationVisible={next.PresentationVisible}; "
            + $"source={next.LastRealtimeSource}");
        Dispatcher.UIThread.Post(() => CompareShadowRuntimeWithLegacy(
            next,
            includePresentation: transition.Event is PresentationAvailabilityChanged));
    }

    private void RuntimeCoordinator_ViewStateChanged(
        object? sender,
        OverlayViewStateChangedEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _nativePauseCoachPublisher.Submit(e.ViewState);
        _fullscreenViewStatePublisher.Submit(e.ViewState);
    }

    private void ApplyCoordinatorRuntimeState(OverlayRuntimeState runtime, bool applyVisibility)
    {
        if (_isClosing)
        {
            return;
        }

        _overlayVisibilityPolicy = runtime.VisibilityPolicy;
        if (runtime.GameplayStateKnown)
        {
            _overlayPlayStateKnown = true;
            _overlayIsPlaying = runtime.IsPlaying;
            _overlayIsPaused = runtime.IsPaused;
            _overlaySuppressedByPolicy = !OverlayVisibilityPolicy.ShouldShow(
                runtime.VisibilityPolicy,
                runtime.IsPlaying,
                runtime.IsPaused);
            if (runtime.LatestRealtime is not null)
            {
                _overlayNativePlayStateKnown = true;
            }
        }

        if (!applyVisibility || !_overlayMode || !runtime.OverlayMode)
        {
            return;
        }

        SetOverlayWindowVisibility(OverlayVisibilityDerivation.ShouldShowNativeOverlay(runtime));
    }

    private void CompareShadowRuntimeWithLegacy(OverlayRuntimeState shadow, bool includePresentation)
    {
        if (_isClosing)
        {
            return;
        }

        var legacy = new OverlayRuntimeLegacyProjection(
            _overlayMode,
            _overlayPlayStateKnown,
            _overlayIsPlaying,
            _overlayIsPaused,
            _overlayVisibilityPolicy,
            _overlayWindow.IsMinimized,
            _shadowPresentationReady,
            _shadowPresentationVisible,
            _overlayWindowVisible);
        OverlayRuntimeParityResult parity = OverlayRuntimeParityComparer.Compare(shadow, legacy, includePresentation);
        if (parity.IsMatch)
        {
            return;
        }

        string signature = string.Join('|', parity.Differences);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.Equals(signature, _lastShadowRuntimeMismatch, StringComparison.Ordinal)
            && now - _lastShadowRuntimeMismatchAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastShadowRuntimeMismatch = signature;
        _lastShadowRuntimeMismatchAt = now;
        AppLogger.Warning(
            "Overlay runtime shadow mismatch",
            $"runtimeVersion={shadow.Version}; eventSequence={shadow.LastEventSequence}; differences={signature}");
    }

    private void ApplyNativeRealtimeTelemetry(TosuRealtimeTelemetry telemetry)
    {
        PostShadowRuntimeEvent(sequence => new RealtimeTelemetryReceived(sequence, telemetry));
        _overlayNativePlayStateKnown = true;
        bool? isPlaying = telemetry.Snapshot.State switch
        {
            RealtimePlayState.Playing or RealtimePlayState.Paused => true,
            RealtimePlayState.Menu or RealtimePlayState.Results or RealtimePlayState.Replay or RealtimePlayState.Spectating => false,
            _ => null
        };
        bool? isPaused = telemetry.Snapshot.State == RealtimePlayState.Paused
            ? true
            : telemetry.Snapshot.State == RealtimePlayState.Playing
                ? false
                : null;
        TraceGameplayState("native-http", telemetry.RawStateName, telemetry.RawStateNumber, isPlaying, isPaused, telemetry.Sample.Focused);
        LogNativePauseCoachTelemetry(telemetry);
    }

    private void LogNativePauseCoachTelemetry(TosuRealtimeTelemetry telemetry)
    {
        var snapshot = telemetry.Snapshot;
        bool? normalizedIsPlaying = snapshot.State switch
        {
            RealtimePlayState.Playing or RealtimePlayState.Paused => true,
            RealtimePlayState.Menu or RealtimePlayState.Results or RealtimePlayState.Replay or RealtimePlayState.Spectating => false,
            _ => null
        };
        bool? normalizedIsPaused = snapshot.State switch
        {
            RealtimePlayState.Paused => true,
            RealtimePlayState.Playing => false,
            _ => null
        };
        string signature = string.Join(
            '|',
            telemetry.RawStateName,
            telemetry.RawStateNumber?.ToString(CultureInfo.InvariantCulture) ?? "null",
            telemetry.RawPaused?.ToString() ?? "null",
            snapshot.State,
            snapshot.SessionId,
            snapshot.WidgetState,
            snapshot.BeatmapId);
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(signature, _lastNativePauseCoachDiagnostic, StringComparison.Ordinal) &&
            now - _lastNativePauseCoachDiagnosticAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastNativePauseCoachDiagnostic = signature;
        _lastNativePauseCoachDiagnosticAt = now;
        AppLogger.Info(
            "PauseCoach telemetry",
            $"source=native-http; rawState={telemetry.RawStateName}; number={telemetry.RawStateNumber?.ToString(CultureInfo.InvariantCulture) ?? "null"}; " +
            $"paused={telemetry.RawPaused?.ToString() ?? "null"}; normalized={snapshot.State}; " +
            $"normalizedIsPlaying={normalizedIsPlaying?.ToString() ?? "null"}; normalizedIsPaused={normalizedIsPaused?.ToString() ?? "null"}; " +
            $"map={snapshot.BeatmapId}; mapTime={snapshot.MapTimeMs}; score={snapshot.Score?.ToString(CultureInfo.InvariantCulture) ?? "null"}; " +
            $"accuracy={snapshot.Accuracy?.ToString(CultureInfo.InvariantCulture) ?? "null"}; " +
            $"hits={telemetry.JudgementTotal}; timingSamples={telemetry.HitErrorSampleCount}; " +
            $"session={snapshot.SessionId}; coach={snapshot.WidgetState}");
    }

    private Task PublishNativePauseCoachSnapshotToBrowserAsync(OverlayViewState viewState)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return PublishNativePauseCoachSnapshotToBrowserCoreAsync(viewState);
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await PublishNativePauseCoachSnapshotToBrowserCoreAsync(viewState);
                completion.TrySetResult(null);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private Task WriteFullscreenViewStateAsync(OverlayViewState viewState)
    {
        _fullscreen.WriteViewState(viewState);
        return Task.CompletedTask;
    }

    private void SetFullscreenViewStatePresentation(bool enabled)
    {
        if (!enabled)
        {
            _fullscreenViewStatePublisher.SetPresentationVisible(false);
            _fullscreenViewStatePublisher.SetBrowserReady(false);
            _fullscreen.ClearViewState();
            return;
        }

        _fullscreenViewStatePublisher.BeginPresentationSession();
        _fullscreenViewStatePublisher.SetBrowserReady(true);
        _fullscreenViewStatePublisher.SetPresentationVisible(true);
    }

    private void SubmitCurrentFullscreenViewState()
    {
        OverlayRuntimeState runtime = _runtimeCoordinator.Current;
        if (runtime.LatestRealtime is null && runtime.LatestAnalysis is null)
        {
            return;
        }

        _fullscreenViewStatePublisher.Submit(OverlayViewStateComposer.Compose(runtime));
    }

    private async Task PublishNativePauseCoachSnapshotToBrowserCoreAsync(OverlayViewState viewState)
    {
        var browser = Browser;
        string json = JsonSerializer.Serialize(viewState, _overlaySnapshotJsonOptions);
        // The renderer handles the event and performs one render. Calling its
        // exported function as well would rebuild the DOM twice and cause
        // visible jitter. The publisher guarantees this is the newest frame
        // for the current browser/document session.
        string script = "window.dispatchEvent(new CustomEvent('overlay:view-state',{detail:" + json + "}));";
        await browser.InvokeScript(script).ConfigureAwait(true);
    }

    private void UpdateOverlayVisibility()
    {
        if (!_overlayMode)
        {
            return;
        }

        OverlayRuntimeState runtime = _runtimeCoordinator.Current;
        if (runtime.OverlayMode && runtime.GameplayStateKnown)
        {
            SetOverlayWindowVisibility(OverlayVisibilityDerivation.ShouldShowNativeOverlay(runtime));
            return;
        }

        // A size report is an optimization for synchronizing the native
        // window bounds, not a prerequisite for visibility. If WebView has
        // not reported its first measurement yet, the saved/default client
        // size is still a valid widget surface and must be shown in menu.
        var visible = _overlayPlayStateKnown
            ? OverlayVisibilityPolicy.ShouldShow(
                _overlayVisibilityPolicy,
                _overlayIsPlaying,
                _overlayIsPaused,
                _overlayWindow.IsMinimized)
            : _overlayWindow.IsMinimized || OverlayVisibilityPolicy.ShouldShowBeforeGameplayStateIsKnown(_overlayVisibilityPolicy);
        SetOverlayWindowVisibility(visible);
    }

    private void SetOverlayWindowVisibility(bool visible)
    {
        // The cached value is only a requested state.  It starts before the
        // native HWND exists and can also become stale when Avalonia or
        // Windows hides/shows the top-level window.  Skipping the native call
        // based on that cache can leave the launcher HWND permanently hidden
        // when entering overlay mode.
        var actualVisible = OperatingSystem.IsWindows()
            ? _overlayWindow.IsVisible
            : IsVisible;
        var expectedOpacity = visible ? GetOverlayOpacity() : 0d;
        if (_overlayWindowVisible == visible && actualVisible == visible && Math.Abs(Opacity - expectedOpacity) < 0.001)
        {
            SetNativePresentationVisible(_overlayMode && visible);
            return;
        }

        var previousOpacity = Opacity;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _overlayWindow.SetVisible(visible);
            }
            else if (visible)
            {
                Show();
            }
            else
            {
                Hide();
            }

            Opacity = visible ? GetOverlayOpacity() : 0;
            _overlayWindowVisible = visible;
            SetNativePresentationVisible(_overlayMode && visible);
        }
        catch (Exception exception)
        {
            AppLogger.Error(
                visible ? "Showing overlay window" : "Hiding overlay window",
                exception);

            // Opacity is only mutated after a successful native sync. On failure
            // keep opacity and the cached requested state coherent with the
            // actual native visibility so the next request retries correctly.
            try
            {
                var nativeVisible = OperatingSystem.IsWindows()
                    ? _overlayWindow.IsVisible
                    : IsVisible;
                Opacity = nativeVisible ? GetOverlayOpacity() : 0;
                _overlayWindowVisible = nativeVisible;
                SetNativePresentationVisible(_overlayMode && nativeVisible);
            }
            catch
            {
                Opacity = previousOpacity;
            }
        }
    }

    private double GetOverlayOpacity() =>
        !_overlayMode
            ? 1d
            : Math.Clamp(_model?.Settings.OverlayOpacityPercent ?? 100, 10, 100) / 100d;

    private void LogOverlayGameplayState(string visibilityPolicy, bool isPlaying, bool? isPaused)
    {
        var nativeVisible = OperatingSystem.IsWindows()
            ? _overlayWindow.IsVisible
            : IsVisible;
        AppLogger.Info(
            "Overlay gameplay state",
            $"visibilityPolicy={visibilityPolicy}; " +
            $"isPlaying={isPlaying}; paused={isPaused?.ToString() ?? "null"}; " +
            $"osuMinimized={_overlayWindow.IsMinimized}; " +
            $"requestedVisible={_overlayWindowVisible}; " +
            $"nativeVisible={nativeVisible}; opacity={Opacity:0.##}");
    }

    private string ResolveOverlayVisibilityPolicy()
    {
        if (_model is null)
        {
            return OverlayVisibilityPolicy.Always;
        }

        var requestedPreset = string.IsNullOrWhiteSpace(_model.Settings.OverlayPresetId) ||
                              (_model.Settings.OverlayPresetId == "default" && _model.Settings.OverlayLayoutMode != "default")
            ? _model.Settings.OverlayLayoutMode
            : _model.Settings.OverlayPresetId;
        var requestedLayout = OverlayPresentationService.NormalizeLayout(requestedPreset);
        var effectivePresetId = requestedLayout == "custom" ? requestedPreset : requestedLayout;
        return OverlayVisibilityPolicy.Normalize(_presetCatalog.Get(effectivePresetId).VisibilityPolicy);
    }

    private void SaveOverlayBounds()
    {
        if (!_overlayMode || _model is null)
        {
            return;
        }

        _model.Settings.OverlayX = Position.X;
        _model.Settings.OverlayY = Position.Y;
        _model.Settings.OverlayWidth = (int)Math.Ceiling(ClientSize.Width * RenderScaling);
        _model.Settings.OverlayHeight = (int)Math.Ceiling(ClientSize.Height * RenderScaling);
        _model.SaveSettings();
    }

    private async void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        if (_model is null || !_fullscreen.IsSupported || !ActiveAnalyzer.Descriptor.SupportsFullscreen)
        {
            return;
        }

        var enable = !_fullscreen.ReadEnabled(_model.Settings.FullscreenOverlayEnabled);
        var confirmed = await ConfirmAsync(L("dialog.fullscreen.title"),
            enable
                ? L("dialog.fullscreen.enable")
                : L("dialog.fullscreen.disable"));
        if (!confirmed)
        {
            return;
        }

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
            {
                Navigate(AnalysisUrl);
            }
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
