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
