using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ManiaMapAnalyzerOverlay.Avalonia.Platform;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Avalonia.ViewModels;

namespace ManiaMapAnalyzerOverlay.Avalonia.Views;

public partial class MainWindow : Window
{
    private const string BaseUrl = "http://127.0.0.1:24050";
    private const string OverlayUrl = BaseUrl + "/ManiaMapAnalyser/?launcher=4";
    private const string DesignUrl = BaseUrl + "/settings?overlay=ManiaMapAnalyser";
    private const string FullscreenEditorUrl = BaseUrl + "/api/ingame?edit=true";

    private readonly OverlayPresentationService presentation = new();
    private readonly FullscreenOverlayService fullscreen = new();
    private readonly UpdateService updates = new();
    private readonly WindowsOverlayController windowsOverlay;
    private MainViewModel? model;
    private bool initialized;
    private bool overlayMode;
    private bool overlayWidgetSized;
    private bool overlayPlayStateKnown;
    private bool overlaySuppressedByPlay;
    private bool overlayInputBeforePlay;
    private PixelPoint normalPosition;
    private Size normalClientSize;

    public MainWindow()
    {
        InitializeComponent();
        windowsOverlay = new WindowsOverlayController(this);
        windowsOverlay.ExitRequested += (_, _) => LeaveOverlayMode();
        windowsOverlay.ClickThroughChanged += enabled => Browser.IsHitTestVisible = !enabled;
        Opened += async (_, _) => await InitializeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        windowsOverlay.Dispose();
        model?.Dispose();
        base.OnClosed(e);
    }

    private async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        model = DataContext as MainViewModel ?? throw new InvalidOperationException("Main view model is unavailable.");
        ManiaMapAnalyzerOverlay.UiText.IsEnglish = string.Equals(model.Settings.Language, "en", StringComparison.OrdinalIgnoreCase);
        ApplyLanguage();
        CustomCssService.EnsureExists();
        model.Tosu.StateChanged += Tosu_StateChanged;
        windowsOverlay.RegisterHotkeys();
        SetControlsEnabled(false);
        ShowMessagePage(Pick("Подготовка анализа карты", "Preparing map analysis"),
            Pick("Проверяю обновления и запускаю локальный сервис tosu…", "Checking updates and starting the local tosu service…"), false);

