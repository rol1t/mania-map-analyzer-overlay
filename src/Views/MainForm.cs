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

namespace ManiaMapAnalyzerOverlay
{
    internal sealed partial class MainForm : Form
    {
        private const string BaseUrl = "http://127.0.0.1:24050";
        private const string OverlayUrl = BaseUrl + "/ManiaMapAnalyser/?launcher=4";
        private const string DesignUrl = BaseUrl + "/settings?overlay=ManiaMapAnalyser";
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
            CustomCssService.EnsureExists();

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

    }
}
