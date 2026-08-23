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
    private readonly DispatcherTimer _overlayGameplayPollTimer;
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly SemaphoreSlim _overlayScaleGate = new(1, 1);
    private readonly AnalyzerEngineCatalog _analyzerEngineCatalog = new();
    private readonly AnalyzerEnginePackageDeployer _analyzerEngineDeployer = new();
    private readonly EffectiveAnalysisConfigurationStore _effectiveAnalysisStore = new();
    private readonly ReplayAnalysisSession _replayAnalysisSession = new();
    private readonly OverlayDragSession _overlayDragSession = new();
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
    private bool _componentPreparationFailed;
    private bool _updatingLanguageSelector;
    private readonly Dictionary<string, string> _lastGameplayTraceBySource = new(StringComparer.OrdinalIgnoreCase);
    private bool _showingLoggedError;
    private bool _overlayWindowVisible = true;
    private PixelPoint _normalPosition;
    private Size _normalClientSize;

    public MainWindow()
    {
        AppLogger.ErrorRaised += AppLogger_ErrorRaised;
        InitializeComponent();
        Browser = CreateBrowser(new SolidColorBrush(Color.Parse("#0E1016")));
        BrowserHost.Child = Browser;
        _presentation = new OverlayPresentationService(_presetCatalog, _analyzerCatalog);
        _windowsOverlay = new WindowsOverlayController(this);
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
        _windowsOverlay.OsuWindowMinimizedChanged += minimized =>
        {
            if (_overlayMode)
            {
                ApplyOverlayWindowAppearance(minimized);
                UpdateOverlayVisibility();
            }
        };
        _overlayGameplayPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _overlayGameplayPollTimer.Tick += OverlayGameplayPollTimer_Tick;
        Deactivated += (_, _) => CancelOverlayGestures();
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

        _model?.Dispose();
        base.OnClosed(e);
    }

    private NativeWebView CreateBrowser(IBrush background)
    {
        var browser = new NativeWebView { Background = background };
        // A WebView2 child HWND cannot be composed reliably into a transparent,
        // click-through top-level window.  Keep the browser in Avalonia's surface
        // instead, so the overlay remains visible and can switch hit testing
        // without exposing the desktop through the whole window.
        browser.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs webView2)
            {
                webView2.ExperimentalOffscreen = true;
            }
        };
        browser.NavigationCompleted += Browser_NavigationCompleted;
        browser.WebMessageReceived += Browser_WebMessageReceived;
        browser.NewWindowRequested += Browser_NewWindowRequested;
        return browser;
    }

    private void ReplaceBrowser(IBrush background)
    {
        var previous = Browser;
        previous.NavigationCompleted -= Browser_NavigationCompleted;
        previous.WebMessageReceived -= Browser_WebMessageReceived;
        previous.NewWindowRequested -= Browser_NewWindowRequested;
        BrowserHost.Child = null;
        Browser = CreateBrowser(background);
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
            var scriptHostFactory = () => new WebViewAnalyzerScriptHost(() => Browser);
            // Resolve the current control for every snapshot. Leaving overlay
            // mode recreates the NativeWebView, so a presenter that captured
            // the detached overlay instance can otherwise keep flooding the
            // UI queue with InvokeScript failures while the launcher is being
            // restored.
            var presenter = new WebViewAnalysisSnapshotPresenter(() => Browser);

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
            await WaitForAnalysisWebViewReadyAsync();
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
                if (_headlessAnalysisController is not null)
                {
                    _ = _headlessAnalysisController.NotifyTosuRestartAsync();
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
                if (_overlayMode)
                {
                    await FitOverlayWindowToRenderedWidgetAsync();
                }
            }

            if (_headlessAnalysisController is not null)
            {
                var status = _headlessAnalysisController.CurrentState.Status;
                if (status == AnalyzerEngineSupervisorStatus.Ready ||
                    status == AnalyzerEngineSupervisorStatus.Fallback ||
                    status == AnalyzerEngineSupervisorStatus.Error)
                {
                    await _headlessAnalysisController.NotifyNavigationAsync();
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
            if (presentationOverlayMode && _overlayMode)
            {
                await ApplyOverlayDocumentAppearanceScriptAsync(_windowsOverlay.IsOsuMinimized);
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
            }
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
            if (_overlayMode && _windowsOverlay.IsOsuMinimized == osuMinimized)
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
            await WaitForAnalysisWebViewReadyAsync();
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
        _overlayWidgetSized = false;
        _overlayRenderedBaseHeight = null;
        _overlayPlayStateKnown = false;
        _overlayNativePlayStateKnown = false;
        _lastGameplayTraceBySource.Clear();
        _overlayIsPlaying = false;
        _overlayIsPaused = null;
        _overlaySuppressedByPolicy = false;
        _overlayScaleUpdateInProgress = false;
        Interlocked.Exchange(ref _queuedOverlayScaleDelta, 0);
        _overlayVisibilityPolicy = ResolveOverlayVisibilityPolicy();
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
        ApplyOverlayWindowAppearance(_windowsOverlay.IsOsuMinimized);

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
            "pause-coach-minimal" => 560d,
            "pause-coach-signal" => 680d,
            _ => 475d
        };
        var baseHeight = layout switch
        {
            "horizontal" => 360d,
            "companella" or "companella-replay" => 340d,
            "pause-coach-card" => 300d,
            "pause-coach-minimal" => 180d,
            "pause-coach-signal" => 270d,
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
        Navigate(AnalysisUrl);
        StartOverlayGameplayPolling();
    }

    private void LeaveOverlayMode()
    {
        if (!_overlayMode || _model is null)
        {
            return;
        }

        SaveOverlayBounds();
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
        ReplaceBrowser(launcherBackground);
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
        _overlaySuppressedByPolicy = false;
        _overlayScaleUpdateInProgress = false;
        Interlocked.Exchange(ref _queuedOverlayScaleDelta, 0);
        Volatile.Write(ref _overlayNativeDragPending, 0);
        _overlayVisibilityPolicy = OverlayVisibilityPolicy.Always;
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

        if (Math.Abs(ClientSize.Width - targetSize.Width) <= 0.5 &&
            Math.Abs(ClientSize.Height - targetSize.Height) <= 0.5)
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
            "pause-coach-minimal" => 560d,
            "pause-coach-signal" => 680d,
            "default" => 475d,
            _ => ClientSize.Width / currentScale
        };
        var baseHeight = _overlayRenderedBaseHeight ?? ClientSize.Height / currentScale;
        var targetSize = new Size(
            Math.Ceiling(baseWidth * nextScale),
            Math.Ceiling(baseHeight * nextScale));
        if (Math.Abs(ClientSize.Width - targetSize.Width) < 0.5 &&
            Math.Abs(ClientSize.Height - targetSize.Height) < 0.5)
        {
            return;
        }

        var position = Position;
        ClientSize = targetSize;
        Position = position;
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
                if (Math.Abs(ClientSize.Width - targetSize.Width) > 0.5 ||
                    Math.Abs(ClientSize.Height - targetSize.Height) > 0.5)
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
            return;
        }

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
        {
            return;
        }

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
                        {
                            SetOverlaySuppressedByPlay(isPlaying, state.IsPaused);
                        }

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
        {
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
                _windowsOverlay.IsOsuMinimized)
            : _windowsOverlay.IsOsuMinimized || OverlayVisibilityPolicy.ShouldShowBeforeGameplayStateIsKnown(_overlayVisibilityPolicy);
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
            ? _windowsOverlay.IsWindowShown
            : IsVisible;
        var expectedOpacity = visible ? GetOverlayOpacity() : 0d;
        if (_overlayWindowVisible == visible && actualVisible == visible && Math.Abs(Opacity - expectedOpacity) < 0.001)
        {
            return;
        }

        var previousOpacity = Opacity;
        try
        {
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

            Opacity = visible ? GetOverlayOpacity() : 0;
            _overlayWindowVisible = visible;
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
                    ? _windowsOverlay.IsWindowShown
                    : IsVisible;
                Opacity = nativeVisible ? GetOverlayOpacity() : 0;
                _overlayWindowVisible = nativeVisible;
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
            ? _windowsOverlay.IsWindowShown
            : IsVisible;
        AppLogger.Info(
            "Overlay gameplay state",
            $"visibilityPolicy={visibilityPolicy}; " +
            $"isPlaying={isPlaying}; paused={isPaused?.ToString() ?? "null"}; " +
            $"osuMinimized={_windowsOverlay.IsOsuMinimized}; " +
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
        return OverlayVisibilityPolicy.Normalize(_presetCatalog.Get(requestedPreset).VisibilityPolicy);
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