        if (!await CheckUpdatesAsync()) return;
        SynchronizeFullscreenState();
        await model.StartAsync();
        if (model.Tosu.IsRunning)
        {
            model.SetStatus(Pick("tosu работает", "tosu is running"), true);
            SetControlsEnabled(true);
            Navigate(OverlayUrl);
        }
        else
        {
            RestartButton.IsEnabled = true;
            ShowMessagePage(Pick("tosu не запущен", "tosu is not running"), model.Status, true);
        }
    }

    private void Tosu_StateChanged(object? sender, TosuStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusDot.Fill = new SolidColorBrush(Color.Parse(e.IsRunning ? "#3DCF8E" : "#FF5F7E"));
            if (e.IsRunning)
                SetControlsEnabled(true);
            else if (initialized && !overlayMode)
                SetControlsEnabled(false, keepRestart: true);
        });
    }

    private async Task<bool> CheckUpdatesAsync()
    {
        if (model is null) return false;
        if (!updates.IsInstalled)
        {
            model.SetStatus(Pick("Проверка обновлений не установлена", "Update checker is not installed"));
            return true;
        }

        model.SetStatus(Pick("Проверка обновлений…", "Checking for updates…"));
        try
        {
            var result = await updates.CheckComponentsAsync();
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? Pick("Скрипт обновления завершился с ошибкой.", "The update script failed."));
            if (result.LauncherUpdateAvailable)
            {
                var accept = await ConfirmAsync(Pick("Доступно обновление", "Update available"),
                    Pick("Доступна новая версия " + result.LatestLauncherVersion + ".\n\nОбновить сейчас? Настройки и CSS сохранятся.",
                        "A new version " + result.LatestLauncherVersion + " is available.\n\nUpdate now? Settings and custom CSS will be preserved."));
                if (accept && updates.StartSelfUpdate())
                {
                    Close();
                    return false;
                }
            }
            if (result.UpdatedTosu || result.UpdatedAddon)
                model.SetStatus(Pick("Компоненты обновлены", "Components updated"));
            else if (string.Equals(result.Compatibility, "unsupported", StringComparison.OrdinalIgnoreCase))
                await InfoAsync(Pick("Совместимость osu!lazer", "osu!lazer compatibility"),
                    Pick("Для osu!lazer " + result.LazerVersion + " ещё нет официального файла совместимости tosu. Часть данных может быть недоступна.",
                        "There is no official tosu compatibility file for osu!lazer " + result.LazerVersion + " yet. Some data may be unavailable."));
        }
        catch (Exception exception)
        {
            model.SetStatus(Pick("Обновления не проверены", "Updates were not checked"));
            try { File.WriteAllText(Path.Combine(AppPaths.BaseDirectory, "startup-update-error.log"), DateTime.Now + Environment.NewLine + exception); }
            catch { }
        }
        return true;
    }

    private void SynchronizeFullscreenState()
    {
        if (model is null) return;
        var enabled = fullscreen.ReadEnabled(model.Settings.FullscreenOverlayEnabled);
        model.Settings.FullscreenOverlayEnabled = enabled;
        if (enabled)
        {
            fullscreen.EnsureProfile(model.Settings, model.Settings.FullscreenOverlayStyleVersion < 1);
            model.Settings.FullscreenOverlayStyleVersion = 1;
        }
        model.SaveSettings();
        UpdateFullscreenButton();
    }

    private void ApplyLanguage()
    {
        if (model is null) return;
        Title = Pick("Mania Map Analyzer Overlay — анализ карты", "Mania Map Analyzer Overlay — map analysis");
        AnalysisButton.Content = Pick("Анализ карты", "Map analysis");
        AppearanceButton.Content = Pick("Оформление", "Appearance");
        OverlayButton.Content = Pick("Оверлей", "Overlay");
        DashboardButton.Content = Pick("Панель tosu", "tosu panel");
        RestartButton.Content = Pick("Перезапустить", "Restart");
        LanguageButton.Content = ManiaMapAnalyzerOverlay.UiText.IsEnglish ? "RU" : "EN";
        ExitButton.Content = Pick("Выход", "Exit");
        UpdateFullscreenButton();
    }

    private string Pick(string russian, string english) => ManiaMapAnalyzerOverlay.UiText.Get(russian, english);

    private void SetControlsEnabled(bool enabled, bool keepRestart = false)
    {
        AnalysisButton.IsEnabled = enabled;
        AppearanceButton.IsEnabled = enabled;
        OverlayButton.IsEnabled = enabled;
        FullscreenButton.IsEnabled = enabled && fullscreen.IsSupported;
        DashboardButton.IsEnabled = enabled;
        RestartButton.IsEnabled = enabled || keepRestart;
    }

    private void Navigate(string url)
    {
        try { Browser.Navigate(new Uri(url)); }
        catch (Exception exception) { model?.SetStatus(exception.Message); }
    }

    private void ShowMessagePage(string title, string message, bool error)
    {
        var accent = error ? "#ff5f7e" : "#8a7dff";
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        var safeMessage = System.Net.WebUtility.HtmlEncode(message).Replace("\n", "<br>");
        var html = "<!doctype html><html><head><meta charset='utf-8'><style>html,body{height:100%;margin:0;background:#0e1016;color:#f4f6fc;font-family:Inter,'Segoe UI',sans-serif}body{display:grid;place-items:center}.box{max-width:520px;padding:42px;text-align:center}.ring{width:42px;height:42px;margin:0 auto 22px;border:4px solid #292d3a;border-top-color:" + accent + ";border-radius:50%;animation:r 1s linear infinite}.error{animation:none;border-color:" + accent + "}h1{font-size:24px;margin:0 0 12px}p{color:#aeb5c8;line-height:1.55;margin:0}@keyframes r{to{transform:rotate(360deg)}}</style></head><body><div class='box'><div class='ring" + (error ? " error" : "") + "'></div><h1>" + safeTitle + "</h1><p>" + safeMessage + "</p></div></body></html>";
        try { Browser.NavigateToString(html, new Uri(BaseUrl)); }
        catch { }
    }

    private async void Browser_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || Browser.Source?.AbsolutePath.StartsWith("/ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase) != true) return;
        await ApplyPresentationAsync();
    }

    private void Browser_NewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e) => e.Handled = true;

    private void Browser_WebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (!overlayMode || string.IsNullOrEmpty(e.Body)) return;
        var message = e.Body;
        if (message == "mma:drag") { windowsOverlay.BeginDrag(); return; }
        if (message == "mma:play:1") { SetOverlaySuppressedByPlay(true); return; }
        if (message == "mma:play:0") { SetOverlaySuppressedByPlay(false); return; }
        if (message.StartsWith("mma:scale:", StringComparison.Ordinal) &&
            int.TryParse(message[10..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            _ = AdjustScaleAsync(delta);
            return;
        }
        if (!message.StartsWith("mma:size:", StringComparison.Ordinal)) return;
        var values = message[9..].Split(',');
        if (values.Length == 3 && int.TryParse(values[0], out var width) && int.TryParse(values[1], out var height) &&
            float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            ResizeOverlayToWidget(width, height);
    }

    private async Task ApplyPresentationAsync()
    {
        if (model is null) return;
        try
        {
            var scripts = presentation.Build(model.Settings, overlayMode);
            await Browser.InvokeScript(scripts.SetupScript);
            await Browser.InvokeScript(scripts.ObserverScript);
            if (model.Settings.FullscreenOverlayEnabled)
                fullscreen.WriteRuntime(model.Settings, scripts.FullscreenSetupScript, scripts.ObserverScript);
        }
        catch (Exception exception)
        {
            model.SetStatus(Pick("Не удалось применить оформление: ", "Could not apply appearance: ") + exception.Message);
        }
    }

    private async Task AdjustScaleAsync(int delta)
    {
        if (model is null) return;
        var next = Math.Clamp(model.Settings.OverlayScalePercent + delta, 50, 180);
        if (next == model.Settings.OverlayScalePercent) return;
        model.Settings.OverlayScalePercent = next;
        model.SaveSettings();
        await ApplyPresentationAsync();
    }

    private async void Analysis_Click(object? sender, RoutedEventArgs e) { Navigate(OverlayUrl); await Task.CompletedTask; }
    private void Dashboard_Click(object? sender, RoutedEventArgs e) => Navigate(BaseUrl + "/");

    private async void Appearance_Click(object? sender, RoutedEventArgs e)
    {
        if (model is null) return;
        var dialog = new AppearanceDialog(model.Settings, ManiaMapAnalyzerOverlay.UiText.IsEnglish);
        var accepted = await dialog.ShowDialog<bool>(this);
        if (!accepted) return;
        if (dialog.OpenAnalyzerSettings) { Navigate(DesignUrl); return; }
        model.Settings.OverlayLayoutMode = dialog.LayoutMode;
        model.Settings.OverlayScalePercent = dialog.ScalePercent;
        model.SaveSettings();
        if (model.Settings.FullscreenOverlayEnabled)
        {
            fullscreen.EnsureProfile(model.Settings, true);
            await model.RestartAsync();
        }
        Navigate(OverlayUrl);
    }

    private async void Restart_Click(object? sender, RoutedEventArgs e)
    {
        if (model is null) return;
        SetControlsEnabled(false);
        ShowMessagePage(Pick("Перезапуск tosu", "Restarting tosu"), Pick("Запускаю локальный сервис…", "Starting the local service…"), false);
        await model.RestartAsync();
        var running = model.Tosu.IsRunning;
        if (running)
            model.SetStatus(Pick("tosu работает", "tosu is running"), true);
        SetControlsEnabled(running, keepRestart: !running);
        if (running) Navigate(OverlayUrl);
    }

    private void Language_Click(object? sender, RoutedEventArgs e)
    {
        if (model is null) return;
        ManiaMapAnalyzerOverlay.UiText.IsEnglish = !ManiaMapAnalyzerOverlay.UiText.IsEnglish;
        model.Settings.Language = ManiaMapAnalyzerOverlay.UiText.IsEnglish ? "en" : "ru";
        model.SaveSettings();
        ApplyLanguage();
        if (Browser.Source?.AbsolutePath.StartsWith("/ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase) == true)
            Browser.Refresh();
    }

    private async void Overlay_Click(object? sender, RoutedEventArgs e) => await EnterOverlayModeAsync();

    private async Task EnterOverlayModeAsync()
    {
        if (model is null || overlayMode) return;
        if (OperatingSystem.IsWindows() && !windowsOverlay.RegisterHotkeys())
        {
            await InfoAsync(Pick("Горячая клавиша занята", "Hotkey unavailable"),
                Pick("Не удалось зарегистрировать Ctrl+Shift+F9/F10.", "Could not register Ctrl+Shift+F9/F10."));
            return;
        }
        if (model.Settings.OverlayHintVersion < 3)
        {
            await InfoAsync(Pick("Режим оверлея", "Overlay mode"), Pick(
                "В окне останется только виджет.\n\nCtrl+Shift+F9 — разблокировать его.\nCtrl + колесо — изменить размер.\nCtrl+Shift+F10 — вернуться в обычное окно.",
                "Only the widget remains.\n\nCtrl+Shift+F9 — unlock it.\nCtrl + wheel — resize.\nCtrl+Shift+F10 — restore the normal window."));
            model.Settings.OverlayHintVersion = 3;
            model.SaveSettings();
        }

        normalPosition = Position;
        normalClientSize = ClientSize;
        overlayMode = true;
        overlayWidgetSized = false;
        overlayPlayStateKnown = false;
        overlaySuppressedByPlay = false;
        overlayInputBeforePlay = true;
        Opacity = 0;
        Toolbar.IsVisible = false;
        RootGrid.RowDefinitions[0].Height = new GridLength(0);
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        Browser.Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var layout = OverlayPresentationService.NormalizeLayout(model.Settings.OverlayLayoutMode);
        var scale = Math.Clamp(model.Settings.OverlayScalePercent, 50, 180) / 100d;
        var width = (layout == "horizontal" ? 920 : layout == "companella" ? 620 : 475) * scale;
        var height = (layout == "horizontal" ? 360 : layout == "companella" ? 320 : 540) * scale;
        ClientSize = new Size(width, height);
        var working = Screens.ScreenFromWindow(this)?.WorkingArea ?? Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var savedVisible = model.Settings.OverlayX > -30000 && model.Settings.OverlayY > -30000;
        Position = savedVisible
            ? new PixelPoint(model.Settings.OverlayX, model.Settings.OverlayY)
            : new PixelPoint(working.Right - (int)Math.Ceiling(width * RenderScaling) - 18, working.Y + 18);
        windowsOverlay.Enter();
        Navigate(OverlayUrl);
    }

    private void LeaveOverlayMode()
    {
        if (!overlayMode || model is null) return;
        SaveOverlayBounds();
        windowsOverlay.Leave();
        overlayMode = false;
        overlayWidgetSized = false;
        overlayPlayStateKnown = false;
        overlaySuppressedByPlay = false;
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
        Navigate(OverlayUrl);
        Activate();
    }

    private void ResizeOverlayToWidget(int physicalWidth, int physicalHeight)
    {
        if (!overlayMode || physicalWidth is < 120 or > 2400 || physicalHeight is < 80 or > 3200) return;
        var position = Position;
        ClientSize = new Size(physicalWidth / RenderScaling, physicalHeight / RenderScaling);
        Position = position;
        overlayWidgetSized = true;
        if (overlayPlayStateKnown && !overlaySuppressedByPlay) Opacity = 1;
        SaveOverlayBounds();
    }

    private void SetOverlaySuppressedByPlay(bool suppressed)
    {
        overlayPlayStateKnown = true;
        if (overlaySuppressedByPlay == suppressed)
        {
            if (!suppressed && overlayWidgetSized) Opacity = 1;
            return;
        }
        overlaySuppressedByPlay = suppressed;
        if (suppressed)
        {
            overlayInputBeforePlay = windowsOverlay.IsClickThrough;
            windowsOverlay.SetClickThrough(true);
            Opacity = 0;
        }
        else
        {
            windowsOverlay.SetClickThrough(overlayInputBeforePlay);
            if (overlayWidgetSized) Opacity = 1;
        }
    }

    private void SaveOverlayBounds()
    {
        if (!overlayMode || model is null) return;
        model.Settings.OverlayX = Position.X;
        model.Settings.OverlayY = Position.Y;
        model.Settings.OverlayWidth = (int)Math.Ceiling(ClientSize.Width * RenderScaling);
        model.Settings.OverlayHeight = (int)Math.Ceiling(ClientSize.Height * RenderScaling);
        model.SaveSettings();
    }

    private async void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        if (model is null || !fullscreen.IsSupported) return;
        var enable = !fullscreen.ReadEnabled(model.Settings.FullscreenOverlayEnabled);
        var confirmed = await ConfirmAsync(Pick("Оверлей для Stable Fullscreen", "Stable Fullscreen Overlay"),
            enable
                ? Pick("Включить полноэкранный оверлей для osu!stable?\n\nОн использует официальный In-Game Overlay от tosu. tosu будет перезапущен.", "Enable the fullscreen overlay for osu!stable?\n\nIt uses tosu's official In-Game Overlay. tosu will restart.")
                : Pick("Выключить полноэкранный оверлей?\n\ntosu будет перезапущен.", "Disable the fullscreen overlay?\n\ntosu will restart."));
        if (!confirmed) return;
        try
        {
            fullscreen.SetEnabled(enable);
            model.Settings.FullscreenOverlayEnabled = enable;
            if (enable)
            {
                model.Settings.FullscreenOverlayStyleVersion = 1;
                fullscreen.EnsureProfile(model.Settings, true);
            }
            model.SaveSettings();
            UpdateFullscreenButton();
            await model.RestartAsync();
            if (enable)
            {
                Navigate(FullscreenEditorUrl);
                await InfoAsync(Pick("Stable Fullscreen включён", "Stable Fullscreen enabled"),
                    Pick("ManiaMapAnalyser добавлен в профиль. Положение меняется в редакторе или по Ctrl+Shift+Space в osu!stable.", "ManiaMapAnalyser was added to the profile. Change its position in the editor or with Ctrl+Shift+Space in osu!stable."));
            }
            else Navigate(OverlayUrl);
        }
        catch (Exception exception)
        {
            await InfoAsync(Pick("Ошибка настройки", "Configuration error"), exception.Message);
        }
    }

    private void UpdateFullscreenButton()
    {
        var enabled = model?.Settings.FullscreenOverlayEnabled == true;
        FullscreenButton.Content = enabled ? Pick("Stable FS: Вкл", "Stable FS: On") : Pick("Stable FS: Выкл", "Stable FS: Off");
        FullscreenButton.Background = new SolidColorBrush(Color.Parse(enabled ? "#2A7E5B" : "#59432A"));
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new MessageDialog(title, message, Pick("Да", "Yes"), Pick("Нет", "No"));
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task InfoAsync(string title, string message)
    {
        var dialog = new MessageDialog(title, message, "OK");
        await dialog.ShowDialog<bool>(this);
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();
}
