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

}
