using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("Mania Map Analyzer Overlay")]
[assembly: AssemblyDescription("Lightweight overlay launcher for tosu and ManiaMapAnalyser")]
[assembly: AssemblyProduct("Mania Map Analyzer Overlay")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ManiaMapAnalyzerOverlay
{
    internal static class Program
    {
        private const string MutexName = @"Local\ManiaMapAnalyzerOverlay-9F780A98-5AC0-4D57-A751-031FB98E5A49";

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static void EnableNativeDpiRendering()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. This must run before
                // WinForms or WebView2 creates a window, otherwise Windows bitmap-scales it.
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    return;
            }
            catch
            {
            }

            try { SetProcessDPIAware(); }
            catch { }
        }

        [STAThread]
        private static void Main()
        {
            EnableNativeDpiRendering();
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    bool english = !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase);
                    MessageBox.Show(
                        english ? "The application is already running." : "Приложение уже запущено.",
                        "Mania Map Analyzer Overlay",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }

    internal static class UiText
    {
        public static bool IsEnglish { get; set; }

        public static string Get(string russian, string english)
        {
            return IsEnglish ? english : russian;
        }
    }

    internal sealed class MainForm : Form
    {
        private const string BaseUrl = "http://127.0.0.1:24050";
        private const string OverlayUrl = BaseUrl + "/ManiaMapAnalyser/?launcher=4";
        private const string DesignUrl = BaseUrl + "/settings?overlay=ManiaMapAnalyser";
        private const string CustomCssFileName = "overlay-custom.css";
        private const int OverlayExitHotkeyId = 0x4D41;
        private const int OverlayInputHotkeyId = 0x4D42;
        private const int WmHotkey = 0x0312;
        private const int WmNcLButtonDown = 0x00A1;
        private const int HtCaption = 0x0002;
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;

        private readonly WebView2 browser;
        private readonly TableLayoutPanel layout;
        private readonly Panel topBar;
        private readonly Label statusLabel;
        private readonly Panel statusDot;
        private readonly Button analysisButton;
        private readonly Button designButton;
        private readonly Button overlayButton;
        private readonly Button dashboardButton;
        private readonly Button restartButton;
        private readonly Button languageButton;
        private readonly Button exitButton;

        private Process tosuProcess;
        private IntPtr jobHandle = IntPtr.Zero;
        private bool closing;
        private bool browserReady;
        private bool overlayMode;
        private bool overlayClickThrough;
        private bool overlayWidgetSized;
        private bool overlayPlayStateKnown;
        private bool overlaySuppressedByPlay;
        private bool overlayInputBeforePlay;
        private bool overlayExitHotkeyRegistered;
        private bool overlayInputHotkeyRegistered;
        private Rectangle normalBounds;
        private LauncherSettings launcherSettings;
        private string startupUpdateSuffix = "";
        private string startupCompatibility = "not-detected";

        public MainForm()
        {
            launcherSettings = LoadLauncherSettings();
            UiText.IsEnglish = string.Equals(launcherSettings.Language, "en", StringComparison.OrdinalIgnoreCase);
            Text = UiText.Get("Mania Map Analyzer Overlay — анализ карты", "Mania Map Analyzer Overlay — map analysis");
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(680, 780);
            MinimumSize = new Size(650, 720);
            BackColor = Color.FromArgb(14, 16, 22);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            if (launcherSettings.CompanellaLayoutVersion < 3)
            {
                if (string.Equals(launcherSettings.OverlayLayoutMode, "companella", StringComparison.OrdinalIgnoreCase))
                    launcherSettings.OverlayScalePercent = 100;
                launcherSettings.CompanellaLayoutVersion = 3;
                SaveLauncherSettings();
            }
            EnsureCustomCssFile();

            topBar = new Panel();
            topBar.Dock = DockStyle.Top;
            topBar.Height = 108;
            topBar.BackColor = Color.FromArgb(24, 27, 36);
            topBar.Padding = new Padding(18, 12, 14, 10);

            var title = new Label();
            title.Text = "MANIA  MAP  ANALYSER";
            title.AutoSize = true;
            title.Location = new Point(18, 11);
            title.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point);
            title.ForeColor = Color.FromArgb(244, 246, 252);

            statusDot = new Panel();
            statusDot.Size = new Size(8, 8);
            statusDot.Location = new Point(20, 42);
            statusDot.BackColor = Color.FromArgb(245, 179, 66);

            statusLabel = new Label();
            statusLabel.Text = UiText.Get("Запуск tosu…", "Starting tosu…");
            statusLabel.AutoSize = true;
            statusLabel.Location = new Point(34, 37);
            statusLabel.ForeColor = Color.FromArgb(178, 184, 199);

            analysisButton = CreateButton(UiText.Get("Анализ карты", "Map analysis"), 105);
            analysisButton.Click += delegate { Navigate(OverlayUrl); };

            designButton = CreateButton(UiText.Get("Оформление", "Appearance"), 94);
            designButton.Click += delegate { ShowOverlayStyleDialog(); };

            overlayButton = CreateButton(UiText.Get("Оверлей", "Overlay"), 82);
            overlayButton.BackColor = Color.FromArgb(51, 92, 126);
            overlayButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(63, 111, 150);
            overlayButton.Click += delegate { EnterOverlayMode(); };

            dashboardButton = CreateButton(UiText.Get("Панель tosu", "tosu panel"), 94);
            dashboardButton.Click += delegate { Navigate(BaseUrl + "/"); };

            restartButton = CreateButton(UiText.Get("Перезапустить", "Restart"), 104);
            restartButton.Click += async delegate { await RestartTosuAsync(); };

            languageButton = CreateButton(UiText.IsEnglish ? "RU" : "EN", 48);
            languageButton.Click += delegate { ToggleLanguage(); };

            exitButton = CreateButton(UiText.Get("Выход", "Exit"), 68);
            exitButton.BackColor = Color.FromArgb(162, 51, 75);
            exitButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(190, 61, 88);
            exitButton.Click += delegate { Close(); };

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 48;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(8, 6, 0, 0);
            buttons.Controls.Add(analysisButton);
            buttons.Controls.Add(designButton);
            buttons.Controls.Add(overlayButton);
            buttons.Controls.Add(dashboardButton);
            buttons.Controls.Add(restartButton);
            buttons.Controls.Add(languageButton);
            buttons.Controls.Add(exitButton);

            topBar.Controls.Add(title);
            topBar.Controls.Add(statusDot);
            topBar.Controls.Add(statusLabel);
            topBar.Controls.Add(buttons);

            browser = new WebView2();
            browser.Dock = DockStyle.Fill;
            browser.BackColor = Color.FromArgb(14, 16, 22);
            browser.DefaultBackgroundColor = Color.FromArgb(14, 16, 22);

            layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Margin = Padding.Empty;
            layout.Padding = Padding.Empty;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            topBar.Dock = DockStyle.Fill;
            layout.Controls.Add(topBar, 0, 0);
            layout.Controls.Add(browser, 0, 1);
            Controls.Add(layout);

            Shown += async delegate { await InitializeAsync(); };
            FormClosing += OnFormClosing;
        }

        private static Button CreateButton(string text, int width)
        {
            var button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 34;
            button.Margin = new Padding(5, 5, 0, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(64, 70, 88);
            button.BackColor = Color.FromArgb(45, 50, 64);
            button.ForeColor = Color.White;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private void ToggleLanguage()
        {
            UiText.IsEnglish = !UiText.IsEnglish;
            launcherSettings.Language = UiText.IsEnglish ? "en" : "ru";
            SaveLauncherSettings();
            ApplyUiLanguage();

            Uri uri = browser.Source;
            if (uri != null && uri.AbsolutePath.StartsWith("/ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase))
                browser.Reload();
        }

        private void ApplyUiLanguage()
        {
            Text = UiText.Get("osu!mania — анализ карты", "osu!mania — map analysis");
            analysisButton.Text = UiText.Get("Анализ карты", "Map analysis");
            designButton.Text = UiText.Get("Оформление", "Appearance");
            overlayButton.Text = UiText.Get("Оверлей", "Overlay");
            dashboardButton.Text = UiText.Get("Панель tosu", "tosu panel");
            restartButton.Text = UiText.Get("Перезапустить", "Restart");
            languageButton.Text = UiText.IsEnglish ? "RU" : "EN";
            exitButton.Text = UiText.Get("Выход", "Exit");
            SetStatus(UiText.Get("Язык интерфейса: русский", "Interface language: English"), true);
        }

        private async Task InitializeAsync()
        {
            analysisButton.Enabled = false;
            designButton.Enabled = false;
            overlayButton.Enabled = false;
            dashboardButton.Enabled = false;
            restartButton.Enabled = false;

            try
            {
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ManiaMapAnalyzerOverlay",
                    "WebView2");
                Directory.CreateDirectory(userData);

                var environmentOptions = new CoreWebView2EnvironmentOptions();
                environmentOptions.AdditionalBrowserArguments =
                    "--renderer-process-limit=1";
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                    null,
                    userData,
                    environmentOptions);
                await browser.EnsureCoreWebView2Async(environment);
                browserReady = true;
                browser.ZoomFactor = 1D;
                browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
                browser.CoreWebView2.Settings.IsZoomControlEnabled = false;
                browser.CoreWebView2.AddWebResourceRequestedFilter(
                    "http://localhost:24050/files/beatmap/*",
                    CoreWebView2WebResourceContext.All);
                browser.CoreWebView2.AddWebResourceRequestedFilter(
                    "http://127.0.0.1:24050/files/beatmap/*",
                    CoreWebView2WebResourceContext.All);
                browser.CoreWebView2.WebResourceRequested += OnBeatmapResourceRequested;
                browser.CoreWebView2.NavigationCompleted += OnBrowserNavigationCompleted;
                browser.CoreWebView2.WebMessageReceived += OnBrowserWebMessageReceived;
                browser.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args)
                {
                    args.Handled = true;
                };
                ShowMessagePage(UiText.Get("Подготовка анализа карты", "Preparing map analysis"), UiText.Get("Запускаю локальный сервис tosu…", "Starting the local tosu service…"), false);
            }
            catch (Exception ex)
            {
                SetStatus(UiText.Get("Не удалось запустить окно WebView2", "Could not start the WebView2 window"), false);
                MessageBox.Show(
                    UiText.Get("Не удалось инициализировать Microsoft Edge WebView2.\r\n\r\n", "Microsoft Edge WebView2 could not be initialized.\r\n\r\n") + ex.Message,
                    UiText.Get("Ошибка запуска", "Startup error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            await CheckAndApplyUpdatesAsync();
            await StartTosuAsync();
        }

        private async void OnBrowserNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            try
            {
                Uri uri = browser.Source;
                if (uri != null &&
                    uri.AbsolutePath.StartsWith("/ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase))
                {
                    await ApplyLauncherStylesAsync();
                }
            }
            catch
            {
            }
        }

        private string GetOverlayLayoutMode()
        {
            string mode = (launcherSettings.OverlayLayoutMode ?? "default").Trim().ToLowerInvariant();
            if (mode != "default" && mode != "horizontal" && mode != "companella" && mode != "custom")
                mode = "default";
            return mode;
        }

        private int GetOverlayScalePercent()
        {
            return Math.Max(50, Math.Min(180, launcherSettings.OverlayScalePercent));
        }

        private static string GetCustomCssPath()
        {
            return Path.Combine(Application.StartupPath, CustomCssFileName);
        }

        private static void EnsureCustomCssFile()
        {
            try
            {
                string path = GetCustomCssPath();
                if (!File.Exists(path))
                    File.WriteAllText(path, GetCustomCssTemplate(), new System.Text.UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string GetCustomCssTemplate()
        {
            return @"/*
  Mania Map Analyzer Overlay custom style / Пользовательский стиль.

  EN: Open Appearance, choose Custom CSS, edit and save this file,
      then click Apply. Updates never overwrite this file.
  RU: Откройте «Оформление», выберите «Пользовательский CSS», измените
      и сохраните файл, затем нажмите «Применить». Обновления его не стирают.

  The size slider is applied on top of this CSS.
  Масштаб из настроек применяется поверх этого CSS.
*/

/* Main colors and transparency / Основные цвета и прозрачность. */
html.mma-layout-custom {
    /* --mma-host-width follows the native size slider / меняется ползунком. */
    --mma-custom-width: var(--mma-host-width, 475px);
    --glass: rgba(18, 22, 38, 0.92);
    --glass-border: rgba(255, 255, 255, 0.14);
    --text-primary: #f5f7ff;
    --text-soft: #a9b1d2;
    --track: rgba(255, 255, 255, 0.10);
    --card-radius: 16px;
}

/* Card size; height follows visible analyser sections / Размер карточки. */
html.mma-layout-custom .dashboard {
    width: var(--mma-custom-width) !important;
    min-width: var(--mma-custom-width) !important;
    max-width: var(--mma-custom-width) !important;
}

html.mma-layout-custom .card.main-card {
    width: 100% !important;
    min-width: 100% !important;
    max-width: 100% !important;
    border-radius: var(--card-radius) !important;
}

/* Examples / Примеры — uncomment the block you need.

html.mma-layout-custom .card.main-card {
    background: rgba(8, 10, 18, 0.82) !important;
    border-color: rgba(255, 80, 140, 0.55) !important;
}

html.mma-layout-custom .star-value {
    color: #ffffff !important;
    background: #ed4f76 !important;
}

html.mma-layout-custom .status {
    color: #7fffc4 !important;
}

*/

/* Narrow layout adaptation / Адаптация для узкого формата. */
@media (max-width: 430px) {
    html.mma-layout-custom .star-value {
        font-size: 40px !important;
    }

    html.mma-layout-custom .star-subtitle {
        font-size: 22px !important;
    }
}
";
        }

        private void ShowOverlayStyleDialog()
        {
            EnsureCustomCssFile();
            using (var dialog = new OverlayStyleDialog(
                GetOverlayLayoutMode(),
                GetOverlayScalePercent(),
                GetCustomCssPath(),
                UiText.IsEnglish))
            {
                DialogResult result = dialog.ShowDialog(this);
                if (result == DialogResult.Yes)
                {
                    Navigate(DesignUrl);
                    return;
                }
                if (result != DialogResult.OK)
                    return;

                launcherSettings.OverlayLayoutMode = dialog.LayoutMode;
                launcherSettings.OverlayScalePercent = dialog.ScalePercent;
                SaveLauncherSettings();

                Uri uri = browser.Source;
                if (uri != null && uri.AbsolutePath.StartsWith("/ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase))
                    browser.Reload();
            }
        }

        private async void AdjustOverlayScale(int delta)
        {
            if (!overlayMode)
                return;

            int next = Math.Max(50, Math.Min(180, GetOverlayScalePercent() + delta));
            if (next == launcherSettings.OverlayScalePercent)
                return;

            launcherSettings.OverlayScalePercent = next;
            SaveLauncherSettings();
            try
            {
                await ApplyLauncherStylesAsync();
            }
            catch
            {
            }
        }

        private static string NativePx(double value, double scale)
        {
            int pixels = Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));
            return pixels.ToString(CultureInfo.InvariantCulture) + "px";
        }

        private static string BuildNativeScaleCss(double scale)
        {
            string scaleText = scale.ToString("0.00", CultureInfo.InvariantCulture);
            return
                "html.launcher-overlay-host{--mma-host-scale:" + scaleText + ";}" +
                "html.launcher-overlay-host .card{border-radius:" + NativePx(16, scale) + "!important;padding:" + NativePx(12, scale) + "!important;}" +
                "html.launcher-overlay-host .main-card{gap:" + NativePx(8, scale) + "!important;}" +
                "html.launcher-overlay-host .status-row{gap:" + NativePx(10, scale) + "!important;margin-bottom:" + NativePx(8, scale) + "!important;}" +
                "html.launcher-overlay-host .status{font-size:" + NativePx(15, scale) + "!important;}" +
                "html.launcher-overlay-host .star-block{gap:" + NativePx(6, scale) + "!important;}" +
                "html.launcher-overlay-host .star-left{gap:" + NativePx(4, scale) + "!important;}" +
                "html.launcher-overlay-host .star-meta{font-size:" + NativePx(14, scale) + "!important;}" +
                "html.launcher-overlay-host .star-value{font-size:" + NativePx(48, scale) + "!important;padding:" + NativePx(4, scale) + " " + NativePx(10, scale) + "!important;border-radius:" + NativePx(20, scale) + "!important;}" +
                "html.launcher-overlay-host .main-card:not(.bars-none) .star-value:not(.category-mode){font-size:" + NativePx(52, scale) + "!important;padding:" + NativePx(5, scale) + " " + NativePx(12, scale) + "!important;border-radius:" + NativePx(22, scale) + "!important;}" +
                "html.launcher-overlay-host .star-value.category-mode{font-size:" + NativePx(24, scale) + "!important;}" +
                "html.launcher-overlay-host .star-right-group{gap:" + NativePx(5, scale) + "!important;}" +
                "html.launcher-overlay-host .star-right{gap:" + NativePx(3, scale) + "!important;margin-bottom:" + NativePx(2, scale) + "!important;}" +
                "html.launcher-overlay-host .star-subtitle{font-size:" + NativePx(27, scale) + "!important;}" +
                "html.launcher-overlay-host .star-caption{font-size:" + NativePx(12, scale) + "!important;}" +
                "html.launcher-overlay-host .top-right-capsule{font-size:" + NativePx(30, scale) + "!important;padding:" + NativePx(4, scale) + " " + NativePx(10, scale) + "!important;border-radius:" + NativePx(18, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-bars{gap:" + NativePx(6, scale) + "!important;padding-right:" + NativePx(4, scale) + "!important;padding-bottom:" + NativePx(18, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-item{gap:" + NativePx(4, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-label{font-size:" + NativePx(15, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-track{height:" + NativePx(10, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-subtype{font-size:" + NativePx(13, scale) + "!important;}" +
                "html.launcher-overlay-host .ett-skill-bars{gap:" + NativePx(8, scale) + "!important;padding:" + NativePx(2, scale) + " " + NativePx(4, scale) + " " + NativePx(18, scale) + " 0!important;}" +
                "html.launcher-overlay-host .ett-skill-item{gap:" + NativePx(4, scale) + "!important;}" +
                "html.launcher-overlay-host .ett-skill-label{font-size:" + NativePx(14, scale) + "!important;}" +
                "html.launcher-overlay-host .ett-skill-track{height:" + NativePx(15, scale) + "!important;}" +
                "html.launcher-overlay-host .ett-skill-head{font-size:" + NativePx(11, scale) + "!important;padding:" + NativePx(1, scale) + " " + NativePx(6, scale) + "!important;}" +
                "html.launcher-overlay-host .mode-tag{font-size:" + NativePx(11, scale) + "!important;padding:" + NativePx(2, scale) + " " + NativePx(9, scale) + "!important;}" +
                "html.launcher-overlay-host .pause-count{font-size:" + NativePx(12, scale) + "!important;}";
        }

        private static string BuildCompanellaCss(double scale, string width)
        {
            string chartHeight = NativePx(142, scale);
            return
                "html.mma-layout-companella{--mma-host-width:" + width + ";}" +
                "html.mma-layout-companella .dashboard{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                "html.mma-layout-companella .main-card{display:grid!important;grid-template-columns:54% minmax(0,46%)!important;grid-template-rows:auto auto auto!important;column-gap:" + NativePx(16, scale) + "!important;row-gap:" + NativePx(7, scale) + "!important;align-items:start!important;align-content:start!important;width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;height:auto!important;min-height:0!important;max-height:none!important;padding:" + NativePx(11, scale) + " " + NativePx(14, scale) + " " + NativePx(28, scale) + "!important;overflow:hidden!important;background-color:#0a0c12!important;background-image:linear-gradient(100deg,rgba(6,8,12,.84) 0%,rgba(8,10,16,.78) 52%,rgba(4,6,11,.91) 100%),var(--mma-comp-cover,var(--ma-cover,none))!important;background-size:cover!important;background-position:center!important;background-repeat:no-repeat!important;border:" + NativePx(1, scale) + " solid rgba(255,255,255,.17)!important;border-bottom:" + NativePx(3, scale) + " solid #ff4f9b!important;border-radius:" + NativePx(9, scale) + "!important;}" +
                "html.mma-layout-companella .main-card.bars-pattern,html.mma-layout-companella .main-card.bars-etterna,html.mma-layout-companella .main-card.bars-etterna.bars-etterna-compact,html.mma-layout-companella .main-card.bars-graph,html.mma-layout-companella .main-card.bars-none,html.mma-layout-companella .main-card.bars-full{height:auto!important;min-height:0!important;max-height:none!important;}" +
                "html.mma-layout-companella .triangle-field{opacity:.10!important;}" +
                "html.mma-layout-companella .status-row{grid-column:1/-1!important;grid-row:1!important;min-width:0!important;margin:0!important;padding:0 0 " + NativePx(6, scale) + "!important;border-bottom:" + NativePx(1, scale) + " solid rgba(255,255,255,.15)!important;}" +
                "html.mma-layout-companella .title-icon{display:none!important;}" +
                "html.mma-layout-companella .status{display:block!important;width:100%!important;max-width:100%!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(16, scale) + "!important;font-weight:600!important;color:#f7f7fa!important;letter-spacing:.01em!important;}" +
                "html.mma-layout-companella .mma-comp-meta{grid-column:1/-1!important;grid-row:2!important;display:grid!important;grid-template-columns:minmax(0,1fr) auto!important;gap:" + NativePx(12, scale) + "!important;align-items:start!important;min-width:0!important;color:#c8cad4!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(10, scale) + "!important;line-height:1.35!important;}" +
                "html.mma-layout-companella .mma-comp-map,html.mma-layout-companella .mma-comp-numbers{min-width:0!important;}" +
                "html.mma-layout-companella .mma-comp-map{overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;}" +
                "html.mma-layout-companella .mma-comp-numbers{text-align:right!important;white-space:nowrap!important;}" +
                "html.mma-layout-companella .mma-comp-muted{color:#9196a7!important;}" +
                "html.mma-layout-companella .mma-comp-chart{grid-column:1!important;grid-row:3!important;display:grid!important;grid-template-columns:repeat(var(--mma-comp-count,7),minmax(0,1fr))!important;gap:" + NativePx(6, scale) + "!important;align-items:end!important;height:" + chartHeight + "!important;min-height:" + chartHeight + "!important;padding:" + NativePx(3, scale) + " 0 0!important;overflow:hidden!important;}" +
                "html.mma-layout-companella .mma-comp-chart:empty{place-items:center!important;color:#9196a7!important;font-size:" + NativePx(11, scale) + "!important;}" +
                "html.mma-layout-companella .mma-comp-chart:empty::after{content:'" + UiText.Get("Нет данных анализа", "No analysis data") + "';}" +
                "html.mma-layout-companella .mma-comp-column{display:grid!important;grid-template-rows:minmax(0,1fr) auto auto!important;gap:" + NativePx(3, scale) + "!important;height:100%!important;min-width:0!important;}" +
                "html.mma-layout-companella .mma-comp-barbox{position:relative!important;display:flex!important;align-items:flex-end!important;min-height:0!important;background:rgba(50,52,62,.70)!important;border-radius:" + NativePx(3, scale) + "!important;overflow:hidden!important;}" +
                "html.mma-layout-companella .mma-comp-bar{width:100%!important;height:var(--mma-value,2%)!important;min-height:" + NativePx(2, scale) + "!important;background:var(--mma-color,#69ced1)!important;border-radius:" + NativePx(3, scale) + " " + NativePx(3, scale) + " 0 0!important;}" +
                "html.mma-layout-companella .mma-comp-number{text-align:center!important;color:#f1f1f5!important;font-size:" + NativePx(9, scale) + "!important;line-height:1.1!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;}" +
                "html.mma-layout-companella .mma-comp-label{text-align:center!important;color:#bfc2cc!important;font-size:" + NativePx(9, scale) + "!important;line-height:1.1!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;}" +
                "html.mma-layout-companella .mma-host-details{grid-column:2!important;grid-row:3!important;display:grid!important;grid-auto-rows:auto!important;gap:" + NativePx(4, scale) + "!important;align-content:start!important;min-width:0!important;overflow:hidden!important;padding-top:" + NativePx(57, scale) + "!important;}" +
                "html.mma-layout-companella .mma-host-details>[hidden]{display:none!important;}" +
                "html.mma-layout-companella .full-separator{display:none!important;}" +
                "html.mma-layout-companella .star-block{grid-column:2!important;grid-row:3!important;z-index:2!important;align-self:start!important;display:grid!important;grid-template-columns:auto minmax(0,1fr)!important;gap:" + NativePx(10, scale) + "!important;min-width:0!important;height:" + NativePx(50, scale) + "!important;padding:0 0 0 " + NativePx(12, scale) + "!important;border-left:" + NativePx(1, scale) + " solid rgba(255,255,255,.17)!important;}" +
                "html.mma-layout-companella .star-left{display:flex!important;flex-direction:column!important;align-items:flex-start!important;justify-content:center!important;width:auto!important;min-width:" + NativePx(78, scale) + "!important;gap:0!important;}" +
                "html.mma-layout-companella .star-meta{order:0!important;width:auto!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(9, scale) + "!important;color:#c9cbd4!important;line-height:1.2!important;white-space:nowrap!important;}" +
                "html.mma-layout-companella .star-value,html.mma-layout-companella .main-card:not(.bars-none) .star-value:not(.category-mode),html.mma-layout-companella .star-value.high-contrast{font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(27, scale) + "!important;font-weight:600!important;line-height:1!important;color:#f8f8fa!important;background:transparent!important;border:0!important;border-radius:0!important;padding:0!important;box-shadow:none!important;text-shadow:none!important;animation:none!important;}" +
                "html.mma-layout-companella .star-value.has-unit::after{right:" + NativePx(-20, scale) + "!important;bottom:0!important;font-size:" + NativePx(8, scale) + "!important;background:rgba(255,79,155,.22)!important;border-color:rgba(255,79,155,.45)!important;}" +
                "html.mma-layout-companella .star-right-group{width:100%!important;max-width:100%!important;min-width:0!important;flex:0 0 auto!important;align-items:flex-start!important;justify-content:center!important;}" +
                "html.mma-layout-companella .star-right{width:100%!important;min-width:0!important;text-align:left!important;justify-items:start!important;gap:" + NativePx(2, scale) + "!important;margin:0!important;}" +
                "html.mma-layout-companella .star-subtitle{display:block!important;width:100%!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(16, scale) + "!important;font-weight:600!important;color:#ff65aa!important;line-height:1.15!important;}" +
                "html.mma-layout-companella .star-caption{font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(9, scale) + "!important;color:#c9c9d1!important;letter-spacing:.04em!important;}" +
                "html.mma-layout-companella .star-tip,html.mma-layout-companella .top-right-capsule{display:none!important;}" +
                "html.mma-layout-companella .cluster-bars,html.mma-layout-companella .ett-skill-bars{display:grid!important;grid-template-columns:1fr!important;grid-auto-rows:" + NativePx(18, scale) + "!important;gap:" + NativePx(2, scale) + "!important;width:100%!important;height:auto!important;min-height:0!important;max-height:" + NativePx(138, scale) + "!important;padding:0!important;margin:0!important;overflow:hidden!important;}" +
                "html.mma-layout-companella .cluster-item{display:grid!important;grid-template-columns:minmax(" + NativePx(82, scale) + ",.9fr) minmax(" + NativePx(55, scale) + ",1fr) minmax(" + NativePx(48, scale) + ",auto)!important;align-items:center!important;gap:" + NativePx(6, scale) + "!important;height:" + NativePx(18, scale) + "!important;min-width:0!important;opacity:1!important;transform:none!important;animation:none!important;}" +
                "html.mma-layout-companella .cluster-label{order:0!important;min-width:0!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(10, scale) + "!important;font-weight:600!important;text-align:left!important;color:#e4e5ea!important;}" +
                "html.mma-layout-companella .cluster-track{order:0!important;display:block!important;width:100%!important;height:" + NativePx(5, scale) + "!important;min-height:" + NativePx(5, scale) + "!important;border-radius:" + NativePx(4, scale) + "!important;background:rgba(255,255,255,.14)!important;overflow:hidden!important;}" +
                "html.mma-layout-companella .cluster-fill{width:var(--bar-width,0%)!important;height:100%!important;min-height:100%!important;border-radius:inherit!important;transform:none!important;animation:none!important;background:#66cdd0!important;}" +
                "html.mma-layout-companella .cluster-subtype{display:block!important;min-width:0!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;text-align:right!important;font-size:" + NativePx(9, scale) + "!important;color:#adb0bd!important;}" +
                "html.mma-layout-companella .ett-skill-item{display:grid!important;grid-template-columns:minmax(" + NativePx(82, scale) + ",.9fr) minmax(" + NativePx(80, scale) + ",1.3fr)!important;align-items:center!important;gap:" + NativePx(7, scale) + "!important;height:" + NativePx(18, scale) + "!important;min-width:0!important;opacity:1!important;transform:none!important;animation:none!important;}" +
                "html.mma-layout-companella .ett-skill-label{min-width:0!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(10, scale) + "!important;font-weight:600!important;text-align:left!important;color:#e4e5ea!important;}" +
                "html.mma-layout-companella .ett-skill-track{position:relative!important;width:100%!important;height:" + NativePx(6, scale) + "!important;min-height:" + NativePx(6, scale) + "!important;}" +
                "html.mma-layout-companella .ett-skill-track-inner{display:block!important;width:100%!important;height:100%!important;border-radius:" + NativePx(4, scale) + "!important;background:rgba(255,255,255,.14)!important;overflow:hidden!important;}" +
                "html.mma-layout-companella .ett-skill-fill{width:var(--bar-width,0%)!important;height:100%!important;min-height:100%!important;border-radius:inherit!important;transform:none!important;animation:none!important;background:#66cdd0!important;}" +
                "html.mma-layout-companella .ett-skill-head{top:50%!important;bottom:auto!important;left:auto!important;right:" + NativePx(3, scale) + "!important;transform:translateY(-50%)!important;font-size:" + NativePx(8, scale) + "!important;line-height:1!important;padding:0!important;background:transparent!important;border:0!important;}" +
                "html.mma-layout-companella .ett-skill-item.empty,html.mma-layout-companella .cluster-item.empty{display:grid!important;grid-template-columns:1fr!important;place-items:center!important;color:#a8a8b4!important;}" +
                "html.mma-layout-companella .cluster-item:nth-child(7n+1) .cluster-fill{background:#dedee1!important;}html.mma-layout-companella .cluster-item:nth-child(7n+2) .cluster-fill{background:#58b8f0!important;}html.mma-layout-companella .cluster-item:nth-child(7n+3) .cluster-fill{background:#5fd56b!important;}html.mma-layout-companella .cluster-item:nth-child(7n+4) .cluster-fill{background:#ffae5c!important;}html.mma-layout-companella .cluster-item:nth-child(7n+5) .cluster-fill{background:#ae5be2!important;}html.mma-layout-companella .cluster-item:nth-child(7n+6) .cluster-fill{background:#ef5d72!important;}html.mma-layout-companella .cluster-item:nth-child(7n+7) .cluster-fill{background:#f4d95f!important;}" +
                "html.mma-layout-companella .body-graph-wrap{width:100%!important;height:" + NativePx(116, scale) + "!important;margin:0!important;}" +
                "html.mma-layout-companella .mode-tag-group{left:auto!important;right:" + NativePx(14, scale) + "!important;bottom:" + NativePx(10, scale) + "!important;}" +
                "html.mma-layout-companella .mode-tag{font-size:" + NativePx(9, scale) + "!important;padding:" + NativePx(2, scale) + " " + NativePx(7, scale) + "!important;background:rgba(31,33,43,.90)!important;border-color:rgba(255,255,255,.16)!important;color:#e8e8ed!important;}" +
                "html.mma-layout-companella .pause-count{display:none!important;}" +
                "html.mma-layout-companella .card-overlay{z-index:20!important;}";
        }

        private static string BuildCompanellaCssV2(double scale, string width)
        {
            return
                "html.mma-layout-companella{--mma-host-width:" + width + ";}" +
                "html.mma-layout-companella .dashboard{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                "html.mma-layout-companella .main-card,html.mma-layout-companella .main-card.bars-pattern,html.mma-layout-companella .main-card.bars-etterna,html.mma-layout-companella .main-card.bars-etterna.bars-etterna-compact,html.mma-layout-companella .main-card.bars-graph,html.mma-layout-companella .main-card.bars-none,html.mma-layout-companella .main-card.bars-full{display:grid!important;grid-template-columns:minmax(0,1fr)!important;grid-template-rows:auto!important;grid-auto-rows:auto!important;gap:" + NativePx(7, scale) + "!important;align-items:start!important;align-content:start!important;width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;height:auto!important;min-height:0!important;max-height:none!important;padding:" + NativePx(11, scale) + " " + NativePx(13, scale) + " " + NativePx(28, scale) + "!important;overflow:hidden!important;background:#0a0c12!important;border:" + NativePx(1, scale) + " solid rgba(255,255,255,.17)!important;border-bottom:" + NativePx(3, scale) + " solid #ff4f9b!important;border-radius:" + NativePx(9, scale) + "!important;}" +
                "html.mma-layout-companella .main-card::before,html.mma-layout-companella .main-card::after{display:none!important;content:none!important;opacity:0!important;}" +
                "html.mma-layout-companella .mma-comp-cover-layer{display:block!important;position:absolute!important;inset:0!important;z-index:0!important;border-radius:inherit!important;background-image:linear-gradient(100deg,rgba(6,8,12,.82) 0%,rgba(8,10,16,.77) 58%,rgba(4,6,11,.90) 100%),var(--mma-comp-cover,var(--ma-cover,none))!important;background-size:cover!important;background-position:center!important;background-repeat:no-repeat!important;pointer-events:none!important;}" +
                "html.mma-layout-companella .main-card>.status-row,html.mma-layout-companella .main-card>.mma-comp-meta,html.mma-layout-companella .main-card>.mma-comp-summary,html.mma-layout-companella .main-card>.mma-comp-chart,html.mma-layout-companella .main-card>.mma-host-details,html.mma-layout-companella .main-card>.mode-tag-group,html.mma-layout-companella .main-card>.pause-count{position:relative!important;z-index:1!important;}" +
                "html.mma-layout-companella .triangle-field{opacity:.08!important;}" +
                "html.mma-layout-companella .status-row{grid-column:1!important;grid-row:1!important;min-width:0!important;margin:0!important;padding:0 0 " + NativePx(6, scale) + "!important;border-bottom:" + NativePx(1, scale) + " solid rgba(255,255,255,.15)!important;}" +
                "html.mma-layout-companella .title-icon{display:none!important;}" +
                "html.mma-layout-companella .status{display:block!important;width:100%!important;max-width:100%!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(15, scale) + "!important;font-weight:600!important;color:#f7f7fa!important;letter-spacing:.01em!important;}" +
                "html.mma-layout-companella .mma-comp-meta{grid-column:1!important;grid-row:2!important;display:block!important;min-width:0!important;color:#c8cad4!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(10, scale) + "!important;line-height:1.35!important;}" +
                "html.mma-layout-companella .mma-comp-map{display:block!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;}" +
                "html.mma-layout-companella .mma-comp-numbers{display:none!important;}" +
                "html.mma-layout-companella .mma-comp-muted{color:#9297a8!important;}" +
                "html.mma-layout-companella .mma-comp-summary{grid-column:1!important;grid-row:3!important;display:grid!important;grid-template-columns:1.45fr repeat(4,minmax(0,1fr))!important;gap:" + NativePx(5, scale) + "!important;width:100%!important;min-width:0!important;}" +
                "html.mma-layout-companella .mma-comp-summary-item{display:flex!important;flex-direction:column!important;justify-content:center!important;min-width:0!important;min-height:" + NativePx(48, scale) + "!important;padding:" + NativePx(5, scale) + " " + NativePx(7, scale) + "!important;background:rgba(15,18,27,.66)!important;border:" + NativePx(1, scale) + " solid rgba(255,255,255,.12)!important;border-radius:" + NativePx(6, scale) + "!important;}" +
                "html.mma-layout-companella .mma-comp-summary-label{font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(8, scale) + "!important;line-height:1.1!important;text-transform:uppercase!important;letter-spacing:.08em!important;color:#9297a8!important;}" +
                "html.mma-layout-companella .mma-comp-summary-value{display:block!important;min-width:0!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(12, scale) + "!important;line-height:1.25!important;font-weight:650!important;color:#f3f4f8!important;}" +
                "html.mma-layout-companella .mma-comp-summary-rating .mma-comp-summary-value{font-size:" + NativePx(21, scale) + "!important;line-height:1!important;color:#ffffff!important;}" +
                "html.mma-layout-companella .mma-comp-summary-note{display:block!important;min-width:0!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-size:" + NativePx(8, scale) + "!important;line-height:1.15!important;color:#bec1cc!important;}" +
                "html.mma-layout-companella .mma-comp-chart{grid-column:1!important;grid-row:4!important;display:grid!important;grid-template-columns:repeat(var(--mma-comp-count,7),minmax(0,1fr))!important;gap:" + NativePx(6, scale) + "!important;align-items:start!important;width:100%!important;height:auto!important;min-height:" + NativePx(192, scale) + "!important;padding:" + NativePx(3, scale) + " 0 0!important;overflow:visible!important;}" +
                "html.mma-layout-companella .mma-comp-chart[hidden]{display:none!important;}" +
                "html.mma-layout-companella .mma-comp-chart:empty{place-items:center!important;color:#9297a8!important;font-size:" + NativePx(10, scale) + "!important;}" +
                "html.mma-layout-companella .mma-comp-chart:empty::after{content:'" + UiText.Get("Нет данных анализа", "No analysis data") + "';}" +
                "html.mma-layout-companella .mma-comp-column{display:grid!important;grid-template-rows:" + NativePx(116, scale) + " auto auto!important;align-content:start!important;gap:" + NativePx(4, scale) + "!important;height:auto!important;min-width:0!important;overflow:visible!important;}" +
                "html.mma-layout-companella .mma-comp-barbox{position:relative!important;display:flex!important;align-items:flex-end!important;width:100%!important;height:" + NativePx(116, scale) + "!important;background:rgba(43,46,57,.78)!important;border:" + NativePx(1, scale) + " solid rgba(255,255,255,.05)!important;border-radius:" + NativePx(3, scale) + "!important;overflow:hidden!important;}" +
                "html.mma-layout-companella .mma-comp-bar{width:100%!important;height:var(--mma-value,2%)!important;min-height:" + NativePx(2, scale) + "!important;background:var(--mma-color,#69ced1)!important;border-radius:" + NativePx(3, scale) + " " + NativePx(3, scale) + " 0 0!important;}" +
                "html.mma-layout-companella .mma-comp-label{grid-row:2!important;display:block!important;min-width:0!important;text-align:center!important;color:#f0f1f5!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(9, scale) + "!important;font-weight:650!important;line-height:1.15!important;white-space:normal!important;overflow:visible!important;overflow-wrap:anywhere!important;}" +
                "html.mma-layout-companella .mma-comp-number{grid-row:3!important;display:block!important;min-width:0!important;text-align:center!important;color:#b9bdca!important;font-family:'Segoe UI',sans-serif!important;font-size:" + NativePx(8, scale) + "!important;font-weight:400!important;line-height:1.2!important;white-space:normal!important;overflow:visible!important;overflow-wrap:anywhere!important;}" +
                "html.mma-layout-companella .mma-host-details{grid-column:1!important;grid-row:5!important;display:grid!important;width:100%!important;min-width:0!important;gap:0!important;padding:0!important;margin:0!important;overflow:visible!important;}" +
                "html.mma-layout-companella .mma-host-details>.full-separator,html.mma-layout-companella .mma-host-details>#pattern-clusters,html.mma-layout-companella .mma-host-details>#ett-skill-bars{display:none!important;}" +
                "html.mma-layout-companella .mma-host-details>.body-graph-wrap{width:100%!important;height:" + NativePx(126, scale) + "!important;margin:0!important;}" +
                "html.mma-layout-companella .star-block{display:none!important;}" +
                "html.mma-layout-companella .star-value.has-unit::after{display:none!important;content:none!important;}" +
                "html.mma-layout-companella .mode-tag-group{left:auto!important;right:" + NativePx(13, scale) + "!important;bottom:" + NativePx(8, scale) + "!important;}" +
                "html.mma-layout-companella .mode-tag{font-size:" + NativePx(9, scale) + "!important;padding:" + NativePx(2, scale) + " " + NativePx(7, scale) + "!important;background:rgba(31,33,43,.90)!important;border-color:rgba(255,255,255,.16)!important;color:#e8e8ed!important;}" +
                "html.mma-layout-companella .pause-count{display:none!important;}" +
                "html.mma-layout-companella .card-overlay{z-index:20!important;}";
        }

        private async Task ApplyLauncherStylesAsync()
        {
            if (!browserReady || browser.CoreWebView2 == null)
                return;

            string layoutMode = GetOverlayLayoutMode();
            string customCss = "";
            string css;
            bool renderSelectedPreset = true;
            double nativeScale = GetOverlayScalePercent() / 100D;
            string defaultWidth = NativePx(475, nativeScale);
            string horizontalWidth = NativePx(920, nativeScale);
            string companellaWidth = NativePx(620, nativeScale);
            if (renderSelectedPreset)
            {
                css =
                    "html,body{width:100%!important;height:100%!important;min-height:0!important;background:transparent!important;overflow:hidden!important;}" +
                    "body{padding:0!important;margin:0!important;}" +
                    ".dashboard{min-height:0!important;margin:0!important;gap:0!important;align-content:start!important;}" +
                    ".card.main-card{margin:0!important;box-shadow:none!important;}" +
                    BuildNativeScaleCss(nativeScale);

                if (string.Equals(layoutMode, "horizontal", StringComparison.Ordinal))
                {
                    css +=
                        "html.mma-layout-horizontal{--mma-host-width:" + horizontalWidth + ";}" +
                        "html.mma-layout-horizontal .dashboard{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                        "html.mma-layout-horizontal .main-card{display:grid!important;grid-template-columns:34% minmax(0,66%)!important;grid-template-rows:auto auto!important;grid-auto-rows:auto!important;column-gap:" + NativePx(20, nativeScale) + "!important;row-gap:" + NativePx(8, nativeScale) + "!important;align-items:start!important;align-content:start!important;width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;height:auto!important;min-height:" + NativePx(318, nativeScale) + "!important;max-height:none!important;padding:" + NativePx(14, nativeScale) + " " + NativePx(16, nativeScale) + " " + NativePx(34, nativeScale) + "!important;overflow:hidden!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-pattern,html.mma-layout-horizontal .main-card.bars-etterna,html.mma-layout-horizontal .main-card.bars-etterna.bars-etterna-compact,html.mma-layout-horizontal .main-card.bars-graph,html.mma-layout-horizontal .main-card.bars-none,html.mma-layout-horizontal .main-card.bars-full{height:auto!important;min-height:" + NativePx(318, nativeScale) + "!important;max-height:none!important;}" +
                        "html.mma-layout-horizontal .status-row{grid-column:1/-1!important;grid-row:1!important;margin:0 0 " + NativePx(4, nativeScale) + "!important;}" +
                        "html.mma-layout-horizontal .star-block{grid-column:1!important;grid-row:2!important;align-self:start!important;display:flex!important;flex-direction:column!important;align-items:stretch!important;justify-content:flex-start!important;gap:" + NativePx(14, nativeScale) + "!important;min-width:0!important;}" +
                        "html.mma-layout-horizontal .star-left{width:100%!important;}" +
                        "html.mma-layout-horizontal .star-right-group{width:100%!important;max-width:100%!important;flex:0 0 auto!important;align-items:flex-start!important;justify-content:flex-start!important;}" +
                        "html.mma-layout-horizontal .star-right{text-align:left!important;justify-items:start!important;}" +
                        "html.mma-layout-horizontal .mma-host-details{grid-column:2!important;grid-row:2!important;display:grid!important;grid-auto-rows:auto!important;gap:" + NativePx(8, nativeScale) + "!important;align-content:start!important;min-width:0!important;overflow:visible!important;}" +
                        "html.mma-layout-horizontal .mma-host-details>[hidden]{display:none!important;}" +
                        "html.mma-layout-horizontal .cluster-bars,html.mma-layout-horizontal .ett-skill-bars{height:auto!important;min-height:0!important;max-height:none!important;overflow:visible!important;padding-bottom:" + NativePx(24, nativeScale) + "!important;margin-bottom:0!important;}" +
                        "html.mma-layout-horizontal .body-graph-wrap{width:100%!important;margin:0 auto " + NativePx(24, nativeScale) + "!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-none{grid-template-columns:1fr!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-none .star-block{grid-column:1/-1!important;display:flex!important;flex-direction:row!important;align-items:flex-end!important;justify-content:space-between!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-none .star-right-group{width:56%!important;max-width:56%!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-none .mma-host-details{display:none!important;}";
                }
                else if (string.Equals(layoutMode, "companella", StringComparison.Ordinal))
                {
                    css += BuildCompanellaCssV2(nativeScale, companellaWidth);
                }
                else
                {
                    css +=
                        "html.mma-layout-default,html.mma-layout-custom{--mma-host-width:" + defaultWidth + ";}" +
                        "html.mma-layout-default .dashboard,html.mma-layout-custom .dashboard{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                        "html.mma-layout-default .main-card,html.mma-layout-custom .main-card{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                        "html.mma-layout-default .main-card,html.mma-layout-custom .main-card{height:" + NativePx(540, nativeScale) + "!important;min-height:" + NativePx(540, nativeScale) + "!important;max-height:" + NativePx(540, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-pattern,html.mma-layout-custom .main-card.bars-pattern{height:" + NativePx(575, nativeScale) + "!important;min-height:" + NativePx(575, nativeScale) + "!important;max-height:" + NativePx(575, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-graph,html.mma-layout-custom .main-card.bars-graph{height:" + NativePx(396, nativeScale) + "!important;min-height:" + NativePx(396, nativeScale) + "!important;max-height:" + NativePx(396, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-none,html.mma-layout-custom .main-card.bars-none{height:" + NativePx(248, nativeScale) + "!important;min-height:" + NativePx(248, nativeScale) + "!important;max-height:" + NativePx(248, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-etterna,html.mma-layout-custom .main-card.bars-etterna{height:" + NativePx(540, nativeScale) + "!important;min-height:" + NativePx(540, nativeScale) + "!important;max-height:" + NativePx(540, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-etterna.bars-etterna-compact,html.mma-layout-custom .main-card.bars-etterna.bars-etterna-compact{height:" + NativePx(500, nativeScale) + "!important;min-height:" + NativePx(500, nativeScale) + "!important;max-height:" + NativePx(500, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-full,html.mma-layout-custom .main-card.bars-full{height:auto!important;min-height:" + NativePx(540, nativeScale) + "!important;max-height:none!important;}";
                    if (string.Equals(layoutMode, "custom", StringComparison.Ordinal))
                    {
                        try
                        {
                            string cssPath = GetCustomCssPath();
                            if (File.Exists(cssPath))
                                customCss = File.ReadAllText(cssPath, System.Text.Encoding.UTF8);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            else
            {
                css =
                    "html{height:100%!important;overflow:hidden!important;}" +
                    "body{height:100%!important;min-height:100%!important;padding:18px!important;overflow:auto!important;}" +
                    ".dashboard{margin:0 auto!important;min-height:0!important;align-content:start!important;}";
            }

            if (!overlayMode)
            {
                css +=
                    "html,body{width:100%!important;height:100%!important;min-height:100%!important;background:#0e1016!important;overflow:auto!important;}" +
                    "body{padding:18px!important;margin:0!important;}" +
                    ".dashboard{min-height:0!important;margin:0 auto!important;gap:0!important;align-content:start!important;}" +
                    ".card.main-card{margin:0 auto!important;box-shadow:0 16px 38px rgba(0,0,0,.30)!important;}" +
                    "html.mma-layout-default,html.mma-layout-custom{--mma-host-width:min(" + defaultWidth + ",calc(100vw - 36px))!important;}" +
                    "html.mma-layout-horizontal{--mma-host-width:min(" + horizontalWidth + ",calc(100vw - 36px))!important;}" +
                    "html.mma-layout-companella{--mma-host-width:min(" + companellaWidth + ",calc(100vw - 36px))!important;}";
            }

            var serializer = new JavaScriptSerializer();
            string script =
                "(function(){" +
                "var s=document.getElementById('launcher-host-style');" +
                "if(!s){s=document.createElement('style');s.id='launcher-host-style';document.head.appendChild(s);}" +
                "s.textContent=" + serializer.Serialize(css) + ";" +
                "var c=document.getElementById('launcher-custom-style');" +
                "if(!c){c=document.createElement('style');c.id='launcher-custom-style';document.head.appendChild(c);}" +
                "c.textContent=" + serializer.Serialize(customCss) + ";" +
                "document.documentElement.classList.toggle('launcher-overlay-host',true);" +
                "document.documentElement.classList.toggle('mma-layout-default'," + (layoutMode == "default" ? "true" : "false") + ");" +
                "document.documentElement.classList.toggle('mma-layout-horizontal'," + (layoutMode == "horizontal" ? "true" : "false") + ");" +
                "document.documentElement.classList.toggle('mma-layout-companella'," + (layoutMode == "companella" ? "true" : "false") + ");" +
                "document.documentElement.classList.toggle('mma-layout-custom'," + (layoutMode == "custom" ? "true" : "false") + ");" +
                "var card=document.querySelector('.main-card');var details=document.getElementById('mma-host-details');" +
                "if(card&&" + (layoutMode == "horizontal" || layoutMode == "companella" ? "true" : "false") + "){" +
                "if(!details){details=document.createElement('div');details.id='mma-host-details';details.className='mma-host-details';" +
                "var anchor=card.querySelector('.mode-tag-group');card.insertBefore(details,anchor);" +
                "['sep-pattern','pattern-clusters','sep-etterna','ett-skill-bars','sep-graph','body-graph-wrap'].forEach(function(id){var n=document.getElementById(id);if(n)details.appendChild(n);});}}" +
                "else if(card&&details){while(details.firstChild)card.insertBefore(details.firstChild,details);details.remove();}" +
                "var compCover=document.getElementById('mma-comp-cover-layer');var compMeta=document.getElementById('mma-comp-meta');var compSummary=document.getElementById('mma-comp-summary');var compChart=document.getElementById('mma-comp-chart');" +
                "if(card&&" + (layoutMode == "companella" ? "true" : "false") + "){window.__mmaCompSignature='';" +
                "if(!compCover){compCover=document.createElement('div');compCover.id='mma-comp-cover-layer';compCover.className='mma-comp-cover-layer';card.insertBefore(compCover,card.firstChild);}" +
                "if(!compMeta){compMeta=document.createElement('div');compMeta.id='mma-comp-meta';compMeta.className='mma-comp-meta';compMeta.innerHTML=\"<div class='mma-comp-map'><span id='mma-comp-mapper'>" + UiText.Get("Ожидание данных карты", "Waiting for beatmap data") + "</span><span class='mma-comp-muted' id='mma-comp-version'></span></div><div class='mma-comp-numbers'><div id='mma-comp-stats'>BPM — · OD — · HP —</div><div class='mma-comp-muted' id='mma-comp-ids'>Set — · Map —</div></div>\";card.insertBefore(compMeta,details||card.querySelector('.mode-tag-group'));}" +
                "if(!compSummary){compSummary=document.createElement('div');compSummary.id='mma-comp-summary';compSummary.className='mma-comp-summary';compSummary.innerHTML=\"<div class='mma-comp-summary-item mma-comp-summary-rating'><span class='mma-comp-summary-label'>Star rating</span><strong class='mma-comp-summary-value' id='mma-summary-star'>—</strong><small class='mma-comp-summary-note' id='mma-summary-star-meta'>LN — · Keys —</small></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>BPM</span><strong class='mma-comp-summary-value' id='mma-summary-bpm'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Set</span><strong class='mma-comp-summary-value' id='mma-summary-set'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Map</span><strong class='mma-comp-summary-value' id='mma-summary-map'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Dan</span><strong class='mma-comp-summary-value' id='mma-summary-dan'>—</strong></div>\";card.insertBefore(compSummary,details||card.querySelector('.mode-tag-group'));}" +
                "if(!compChart){compChart=document.createElement('div');compChart.id='mma-comp-chart';compChart.className='mma-comp-chart';card.insertBefore(compChart,details||card.querySelector('.mode-tag-group'));}}" +
                "else{if(compCover)compCover.remove();if(compMeta)compMeta.remove();if(compSummary)compSummary.remove();if(compChart)compChart.remove();document.documentElement.style.removeProperty('--mma-comp-cover');}" +
                "})();";
            await browser.ExecuteScriptAsync(script);

            if (renderSelectedPreset)
            {
                string observerScript =
                    "(function(){" +
                    "var card=document.querySelector('.main-card');if(!card||!window.chrome||!chrome.webview)return;" +
                    "function report(){var r=card.getBoundingClientRect();var s=getComputedStyle(card);var dpr=Math.max(1,window.devicePixelRatio||1);" +
                    "chrome.webview.postMessage('mma:size:'+Math.ceil(r.width*dpr)+','+Math.ceil(r.height*dpr)+','+((parseFloat(s.borderTopLeftRadius)||0)*dpr));}" +
                    "function syncCompanellaSummary(){var source=document.getElementById('rework-star'),target=document.getElementById('mma-summary-star'),meta=document.getElementById('mma-summary-star-meta'),diff=document.getElementById('rework-diff'),dan=document.getElementById('mma-summary-dan');if(source&&target){var unit=source.getAttribute('data-unit')||'SR';target.textContent=((source.textContent||'—').trim()||'—')+(unit?' '+unit:'');}var sourceMeta=document.getElementById('rework-meta');if(sourceMeta&&meta)meta.textContent=(sourceMeta.textContent||'').replace(/\\s+/g,' ').trim()||'LN — · Keys —';if(diff&&dan)dan.textContent=(diff.textContent||'—').trim()||'—';}" +
                    "function syncCompanella(){if(!document.documentElement.classList.contains('mma-layout-companella'))return;syncCompanellaSummary();var chart=document.getElementById('mma-comp-chart');if(!chart)return;" +
                    "var p=document.getElementById('pattern-clusters'),e=document.getElementById('ett-skill-bars');var list=p&&!p.hidden?p:(e&&!e.hidden?e:null);var graph=document.getElementById('body-graph-wrap');var graphOnly=!list&&graph&&!graph.hidden;chart.hidden=!!graphOnly;var rows=list?Array.prototype.slice.call(list.children):[];var items=[];" +
                    "rows.forEach(function(row){if(row.classList.contains('empty')||row.classList.contains('skeleton'))return;var label=row.querySelector('.cluster-label,.ett-skill-label');var value=row.querySelector('.cluster-subtype,.ett-skill-head');var fill=row.querySelector('.cluster-fill,.ett-skill-fill');if(!label||!fill)return;var raw=fill.style.getPropertyValue('--bar-width')||getComputedStyle(fill).getPropertyValue('--bar-width')||fill.style.width||'0';var pct=parseFloat(raw);if(!isFinite(pct))pct=0;pct=Math.max(2,Math.min(100,pct));items.push({label:(label.textContent||'—').trim(),value:value?(value.textContent||'').trim():'',pct:pct});});" +
                    "items=items.slice(0,8);var signature=items.map(function(x){return x.label+'|'+x.value+'|'+x.pct;}).join('~');if(signature===window.__mmaCompSignature)return;window.__mmaCompSignature=signature;chart.textContent='';chart.style.setProperty('--mma-comp-count',String(Math.max(1,items.length)));var colors=['#dedee1','#58b8f0','#5fd56b','#ffae5c','#ae5be2','#ef5d72','#f4d95f','#66cdd0'];" +
                    "items.forEach(function(item,i){var col=document.createElement('div');col.className='mma-comp-column';var box=document.createElement('div');box.className='mma-comp-barbox';var bar=document.createElement('div');bar.className='mma-comp-bar';bar.style.setProperty('--mma-value',item.pct+'%');bar.style.setProperty('--mma-color',colors[i%colors.length]);box.appendChild(bar);var value=document.createElement('div');value.className='mma-comp-number';value.textContent=item.value||Math.round(item.pct);var label=document.createElement('div');label.className='mma-comp-label';label.textContent=item.label;col.appendChild(box);col.appendChild(label);col.appendChild(value);chart.appendChild(col);});}" +
                    "window.__mmaSyncCompanella=syncCompanella;" +
                    "function getNumber(obj,names){if(!obj)return null;for(var i=0;i<names.length;i++){var v=Number(obj[names[i]]);if(isFinite(v))return v;}return null;}" +
                    "function formatNumber(v){if(v===null||!isFinite(v))return '';return (Math.round(v*10)/10).toString();}" +
                    "function formatBpm(bm,stats){var bpm=bm.bpm||bm.BPM||(stats&&(stats.bpm||stats.BPM));if(bpm&&typeof bpm==='object'){var lo=getNumber(bpm,['min','minimum','lowest']);var hi=getNumber(bpm,['max','maximum','highest']);var common=getNumber(bpm,['common','base','current']);if(lo!==null&&hi!==null&&Math.abs(lo-hi)>.1)return formatNumber(lo)+'–'+formatNumber(hi)+' BPM';if(common!==null)return formatNumber(common)+' BPM';if(hi!==null)return formatNumber(hi)+' BPM';if(lo!==null)return formatNumber(lo)+' BPM';}var num=Number(bpm);return isFinite(num)&&num>0?formatNumber(num)+' BPM':'BPM —';}" +
                    "function updateCompanellaMeta(data){if(!document.documentElement.classList.contains('mma-layout-companella'))return;var bm=data&&data.beatmap;if(!bm)return;var md=bm.metadata||{};var stats=bm.stats||{};var mapper=String(bm.mapper||md.mapper||md.creator||'').trim();var version=String(bm.version||md.difficulty||md.version||'').trim();var mapEl=document.getElementById('mma-comp-mapper'),verEl=document.getElementById('mma-comp-version'),statsEl=document.getElementById('mma-comp-stats'),idsEl=document.getElementById('mma-comp-ids');if(mapEl)mapEl.textContent=mapper?'" + UiText.Get("Автор карты: ", "Mapped by ") + "'+mapper:'" + UiText.Get("Автор —", "Mapper —") + "';if(verEl)verEl.textContent=version?' · ['+version+']':'';" +
                    "var bpmText=formatBpm(bm,stats);var od=getNumber(stats,['OD','od','overallDifficulty']),hp=getNumber(stats,['HP','hp','drainRate']);var statParts=[bpmText];if(od!==null)statParts.push('OD '+formatNumber(od));if(hp!==null)statParts.push('HP '+formatNumber(hp));if(statsEl)statsEl.textContent=statParts.join(' · ');var mapId=bm.id||bm.beatmapId||'',setId=bm.set||bm.setId||bm.beatmapSetId||'';if(idsEl)idsEl.textContent='Set '+(setId||'—')+' · Map '+(mapId||'—');var bpmEl=document.getElementById('mma-summary-bpm'),setEl=document.getElementById('mma-summary-set'),mapIdEl=document.getElementById('mma-summary-map');if(bpmEl)bpmEl.textContent=bpmText.replace(/\\s*BPM$/i,'')||'—';if(setEl)setEl.textContent=setId||'—';if(mapIdEl)mapIdEl.textContent=mapId||'—';" +
                    "var title=String(bm.title||md.title||''),artist=String(bm.artist||md.artist||'');var identity=String(mapId||setId||(artist+'-'+title+'-'+version));if(identity&&identity!==window.__mmaCompCoverIdentity){window.__mmaCompCoverIdentity=identity;var cover='url(\"http://'+location.host+'/files/beatmap/background?ts='+encodeURIComponent(identity)+'\")';document.documentElement.style.setProperty('--mma-comp-cover',cover);}}" +
                    "window.__mmaUpdateCompanellaMeta=updateCompanellaMeta;" +
                    "function queueCompanella(){if(window.__mmaCompFrame)return;window.__mmaCompFrame=requestAnimationFrame(function(){window.__mmaCompFrame=0;if(window.__mmaSyncCompanella)window.__mmaSyncCompanella();});}" +
                    "if(!window.__mmaLauncherBound){window.__mmaLauncherBound=true;" +
                    "if(" + (overlayMode ? "true" : "false") + "){document.addEventListener('mousedown',function(e){if(e.button===0)chrome.webview.postMessage('mma:drag');},true);" +
                    "document.addEventListener('wheel',function(e){if(!e.ctrlKey)return;e.preventDefault();var now=Date.now();if(now-(window.__mmaScaleWheelAt||0)<160)return;window.__mmaScaleWheelAt=now;chrome.webview.postMessage('mma:scale:'+(e.deltaY<0?'5':'-5'));},{capture:true,passive:false});}" +
                    "window.addEventListener('resize',report);" +
                    "if(window.ResizeObserver)new ResizeObserver(report).observe(card);" +
                    "new MutationObserver(function(){report();queueCompanella();}).observe(card,{attributes:true,subtree:true,childList:true,characterData:true});}" +
                    "if(!window.__mmaPlayWatcherBound){window.__mmaPlayWatcherBound=true;var lastPlay=null;" +
                    "function connectPlayWatcher(){var ws=new WebSocket('ws://'+location.host+'/websocket/v2?l='+encodeURIComponent(window.COUNTER_PATH||location.pathname));" +
                    "window.__mmaPlayWatcherSocket=ws;" +
                    "ws.onopen=function(){ws.send('applyFilters:'+JSON.stringify([{field:'state',keys:['name']},{field:'beatmap',keys:['artist','title','version','mapper','id','set','setId','beatmapSetId','metadata','stats','bpm']}]));};" +
                    "ws.onmessage=function(e){try{var d=JSON.parse(e.data);var n=String(d&&d.state&&d.state.name||'').toLowerCase().replace(/[^a-z]/g,'');" +
                    "if(window.__mmaUpdateCompanellaMeta)window.__mmaUpdateCompanellaMeta(d);if(!n)return;var playing=n==='play'||n==='gameplay'||n==='playing';if(playing!==lastPlay){lastPlay=playing;chrome.webview.postMessage('mma:play:'+(playing?'1':'0'));}}catch(_){}};" +
                    "ws.onclose=function(){if(document.documentElement.classList.contains('launcher-overlay-host'))setTimeout(connectPlayWatcher,1000);};}" +
                    "connectPlayWatcher();}" +
                    "syncCompanella();report();setTimeout(function(){syncCompanella();report();},120);setTimeout(function(){syncCompanella();report();},600);" +
                    "})();";
                await browser.ExecuteScriptAsync(observerScript);
            }
        }

        private void OnBrowserWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!overlayMode)
                return;

            string message;
            try { message = args.TryGetWebMessageAsString(); }
            catch { return; }

            if (string.Equals(message, "mma:drag", StringComparison.Ordinal))
            {
                BeginOverlayDrag();
                return;
            }

            if (string.Equals(message, "mma:play:1", StringComparison.Ordinal))
            {
                SetOverlaySuppressedByPlay(true);
                return;
            }
            if (string.Equals(message, "mma:play:0", StringComparison.Ordinal))
            {
                SetOverlaySuppressedByPlay(false);
                return;
            }

            const string scalePrefix = "mma:scale:";
            if (message.StartsWith(scalePrefix, StringComparison.Ordinal))
            {
                int delta;
                if (int.TryParse(message.Substring(scalePrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out delta))
                    AdjustOverlayScale(delta);
                return;
            }

            const string prefix = "mma:size:";
            if (!message.StartsWith(prefix, StringComparison.Ordinal))
                return;

            string[] values = message.Substring(prefix.Length).Split(',');
            int width;
            int height;
            float radius;
            if (values.Length != 3 ||
                !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
                !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) ||
                !float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
                return;

            ResizeOverlayToWidget(width, height, radius);
        }

        private async Task CheckAndApplyUpdatesAsync()
        {
            string updateScript = Path.Combine(Application.StartupPath, "Update-ManiaMapAnalyzerOverlay.ps1");
            if (!File.Exists(updateScript))
            {
                startupUpdateSuffix = UiText.Get(" · проверка обновлений не установлена", " · update checker is not installed");
                return;
            }

            SetStatus(UiText.Get("Проверка обновлений…", "Checking for updates…"), null);
            ShowMessagePage(UiText.Get("Проверка обновлений", "Update check"), UiText.Get("Сверяю версии tosu, анализатора и osu!lazer…", "Checking tosu, analyser and osu!lazer versions…"), false);

            try
            {
                Dictionary<string, object> result = await Task.Run<Dictionary<string, object>>(delegate
                {
                    return RunUpdateScript(updateScript);
                });

                bool success = GetJsonBoolean(result, "Success");
                if (!success)
                {
                    string error = GetJsonString(result, "Error");
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? UiText.Get("Скрипт обновления завершился с ошибкой.", "The update script failed.") : error);
                }

                try
                {
                    string oldErrorLog = Path.Combine(Application.StartupPath, "startup-update-error.log");
                    if (File.Exists(oldErrorLog)) File.Delete(oldErrorLog);
                }
                catch
                {
                }

                startupCompatibility = GetJsonString(result, "Compatibility");
                bool updatedTosu = GetJsonBoolean(result, "UpdatedTosu");
                bool updatedAddon = GetJsonBoolean(result, "UpdatedAddon");
                string latestTosu = GetJsonString(result, "LatestTosu");
                string latestAddon = GetJsonString(result, "LatestAddon");

                if (updatedTosu || updatedAddon)
                {
                    startupUpdateSuffix = UiText.Get(" · обновлено", " · updated");
                    ShowMessagePage(
                        UiText.Get("Обновление установлено", "Update installed"),
                        "tosu " + latestTosu + "\nManiaMapAnalyser " + latestAddon + "\n\n" + UiText.Get("Запускаю анализатор…", "Starting the analyser…"),
                        false);
                }
                else if (string.Equals(startupCompatibility, "supported", StringComparison.OrdinalIgnoreCase))
                {
                    startupUpdateSuffix = UiText.Get(" · актуально", " · up to date");
                }
                else if (string.Equals(startupCompatibility, "unsupported", StringComparison.OrdinalIgnoreCase))
                {
                    string lazerVersion = GetJsonString(result, "LazerVersion");
                    startupUpdateSuffix = UiText.Get(" · lazer пока не поддерживается", " · lazer is not supported yet");
                    MessageBox.Show(
                        UiText.Get("Для osu!lazer " + lazerVersion + " ещё нет официального файла совместимости tosu.\r\n\r\nПриложение запустится, но часть данных может быть недоступна. Проверка повторится при следующем запуске.",
                            "There is no official tosu compatibility file for osu!lazer " + lazerVersion + " yet.\r\n\r\nThe application will start, but some data may be unavailable. It will check again next time."),
                        UiText.Get("Совместимость osu!lazer", "osu!lazer compatibility"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else
                {
                    startupUpdateSuffix = UiText.Get(" · lazer не обнаружен", " · lazer not detected");
                }
            }
            catch (Exception ex)
            {
                startupUpdateSuffix = UiText.Get(" · обновления не проверены", " · updates not checked");
                try
                {
                    File.WriteAllText(
                        Path.Combine(Application.StartupPath, "startup-update-error.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + ex,
                        System.Text.Encoding.UTF8);
                }
                catch
                {
                }
            }
        }

        private static Dictionary<string, object> RunUpdateScript(string scriptPath)
        {
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = "powershell.exe";
            startInfo.Arguments =
                "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(scriptPath) +
                " -InstallPath " + QuoteArgument(Application.StartupPath) +
                " -ComponentsOnly -Json -Quiet";
            startInfo.WorkingDirectory = Application.StartupPath;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new InvalidOperationException(UiText.Get("Не удалось запустить скрипт обновления.", "Could not start the update script."));

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(180000))
                {
                    process.Kill();
                    throw new TimeoutException(UiText.Get("Проверка обновлений заняла больше трёх минут.", "The update check took longer than three minutes."));
                }

                Task.WaitAll(outputTask, errorTask);
                string output = outputTask.Result;
                string error = errorTask.Result;

                if (string.IsNullOrWhiteSpace(output))
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? UiText.Get("Скрипт обновления не вернул результат.", "The update script returned no result.") : error);

                var serializer = new JavaScriptSerializer();
                Dictionary<string, object> result = serializer.Deserialize<Dictionary<string, object>>(output.Trim());
                if (result == null)
                    throw new InvalidOperationException(UiText.Get("Не удалось прочитать результат проверки обновлений.", "Could not read the update-check result."));
                return result;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string GetJsonString(Dictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }

        private static bool GetJsonBoolean(Dictionary<string, object> source, string key)
        {
            object value;
            if (!source.TryGetValue(key, out value) || value == null)
                return false;
            try { return Convert.ToBoolean(value); }
            catch { return false; }
        }

        private void OnBeatmapResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                Uri requestUri = new Uri(args.Request.Uri);
                string resourceName;
                string contentType;

                if (requestUri.AbsolutePath.EndsWith("/file", StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = "beatmap";
                    contentType = "text/plain; charset=utf-8";
                }
                else if (requestUri.AbsolutePath.EndsWith("/background", StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = "background";
                    contentType = "application/octet-stream";
                }
                else if (requestUri.AbsolutePath.EndsWith("/audio", StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = "audio";
                    contentType = "audio/mpeg";
                }
                else
                {
                    return;
                }

                string json = DownloadLocalJson(BaseUrl + "/json/v2");
                var serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);

                object clientValue;
                if (!root.TryGetValue("client", out clientValue) ||
                    !string.Equals(Convert.ToString(clientValue), "lazer", StringComparison.OrdinalIgnoreCase))
                    return;

                object filesValue;
                if (!root.TryGetValue("files", out filesValue))
                    return;

                Dictionary<string, object> files = filesValue as Dictionary<string, object>;
                if (files == null)
                    return;

                object relativeValue;
                if (!files.TryGetValue(resourceName, out relativeValue))
                    return;

                string relativePath = Convert.ToString(relativeValue);
                if (string.IsNullOrWhiteSpace(relativePath))
                    return;

                string storageRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "osu",
                    "files");
                string normalizedRoot = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string filePath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));

                if (!filePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                    return;

                byte[] content = File.ReadAllBytes(filePath);
                if (resourceName == "background")
                    contentType = DetectImageContentType(content);

                var stream = new MemoryStream(content, false);
                string headers =
                    "Content-Type: " + contentType + "\r\n" +
                    "Content-Length: " + content.Length + "\r\n" +
                    "Access-Control-Allow-Origin: *\r\n" +
                    "Cache-Control: no-store";
                args.Response = browser.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream,
                    200,
                    "OK",
                    headers);
            }
            catch
            {
                // If the lazer bridge cannot resolve a file, let tosu handle the request normally.
            }
        }

        private static string DownloadLocalJson(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 1500;
            request.ReadWriteTimeout = 1500;
            request.Proxy = null;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            using (var reader = new StreamReader(responseStream))
                return reader.ReadToEnd();
        }

        private static string DetectImageContentType(byte[] data)
        {
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return "image/png";
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return "image/jpeg";
            if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
                return "image/gif";
            if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
                return "image/webp";
            return "application/octet-stream";
        }

        private void EnterOverlayMode()
        {
            if (overlayMode || !browserReady)
                return;

            if (!overlayExitHotkeyRegistered)
            {
                MessageBox.Show(
                    UiText.Get("Не удалось зарегистрировать Ctrl+Shift+F10. Режим оверлея не включён, чтобы окно не оказалось недоступным.", "Ctrl+Shift+F10 could not be registered. Overlay mode was not enabled so the window cannot become inaccessible."),
                    UiText.Get("Горячая клавиша занята", "Hotkey is unavailable"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (launcherSettings.OverlayHintVersion < 3)
            {
                MessageBox.Show(
                    UiText.Get(
                        "В оверлее останется только сам виджет — без рамки и панели.\r\n\r\nПеретащите виджет мышью в нужное место.\r\nCtrl + колесо мыши — изменить размер.\r\nCtrl+Shift+F9 — включить или отключить сквозной клик.\r\nCtrl+Shift+F10 — выйти из оверлея и вернуть обычное окно.\r\n\r\nФормат, масштаб и пользовательский CSS настраиваются кнопкой «Оформление».\r\n\r\nДля exclusive fullscreen используйте встроенный In-Game Overlay в настройках tosu — он тяжелее.",
                        "Only the widget remains in overlay mode—without the frame or toolbar.\r\n\r\nDrag the widget to position it.\r\nCtrl + mouse wheel — resize.\r\nCtrl+Shift+F9 — toggle click-through.\r\nCtrl+Shift+F10 — leave overlay mode and restore the window.\r\n\r\nChoose the layout, scale and custom CSS with the Appearance button.\r\n\r\nFor exclusive fullscreen, use tosu's built-in In-Game Overlay; it uses more resources."),
                    UiText.Get("Режим оверлея", "Overlay mode"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                launcherSettings.OverlayHintShown = true;
                launcherSettings.OverlayHintVersion = 3;
                SaveLauncherSettings();
            }

            normalBounds = Bounds;
            overlayMode = true;
            overlayClickThrough = false;
            overlayWidgetSized = false;
            overlayPlayStateKnown = false;
            overlaySuppressedByPlay = false;
            overlayInputBeforePlay = false;
            Opacity = 0D;

            SuspendLayout();
            FormBorderStyle = FormBorderStyle.None;
            MinimumSize = Size.Empty;
            MaximizeBox = false;
            TopMost = true;
            topBar.Visible = false;
            layout.RowStyles[0].Height = 0F;
            browser.DefaultBackgroundColor = Color.FromArgb(0, 0, 0, 0);

            string activeLayout = GetOverlayLayoutMode();
            bool horizontalLayout = string.Equals(activeLayout, "horizontal", StringComparison.Ordinal);
            bool companellaLayout = string.Equals(activeLayout, "companella", StringComparison.Ordinal);
            double initialScale = GetOverlayScalePercent() / 100D;
            int initialWidth = (int)Math.Ceiling((horizontalLayout ? 920 : companellaLayout ? 620 : 475) * initialScale);
            int initialHeight = (int)Math.Ceiling((horizontalLayout ? 360 : companellaLayout ? 320 : 540) * initialScale);
            Rectangle target = new Rectangle(
                launcherSettings.OverlayX,
                launcherSettings.OverlayY,
                initialWidth,
                initialHeight);
            Rectangle working = Screen.FromControl(this).WorkingArea;
            if (!IsRectangleVisible(target))
            {
                target = new Rectangle(
                    working.Right - Math.Min(initialWidth, working.Width) - 18,
                    working.Top + 18,
                    Math.Min(initialWidth, working.Width),
                    Math.Min(initialHeight, working.Height));
            }
            Bounds = target;
            ResumeLayout(true);

            Navigate(OverlayUrl);

            var revealTimer = new System.Windows.Forms.Timer();
            revealTimer.Interval = 1200;
            revealTimer.Tick += delegate
            {
                revealTimer.Stop();
                revealTimer.Dispose();
                if (overlayMode && !overlaySuppressedByPlay && (!overlayWidgetSized || !overlayPlayStateKnown))
                {
                    ApplyRoundedWindowRegion(ClientSize.Width, ClientSize.Height, 16);
                    Opacity = 1D;
                }
            };
            revealTimer.Start();
        }

        private void LeaveOverlayMode()
        {
            if (!overlayMode)
                return;

            SetOverlayClickThrough(false);
            SaveOverlayBounds();
            overlayMode = false;
            overlayWidgetSized = false;
            overlayPlayStateKnown = false;
            overlaySuppressedByPlay = false;
            overlayInputBeforePlay = false;
            Opacity = 1D;

            SuspendLayout();
            Region oldRegion = Region;
            Region = null;
            if (oldRegion != null)
                oldRegion.Dispose();
            layout.RowStyles[0].Height = 108F;
            topBar.Visible = true;
            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(650, 720);
            MaximizeBox = true;
            browser.DefaultBackgroundColor = Color.FromArgb(14, 16, 22);
            if (!normalBounds.IsEmpty)
                Bounds = normalBounds;
            ResumeLayout(true);

            Navigate(OverlayUrl);
            Activate();
        }

        private void ToggleOverlayInput()
        {
            if (!overlayMode)
                return;

            if (!overlayInputHotkeyRegistered)
            {
                MessageBox.Show(
                    UiText.Get("Не удалось зарегистрировать Ctrl+Shift+F9. Сквозной клик недоступен, но выйти можно через Ctrl+Shift+F10.", "Ctrl+Shift+F9 could not be registered. Click-through is unavailable, but Ctrl+Shift+F10 still exits overlay mode."),
                    UiText.Get("Горячая клавиша занята", "Hotkey is unavailable"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (overlaySuppressedByPlay)
            {
                overlayInputBeforePlay = !overlayInputBeforePlay;
                return;
            }

            SaveOverlayBounds();
            SetOverlayClickThrough(!overlayClickThrough);
        }

        private void SetOverlaySuppressedByPlay(bool suppressed)
        {
            if (!overlayMode)
                return;

            overlayPlayStateKnown = true;
            if (overlaySuppressedByPlay == suppressed)
            {
                if (!suppressed && overlayWidgetSized)
                    Opacity = 1D;
                return;
            }

            overlaySuppressedByPlay = suppressed;
            if (suppressed)
            {
                overlayInputBeforePlay = overlayClickThrough;
                SetOverlayClickThrough(true);
                Opacity = 0D;
            }
            else
            {
                SetOverlayClickThrough(overlayInputBeforePlay);
                if (overlayWidgetSized)
                    Opacity = 1D;
            }
        }

        private void SetOverlayClickThrough(bool enabled)
        {
            if (!IsHandleCreated)
                return;

            int style = GetWindowLong(Handle, GwlExStyle);
            if (enabled)
                style |= WsExTransparent;
            else
                style &= ~WsExTransparent;
            SetWindowLong(Handle, GwlExStyle, style);
            overlayClickThrough = enabled;
        }

        private void BeginOverlayDrag()
        {
            if (!overlayMode || overlayClickThrough)
                return;

            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }

        private void ResizeOverlayToWidget(int width, int height, float radius)
        {
            if (!overlayMode || width < 120 || height < 80 || width > 2000 || height > 3000)
                return;

            Point location = Location;
            ClientSize = new Size(width, height);
            Location = location;
            ApplyRoundedWindowRegion(width, height, (int)Math.Ceiling(radius));
            overlayWidgetSized = true;
            if (overlayPlayStateKnown && !overlaySuppressedByPlay)
                Opacity = 1D;
            SaveOverlayBounds();
        }

        private void ApplyRoundedWindowRegion(int width, int height, int radius)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
            Region nextRegion;
            if (radius == 0)
            {
                nextRegion = new Region(new Rectangle(0, 0, width, height));
            }
            else
            {
                int diameter = radius * 2;
                using (var path = new GraphicsPath())
                {
                    path.AddArc(0, 0, diameter, diameter, 180, 90);
                    path.AddArc(width - diameter, 0, diameter, diameter, 270, 90);
                    path.AddArc(width - diameter, height - diameter, diameter, diameter, 0, 90);
                    path.AddArc(0, height - diameter, diameter, diameter, 90, 90);
                    path.CloseFigure();
                    nextRegion = new Region(path);
                }
            }

            Region oldRegion = Region;
            Region = nextRegion;
            if (oldRegion != null)
                oldRegion.Dispose();
        }

        private static bool IsRectangleVisible(Rectangle bounds)
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle intersection = Rectangle.Intersect(screen.WorkingArea, bounds);
                if (intersection.Width >= 120 && intersection.Height >= 80)
                    return true;
            }
            return false;
        }

        private void SaveOverlayBounds()
        {
            if (!overlayMode)
                return;

            launcherSettings.OverlayX = Bounds.X;
            launcherSettings.OverlayY = Bounds.Y;
            launcherSettings.OverlayWidth = Bounds.Width;
            launcherSettings.OverlayHeight = Bounds.Height;
            SaveLauncherSettings();
        }

        private static string GetLauncherSettingsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ManiaMapAnalyzerOverlay",
                "launcher-settings.json");
        }

        private static LauncherSettings LoadLauncherSettings()
        {
            try
            {
                string path = GetLauncherSettingsPath();
                if (File.Exists(path))
                {
                    var serializer = new JavaScriptSerializer();
                    LauncherSettings settings = serializer.Deserialize<LauncherSettings>(File.ReadAllText(path));
                    if (settings != null)
                        return settings;
                }
            }
            catch
            {
            }
            return new LauncherSettings();
        }

        private void SaveLauncherSettings()
        {
            try
            {
                string path = GetLauncherSettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(path, serializer.Serialize(launcherSettings), System.Text.Encoding.UTF8);
            }
            catch
            {
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            overlayExitHotkeyRegistered = RegisterHotKey(Handle, OverlayExitHotkeyId, ModControl | ModShift, (uint)Keys.F10);
            overlayInputHotkeyRegistered = RegisterHotKey(Handle, OverlayInputHotkeyId, ModControl | ModShift, (uint)Keys.F9);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (overlayExitHotkeyRegistered)
            {
                UnregisterHotKey(Handle, OverlayExitHotkeyId);
                overlayExitHotkeyRegistered = false;
            }
            if (overlayInputHotkeyRegistered)
            {
                UnregisterHotKey(Handle, OverlayInputHotkeyId);
                overlayInputHotkeyRegistered = false;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == OverlayExitHotkeyId)
            {
                if (overlayMode)
                    LeaveOverlayMode();
                return;
            }
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == OverlayInputHotkeyId)
            {
                if (overlayMode)
                    ToggleOverlayInput();
                return;
            }
            base.WndProc(ref m);
        }

        private async Task RestartTosuAsync()
        {
            restartButton.Enabled = false;
            analysisButton.Enabled = false;
            designButton.Enabled = false;
            overlayButton.Enabled = false;
            dashboardButton.Enabled = false;
            SetStatus(UiText.Get("Перезапуск tosu…", "Restarting tosu…"), null);
            ShutdownTosu();
            await Task.Delay(500);
            await StartTosuAsync();
        }

        private async Task StartTosuAsync()
        {
            string executable = Path.Combine(Application.StartupPath, "tosu", "tosu.exe");
            if (!File.Exists(executable))
            {
                SetStatus(UiText.Get("Файл tosu.exe не найден", "tosu.exe was not found"), false);
                ShowMessagePage(UiText.Get("tosu не найден", "tosu was not found"), UiText.Get("Проверьте, что папка tosu лежит рядом с приложением.", "Make sure the tosu folder is next to the application."), true);
                restartButton.Enabled = true;
                return;
            }

            try
            {
                StopStaleBundledInstances(executable);
                CreateKillOnCloseJob();

                var startInfo = new ProcessStartInfo();
                startInfo.FileName = executable;
                startInfo.WorkingDirectory = Path.GetDirectoryName(executable);
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;

                tosuProcess = Process.Start(startInfo);
                if (tosuProcess == null)
                    throw new InvalidOperationException(UiText.Get("Windows не вернула процесс tosu.", "Windows did not return the tosu process."));

                if (!AssignProcessToJobObject(jobHandle, tosuProcess.Handle))
                    throw new InvalidOperationException(UiText.Get("Не удалось привязать процесс tosu к приложению.", "Could not attach the tosu process to the application."));

                tosuProcess.EnableRaisingEvents = true;
                tosuProcess.Exited += OnTosuExited;
                SetStatus(UiText.Get("tosu запускается…", "tosu is starting…"), null);

                bool ready = await Task.Run<bool>(delegate { return WaitForServer(25); });
                if (closing)
                    return;

                if (!ready || tosuProcess == null || tosuProcess.HasExited)
                    throw new InvalidOperationException(UiText.Get("tosu не открыл локальный сервер на порту 24050.", "tosu did not open its local server on port 24050."));

                bool? healthy = string.Equals(startupCompatibility, "unsupported", StringComparison.OrdinalIgnoreCase)
                    ? (bool?)null
                    : true;
                SetStatus(UiText.Get("tosu работает", "tosu is running") + startupUpdateSuffix, healthy);
                analysisButton.Enabled = true;
                designButton.Enabled = true;
                overlayButton.Enabled = true;
                dashboardButton.Enabled = true;
                restartButton.Enabled = true;
                Navigate(OverlayUrl);
            }
            catch (Exception ex)
            {
                ShutdownTosu();
                SetStatus(UiText.Get("tosu не запущен", "tosu is not running"), false);
                ShowMessagePage(UiText.Get("Не удалось запустить tosu", "Could not start tosu"), ex.Message + "\n\n" + UiText.Get("Нажмите «Перезапустить», чтобы попробовать ещё раз.", "Click Restart to try again."), true);
                restartButton.Enabled = true;
            }
        }

        private bool WaitForServer(int timeoutSeconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds) && !closing)
            {
                try
                {
                    if (tosuProcess == null || tosuProcess.HasExited)
                        return false;

                    var request = (HttpWebRequest)WebRequest.Create(OverlayUrl);
                    request.Timeout = 1000;
                    request.ReadWriteTimeout = 1000;
                    request.Proxy = null;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if (response.StatusCode == HttpStatusCode.OK)
                            return true;
                    }
                }
                catch
                {
                }

                Thread.Sleep(300);
            }
            return false;
        }

        private void Navigate(string url)
        {
            if (browserReady && browser.CoreWebView2 != null)
                browser.CoreWebView2.Navigate(url);
        }

        private void ShowMessagePage(string title, string message, bool error)
        {
            if (!browserReady)
                return;

            string accent = error ? "#ff5f7e" : "#8a7dff";
            string safeTitle = WebUtility.HtmlEncode(title);
            string safeMessage = WebUtility.HtmlEncode(message).Replace("\n", "<br>");
            string html = "<!doctype html><html><head><meta charset='utf-8'><style>" +
                "html,body{height:100%;margin:0;background:#0e1016;color:#f4f6fc;font-family:'Segoe UI',sans-serif}" +
                "body{display:grid;place-items:center}.box{max-width:520px;padding:42px;text-align:center}" +
                ".ring{width:42px;height:42px;margin:0 auto 22px;border:4px solid #292d3a;border-top-color:" + accent + ";border-radius:50%;animation:r 1s linear infinite}" +
                ".error{animation:none;border-color:" + accent + "}h1{font-size:24px;margin:0 0 12px}p{color:#aeb5c8;line-height:1.55;margin:0}" +
                "@keyframes r{to{transform:rotate(360deg)}}</style></head><body><div class='box'><div class='ring" +
                (error ? " error" : "") + "'></div><h1>" + safeTitle + "</h1><p>" + safeMessage + "</p></div></body></html>";
            browser.NavigateToString(html);
        }

        private void SetStatus(string text, bool? healthy)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, bool?>(SetStatus), text, healthy);
                return;
            }

            statusLabel.Text = text;
            if (healthy == true)
                statusDot.BackColor = Color.FromArgb(61, 207, 142);
            else if (healthy == false)
                statusDot.BackColor = Color.FromArgb(255, 95, 126);
            else
                statusDot.BackColor = Color.FromArgb(245, 179, 66);
        }

        private void OnTosuExited(object sender, EventArgs e)
        {
            if (closing || IsDisposed || !IsHandleCreated)
                return;

            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (closing)
                        return;
                    analysisButton.Enabled = false;
                    designButton.Enabled = false;
                    overlayButton.Enabled = false;
                    dashboardButton.Enabled = false;
                    restartButton.Enabled = true;
                    SetStatus(UiText.Get("tosu остановлен", "tosu has stopped"), false);
                    ShowMessagePage(UiText.Get("tosu остановлен", "tosu has stopped"), UiText.Get("Нажмите «Перезапустить», чтобы снова включить анализ карты.", "Click Restart to enable map analysis again."), true);
                }));
            }
            catch
            {
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            SaveOverlayBounds();
            SetOverlayClickThrough(false);
            closing = true;
            ShutdownTosu();
        }

        private void StopStaleBundledInstances(string expectedPath)
        {
            string normalizedExpected = Path.GetFullPath(expectedPath);
            foreach (Process process in Process.GetProcessesByName("tosu"))
            {
                try
                {
                    string processPath = process.MainModule.FileName;
                    if (string.Equals(Path.GetFullPath(processPath), normalizedExpected, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private void ShutdownTosu()
        {
            Process process = tosuProcess;
            tosuProcess = null;

            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
                jobHandle = IntPtr.Zero;
            }

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private void CreateKillOnCloseJob()
        {
            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
                jobHandle = IntPtr.Zero;
            }

            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero)
                throw new InvalidOperationException(UiText.Get("Windows не удалось создать объект контроля процесса.", "Windows could not create the process-control object."));

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, pointer, (uint)length))
                    throw new InvalidOperationException(UiText.Get("Windows не удалось настроить контроль процесса tosu.", "Windows could not configure tosu process control."));
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr window, int index, int newStyle);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }

    internal sealed class OverlayStyleDialog : Form
    {
        private readonly ComboBox layoutBox;
        private readonly TrackBar scaleTrack;
        private readonly Label scaleValue;
        private readonly Label description;
        private readonly string customCssPath;
        private readonly bool english;

        public string LayoutMode { get; private set; }
        public int ScalePercent { get; private set; }

        public OverlayStyleDialog(string layoutMode, int scalePercent, string cssPath, bool useEnglish)
        {
            english = useEnglish;
            customCssPath = cssPath;
            Text = Pick("Оформление оверлея", "Overlay appearance");
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(620, 430);
            BackColor = Color.FromArgb(18, 21, 29);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            var title = new Label();
            title.Text = Pick("Вид и размер оверлея", "Overlay layout and size");
            title.Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point);
            title.AutoSize = true;
            title.Location = new Point(22, 18);

            var subtitle = new Label();
            subtitle.Text = Pick("Выберите готовый формат или подключите обычный CSS-файл.", "Choose a preset or use a regular CSS file.");
            subtitle.ForeColor = Color.FromArgb(166, 174, 196);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(25, 55);

            var layoutLabel = CreateCaption(Pick("Формат", "Layout"), 24, 92);
            layoutBox = new ComboBox();
            layoutBox.DropDownStyle = ComboBoxStyle.DropDownList;
            layoutBox.FlatStyle = FlatStyle.Flat;
            layoutBox.BackColor = Color.FromArgb(38, 43, 56);
            layoutBox.ForeColor = Color.White;
            layoutBox.Location = new Point(24, 114);
            layoutBox.Size = new Size(270, 28);
            layoutBox.Items.AddRange(english
                ? new object[] { "Default", "Horizontal", "Companella", "Custom CSS" }
                : new object[] { "По умолчанию", "Горизонтальный", "Companella", "Пользовательский CSS" });
            layoutBox.SelectedIndex = layoutMode == "horizontal" ? 1 : layoutMode == "companella" ? 2 : layoutMode == "custom" ? 3 : 0;

            description = new Label();
            description.Location = new Point(314, 94);
            description.Size = new Size(282, 58);
            description.ForeColor = Color.FromArgb(190, 198, 218);

            var scaleLabel = CreateCaption(Pick("Размер оверлея", "Overlay size"), 24, 169);
            scaleTrack = new TrackBar();
            scaleTrack.Minimum = 50;
            scaleTrack.Maximum = 180;
            scaleTrack.TickFrequency = 10;
            scaleTrack.SmallChange = 5;
            scaleTrack.LargeChange = 10;
            scaleTrack.Value = Math.Max(50, Math.Min(180, scalePercent));
            scaleTrack.Location = new Point(18, 191);
            scaleTrack.Size = new Size(500, 45);
            scaleTrack.BackColor = BackColor;

            scaleValue = new Label();
            scaleValue.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point);
            scaleValue.TextAlign = ContentAlignment.MiddleCenter;
            scaleValue.Location = new Point(525, 191);
            scaleValue.Size = new Size(70, 34);
            scaleValue.BackColor = Color.FromArgb(38, 43, 56);

            var wheelHint = new Label();
            wheelHint.Text = Pick("Ctrl + колесо мыши меняет нативный размер без размытия текста.", "Ctrl + mouse wheel changes the native size without blurring text.");
            wheelHint.AutoSize = true;
            wheelHint.ForeColor = Color.FromArgb(139, 184, 218);
            wheelHint.Location = new Point(25, 237);

            var cssLabel = CreateCaption(Pick("Файл пользовательского стиля", "Custom style file"), 24, 273);
            var cssPathBox = new TextBox();
            cssPathBox.ReadOnly = true;
            cssPathBox.Text = customCssPath;
            cssPathBox.BackColor = Color.FromArgb(31, 35, 46);
            cssPathBox.ForeColor = Color.FromArgb(210, 215, 229);
            cssPathBox.BorderStyle = BorderStyle.FixedSingle;
            cssPathBox.Location = new Point(24, 295);
            cssPathBox.Size = new Size(572, 24);

            var openCssButton = CreateDialogButton(Pick("Открыть CSS", "Open CSS"), 24, 330, 120);
            openCssButton.Click += delegate { OpenCustomCss(); };

            var addonSettingsButton = CreateDialogButton(Pick("Настройки анализатора", "Analyser settings"), 153, 330, 175);
            addonSettingsButton.Click += delegate
            {
                DialogResult = DialogResult.Yes;
                Close();
            };

            var cancelButton = CreateDialogButton(Pick("Отмена", "Cancel"), 399, 376, 92);
            cancelButton.DialogResult = DialogResult.Cancel;
            var applyButton = CreateDialogButton(Pick("Применить", "Apply"), 500, 376, 96);
            applyButton.BackColor = Color.FromArgb(51, 105, 145);
            applyButton.Click += delegate
            {
                LayoutMode = layoutBox.SelectedIndex == 1 ? "horizontal" : layoutBox.SelectedIndex == 2 ? "companella" : layoutBox.SelectedIndex == 3 ? "custom" : "default";
                ScalePercent = scaleTrack.Value;
                DialogResult = DialogResult.OK;
                Close();
            };

            int previousLayoutIndex = layoutBox.SelectedIndex;
            layoutBox.SelectedIndexChanged += delegate
            {
                if (layoutBox.SelectedIndex == 2 && previousLayoutIndex != 2)
                {
                    scaleTrack.Value = 100;
                    UpdateScaleLabel();
                }
                previousLayoutIndex = layoutBox.SelectedIndex;
                UpdateDescription();
            };
            scaleTrack.Scroll += delegate { UpdateScaleLabel(); };

            Controls.Add(title);
            Controls.Add(subtitle);
            Controls.Add(layoutLabel);
            Controls.Add(layoutBox);
            Controls.Add(description);
            Controls.Add(scaleLabel);
            Controls.Add(scaleTrack);
            Controls.Add(scaleValue);
            Controls.Add(wheelHint);
            Controls.Add(cssLabel);
            Controls.Add(cssPathBox);
            Controls.Add(openCssButton);
            Controls.Add(addonSettingsButton);
            Controls.Add(cancelButton);
            Controls.Add(applyButton);

            AcceptButton = applyButton;
            CancelButton = cancelButton;
            UpdateDescription();
            UpdateScaleLabel();
        }

        private string Pick(string russian, string englishText)
        {
            return english ? englishText : russian;
        }

        private Label CreateCaption(string text, int x, int y)
        {
            var label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Location = new Point(x, y);
            label.ForeColor = Color.FromArgb(232, 235, 244);
            label.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            return label;
        }

        private static Button CreateDialogButton(string text, int x, int y, int width)
        {
            var button = new Button();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(width, 34);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Color.FromArgb(45, 50, 64);
            button.ForeColor = Color.White;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private void UpdateDescription()
        {
            if (layoutBox.SelectedIndex == 1)
                description.Text = Pick("Широкая компоновка: оценка слева, полосы и график справа. Удобно размещать сверху или снизу экрана.", "Wide layout: rating on the left, bars and graph on the right. Suitable for the top or bottom of the screen.");
            else if (layoutBox.SelectedIndex == 2)
                description.Text = Pick("Самодостаточная компактная панель на 100%: обложка карты, вертикальные показатели, подробные строки и оценка.", "Compact 100% preset with cover art, vertical metrics, full descriptions and rating.");
            else if (layoutBox.SelectedIndex == 3)
                description.Text = Pick("Стиль берётся из overlay-custom.css. После сохранения файла снова нажмите «Применить».", "Styles are loaded from overlay-custom.css. Save the file and click Apply again.");
            else
                description.Text = Pick("Компактная вертикальная карточка. Подходит для размещения сбоку от игрового поля.", "Compact vertical card for placement beside the playfield.");
        }

        private void UpdateScaleLabel()
        {
            scaleValue.Text = scaleTrack.Value.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void OpenCustomCss()
        {
            try
            {
                if (!File.Exists(customCssPath))
                    throw new FileNotFoundException(Pick("CSS-файл не найден.", "CSS file was not found."), customCssPath);
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = "notepad.exe";
                startInfo.Arguments = "\"" + customCssPath + "\"";
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Pick("Не удалось открыть CSS-файл.\r\n\r\n", "Could not open the CSS file.\r\n\r\n") + ex.Message,
                    Pick("Оформление оверлея", "Overlay appearance"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    internal sealed class LauncherSettings
    {
        public int OverlayX { get; set; }
        public int OverlayY { get; set; }
        public int OverlayWidth { get; set; }
        public int OverlayHeight { get; set; }
        public bool OverlayHintShown { get; set; }
        public int OverlayHintVersion { get; set; }
        public string OverlayLayoutMode { get; set; }
        public int OverlayScalePercent { get; set; }
        public int CompanellaLayoutVersion { get; set; }
        public string Language { get; set; }

        public LauncherSettings()
        {
            OverlayX = -32000;
            OverlayY = -32000;
            OverlayWidth = 520;
            OverlayHeight = 650;
            OverlayHintVersion = 0;
            OverlayLayoutMode = "default";
            OverlayScalePercent = 100;
            CompanellaLayoutVersion = 0;
            Language = string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        }
    }
}
