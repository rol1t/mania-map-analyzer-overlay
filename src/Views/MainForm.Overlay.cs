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
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                if (overlayMode)
                {
                    parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
                    if (overlayClickThrough)
                        parameters.ExStyle |= WsExTransparent;
                }
                return parameters;
            }
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
                        "В оверлее останется только сам виджет — без рамки и панели.\r\n\r\nПо умолчанию мышь полностью проходит через него в игру.\r\nCtrl+Shift+F9 — разблокировать виджет для перемещения.\r\nCtrl + колесо мыши — изменить размер после разблокировки.\r\nCtrl+Shift+F10 — выйти из оверлея и вернуть обычное окно.\r\n\r\nФормат, масштаб и пользовательский CSS настраиваются кнопкой «Оформление».\r\n\r\nДля exclusive fullscreen используйте встроенный In-Game Overlay в настройках tosu — он тяжелее.",
                        "Only the widget remains in overlay mode—without the frame or toolbar.\r\n\r\nMouse input passes through it to the game by default.\r\nCtrl+Shift+F9 — unlock the widget for positioning.\r\nCtrl + mouse wheel — resize it after unlocking.\r\nCtrl+Shift+F10 — leave overlay mode and restore the window.\r\n\r\nChoose the layout, scale and custom CSS with the Appearance button.\r\n\r\nFor exclusive fullscreen, use tosu's built-in In-Game Overlay; it uses more resources."),
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
            // Apply these flags after WinForms has finished changing the border and
            // bounds; those operations can recreate the native window and reset styles.
            RefreshOverlayWindowStyles();
            SetOverlayClickThrough(true);
            ReturnFocusToOsu();
            StartOverlayInputGuard();

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

            StopOverlayInputGuard();
            SetOverlayClickThrough(false);
            SaveOverlayBounds();
            overlayMode = false;
            overlayWidgetSized = false;
            overlayPlayStateKnown = false;
            overlaySuppressedByPlay = false;
            overlayInputBeforePlay = false;
            Opacity = 1D;
            RefreshOverlayWindowStyles();

            SuspendLayout();
            Region oldRegion = Region;
            Region = null;
            if (oldRegion != null)
                oldRegion.Dispose();
            layout.RowStyles[0].Height = NormalTopBarHeight;
            topBar.Visible = true;
            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(650, 740);
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
            bool enableClickThrough = !overlayClickThrough;
            if (!enableClickThrough && IsOsuWindowRestored())
            {
                SetOverlayClickThrough(true);
                ReturnFocusToOsu();
                return;
            }

            SetOverlayClickThrough(enableClickThrough);
            if (enableClickThrough)
                ReturnFocusToOsu();
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

            overlayClickThrough = enabled;
            browser.Enabled = !enabled;
            UpdateStyles();
        }

        private void StartOverlayInputGuard()
        {
            StopOverlayInputGuard();
            overlayInputGuardTimer = new System.Windows.Forms.Timer();
            overlayInputGuardTimer.Interval = 250;
            overlayInputGuardTimer.Tick += delegate
            {
                if (overlayMode && !overlayClickThrough && IsOsuWindowRestored())
                {
                    SetOverlayClickThrough(true);
                    ReturnFocusToOsu();
                }
            };
            overlayInputGuardTimer.Start();
        }

        private void StopOverlayInputGuard()
        {
            if (overlayInputGuardTimer == null)
                return;

            overlayInputGuardTimer.Stop();
            overlayInputGuardTimer.Dispose();
            overlayInputGuardTimer = null;
        }

        private static bool IsOsuWindowRestored()
        {
            Process[] osuProcesses = Process.GetProcessesByName("osu!");
            try
            {
                return osuProcesses.Any(process =>
                    process.MainWindowHandle != IntPtr.Zero &&
                    IsWindowVisible(process.MainWindowHandle) &&
                    !IsIconic(process.MainWindowHandle));
            }
            catch
            {
                return false;
            }
            finally
            {
                foreach (Process process in osuProcesses)
                    process.Dispose();
            }
        }

        private void RefreshOverlayWindowStyles()
        {
            if (!IsHandleCreated)
                return;

            UpdateStyles();
        }

        private static void ReturnFocusToOsu()
        {
            Process[] osuProcesses = Process.GetProcessesByName("osu!");
            try
            {
                Process osuProcess = osuProcesses
                    .Where(process => process.MainWindowHandle != IntPtr.Zero)
                    .FirstOrDefault();

                if (osuProcess != null)
                    SetForegroundWindow(osuProcess.MainWindowHandle);
            }
            catch
            {
                // osu! can exit between process enumeration and focus restoration.
            }
            finally
            {
                foreach (Process process in osuProcesses)
                    process.Dispose();
            }
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
            if (overlayMode && overlayClickThrough && m.Msg == WmNcHitTest)
            {
                m.Result = (IntPtr)HtTransparent;
                return;
            }

            // A desktop overlay must never become the foreground window. Otherwise
            // exclusive-fullscreen osu! can lose focus and show the Windows cursor.
            if (overlayMode && m.Msg == WmMouseActivate)
            {
                m.Result = (IntPtr)MaNoActivate;
                return;
            }

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

    }
}
