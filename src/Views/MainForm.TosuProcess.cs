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
        private async Task RestartTosuAsync()
        {
            restartButton.Enabled = false;
            analysisButton.Enabled = false;
            designButton.Enabled = false;
            overlayButton.Enabled = false;
            fullscreenOverlayButton.Enabled = false;
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
                fullscreenOverlayButton.Enabled = false;
                return;
            }

            try
            {
                SynchronizeFullscreenOverlayState();
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
                fullscreenOverlayButton.Enabled = true;
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
                fullscreenOverlayButton.Enabled = false;
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
                    fullscreenOverlayButton.Enabled = false;
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
            StopOverlayInputGuard();
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

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
