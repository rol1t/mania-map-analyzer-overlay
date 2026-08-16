using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Web.Script.Serialization;

namespace ManiaMapAnalyzerOverlay
{
    internal sealed partial class MainForm
    {
        private const string FullscreenOverlayEnvironmentKey = "ENABLE_INGAME_OVERLAY";
        private const string FullscreenCounterFolderName = "ManiaMapAnalyzerOverlay";

        private async Task ToggleFullscreenOverlayAsync()
        {
            bool wasEnabled = ReadFullscreenOverlayEnabled();
            int previousStyleVersion = launcherSettings.FullscreenOverlayStyleVersion;
            bool enable = !wasEnabled;
            string action = enable
                ? UiText.Get(
                    "Включить полноэкранный оверлей для osu!stable?\r\n\r\nОн использует официальный In-Game Overlay от tosu и потребляет немного больше ресурсов. tosu будет перезапущен.",
                    "Enable the fullscreen overlay for osu!stable?\r\n\r\nIt uses tosu's official In-Game Overlay and consumes slightly more resources. tosu will restart.")
                : UiText.Get(
                    "Выключить полноэкранный оверлей для osu!stable?\r\n\r\ntosu будет перезапущен.",
                    "Disable the fullscreen overlay for osu!stable?\r\n\r\ntosu will restart.");

            if (MessageBox.Show(
                    action,
                    UiText.Get("Оверлей для Stable Fullscreen", "Stable Fullscreen Overlay"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                SetFullscreenOverlayEnabled(enable);
                launcherSettings.FullscreenOverlayEnabled = enable;
                if (enable)
                    launcherSettings.FullscreenOverlayStyleVersion = 1;
                SaveLauncherSettings();
                UpdateFullscreenOverlayButton();

                if (enable)
                    EnsureFullscreenOverlayProfile(true);

                await RestartTosuAsync();
                if (tosuProcess == null || tosuProcess.HasExited)
                    return;

                if (enable)
                {
                    Navigate(FullscreenOverlayEditorUrl);
                    MessageBox.Show(
                        UiText.Get(
                            "Полноэкранный оверлей включён, ManiaMapAnalyser уже добавлен в профиль.\r\n\r\nВыбранное в приложении оформление применяется автоматически. В открывшемся редакторе можно изменить положение, а в osu!stable вызвать его сочетанием Ctrl+Shift+Space.",
                            "The fullscreen overlay is enabled and ManiaMapAnalyser has already been added to the profile.\r\n\r\nThe appearance selected in the application is applied automatically. Use the editor that has opened to change its position, or open it in osu!stable with Ctrl+Shift+Space."),
                        UiText.Get("Stable Fullscreen включён", "Stable Fullscreen enabled"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                try { SetFullscreenOverlayEnabled(wasEnabled); }
                catch { }
                launcherSettings.FullscreenOverlayEnabled = wasEnabled;
                launcherSettings.FullscreenOverlayStyleVersion = previousStyleVersion;
                SaveLauncherSettings();
                UpdateFullscreenOverlayButton();
                MessageBox.Show(
                    UiText.Get("Не удалось изменить режим полноэкранного оверлея.\r\n\r\n", "Could not change the fullscreen overlay mode.\r\n\r\n") + ex.Message,
                    UiText.Get("Ошибка настройки", "Configuration error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void UpdateFullscreenOverlayButton()
        {
            bool enabled = launcherSettings != null && launcherSettings.FullscreenOverlayEnabled;
            fullscreenOverlayButton.Text = enabled
                ? UiText.Get("Stable FS: Вкл", "Stable FS: On")
                : UiText.Get("Stable FS: Выкл", "Stable FS: Off");
            fullscreenOverlayButton.BackColor = enabled
                ? Color.FromArgb(42, 126, 91)
                : Color.FromArgb(89, 67, 42);
            fullscreenOverlayButton.FlatAppearance.MouseOverBackColor = enabled
                ? Color.FromArgb(51, 150, 108)
                : Color.FromArgb(112, 82, 49);
        }

        private void SynchronizeFullscreenOverlayState()
        {
            bool enabled = ReadFullscreenOverlayEnabled();
            bool settingsChanged = false;
            if (launcherSettings.FullscreenOverlayEnabled != enabled)
            {
                launcherSettings.FullscreenOverlayEnabled = enabled;
                settingsChanged = true;
            }
            if (enabled)
            {
                bool updateBounds = launcherSettings.FullscreenOverlayStyleVersion < 1;
                EnsureFullscreenOverlayProfile(updateBounds);
                if (updateBounds)
                {
                    launcherSettings.FullscreenOverlayStyleVersion = 1;
                    settingsChanged = true;
                }
            }
            if (settingsChanged)
                SaveLauncherSettings();
            UpdateFullscreenOverlayButton();
        }

        private void EnsureFullscreenOverlayProfile(bool updateBounds)
        {
            Size overlaySize = GetFullscreenOverlaySize();
            EnsureFullscreenOverlayAssets(overlaySize);

            string settingsDirectory = Path.Combine(Application.StartupPath, "tosu", "settings");
            string settingsPath = Path.Combine(settingsDirectory, "__ingame__.values.json");
            var serializer = new JavaScriptSerializer();
            InGameOverlayConfiguration configuration = null;

            if (File.Exists(settingsPath))
            {
                string json = File.ReadAllText(settingsPath);
                if (!string.IsNullOrWhiteSpace(json))
                    configuration = serializer.Deserialize<InGameOverlayConfiguration>(json);
            }

            if (configuration == null)
                configuration = new InGameOverlayConfiguration();
            if (string.IsNullOrWhiteSpace(configuration.ingame_profile))
                configuration.ingame_profile = "default";
            if (string.IsNullOrWhiteSpace(configuration.obs_profile))
                configuration.obs_profile = "default";
            if (configuration.profiles == null)
                configuration.profiles = new List<InGameOverlayProfile>();

            InGameOverlayProfile activeProfile = null;
            foreach (InGameOverlayProfile profile in configuration.profiles)
            {
                if (profile != null && string.Equals(profile.id, configuration.ingame_profile, StringComparison.Ordinal))
                {
                    activeProfile = profile;
                    break;
                }
            }

            if (activeProfile == null && configuration.profiles.Count > 0)
                activeProfile = configuration.profiles[0];
            if (activeProfile == null)
            {
                activeProfile = new InGameOverlayProfile
                {
                    id = "default",
                    name = "default",
                    overlays = new List<InGameOverlayItem>()
                };
                configuration.profiles.Add(activeProfile);
            }

            if (string.IsNullOrWhiteSpace(activeProfile.id))
                activeProfile.id = "default";
            if (string.IsNullOrWhiteSpace(activeProfile.name))
                activeProfile.name = activeProfile.id;
            if (activeProfile.overlays == null)
                activeProfile.overlays = new List<InGameOverlayItem>();
            configuration.ingame_profile = activeProfile.id;

            InGameOverlayItem overlayItem = null;
            foreach (InGameOverlayItem overlay in activeProfile.overlays)
            {
                if (overlay != null && string.Equals(overlay.folderName, FullscreenCounterFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    overlayItem = overlay;
                    break;
                }
            }

            bool migratedFromDefaultCounter = false;
            if (overlayItem == null)
            {
                foreach (InGameOverlayItem overlay in activeProfile.overlays)
                {
                    if (overlay != null && string.Equals(overlay.folderName, "ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase))
                    {
                        overlayItem = overlay;
                        migratedFromDefaultCounter = true;
                        break;
                    }
                }
            }

            if (overlayItem == null)
            {
                overlayItem = new InGameOverlayItem
                {
                    id = 1,
                    top = 24,
                    left = 24,
                    scale = 1D,
                    z_index = 1
                };
                activeProfile.overlays.Add(overlayItem);
                updateBounds = true;
            }

            overlayItem._enabled = true;
            overlayItem._settings = false;
            overlayItem.folderName = FullscreenCounterFolderName;
            overlayItem.url = BaseUrl + "/" + FullscreenCounterFolderName;
            if (updateBounds || migratedFromDefaultCounter || overlayItem.width <= 0 || overlayItem.height <= 0)
            {
                overlayItem.width = overlaySize.Width;
                overlayItem.height = overlaySize.Height;
            }

            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(settingsPath, serializer.Serialize(configuration), new UTF8Encoding(false));
        }

        private Size GetFullscreenOverlaySize()
        {
            double scale = GetOverlayScalePercent() / 100D;
            string layoutMode = GetOverlayLayoutMode();
            int width;
            int height;

            if (string.Equals(layoutMode, "horizontal", StringComparison.Ordinal))
            {
                width = 960;
                height = 650;
            }
            else if (string.Equals(layoutMode, "companella", StringComparison.Ordinal))
            {
                width = 640;
                height = 470;
            }
            else if (string.Equals(layoutMode, "custom", StringComparison.Ordinal))
            {
                width = 620;
                height = 700;
            }
            else
            {
                width = 520;
                height = 640;
            }

            return new Size(
                Math.Max(240, (int)Math.Ceiling(width * scale)),
                Math.Max(180, (int)Math.Ceiling(height * scale)));
        }

        private void EnsureFullscreenOverlayAssets(Size overlaySize)
        {
            string counterDirectory = GetFullscreenCounterDirectory();
            Directory.CreateDirectory(counterDirectory);

            string metadata =
                "Usecase: ingame\r\n" +
                "Name: Mania Map Analyzer Overlay\r\n" +
                "Author: rol1t\r\n" +
                "CompatibleWith: tosu\r\n" +
                "Resolution: " + overlaySize.Width + "x" + overlaySize.Height + "\r\n" +
                "Version: 1.0\r\n" +
                "Notes: Applies the launcher-selected preset to ManiaMapAnalyser in tosu's In-Game Overlay.\r\n";
            File.WriteAllText(Path.Combine(counterDirectory, "metadata.txt"), metadata, new UTF8Encoding(false));

            string indexHtml = @"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
  <style>
    html,body{width:100%;height:100%;margin:0;overflow:hidden;background:transparent}
    #mma-source{display:block;width:100%;height:100%;border:0;background:transparent}
  </style>
</head>
<body>
  <iframe id=""mma-source"" src=""/ManiaMapAnalyser/?launcher-fullscreen=1"" allowtransparency=""true""></iframe>
  <script>
    (function(){
      const frame=document.getElementById('mma-source');
      const base='/ManiaMapAnalyzerOverlay/';
      let appliedVersion='';
      async function apply(version){
        try{
          const doc=frame.contentDocument;
          if(!doc||!doc.head)return;
          const previous=doc.getElementById('mma-fullscreen-runtime');
          if(previous)previous.remove();
          const script=doc.createElement('script');
          script.id='mma-fullscreen-runtime';
          script.src=base+'runtime.js?v='+encodeURIComponent(version);
          doc.head.appendChild(script);
          appliedVersion=version;
        }catch(_){}
      }
      async function refresh(){
        try{
          const response=await fetch(base+'runtime.version?t='+Date.now(),{cache:'no-store'});
          const version=(await response.text()).trim();
          if(version&&version!==appliedVersion)await apply(version);
        }catch(_){}
      }
      frame.addEventListener('load',function(){appliedVersion='';refresh();});
      setInterval(refresh,1000);
      refresh();
    })();
  </script>
</body>
</html>";
            File.WriteAllText(Path.Combine(counterDirectory, "index.html"), indexHtml, new UTF8Encoding(false));

            string runtimePath = Path.Combine(counterDirectory, "runtime.js");
            string versionPath = Path.Combine(counterDirectory, "runtime.version");
            if (!File.Exists(runtimePath))
                File.WriteAllText(runtimePath, "(function(){})();", new UTF8Encoding(false));
            if (!File.Exists(versionPath))
                File.WriteAllText(versionPath, "0", new UTF8Encoding(false));
        }

        private void WriteFullscreenOverlayRuntime(string setupScript, string observerScript)
        {
            EnsureFullscreenOverlayAssets(GetFullscreenOverlaySize());
            string counterDirectory = GetFullscreenCounterDirectory();
            string runtimePath = Path.Combine(counterDirectory, "runtime.js");
            string runtime = setupScript + Environment.NewLine + observerScript;

            if (File.Exists(runtimePath) && string.Equals(File.ReadAllText(runtimePath), runtime, StringComparison.Ordinal))
                return;

            File.WriteAllText(runtimePath, runtime, new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(counterDirectory, "runtime.version"),
                DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
        }

        private static string GetFullscreenCounterDirectory()
        {
            return Path.Combine(Application.StartupPath, "tosu", "static", FullscreenCounterFolderName);
        }

        private bool ReadFullscreenOverlayEnabled()
        {
            string environmentPath = GetTosuEnvironmentPath();
            if (!File.Exists(environmentPath))
                return launcherSettings.FullscreenOverlayEnabled;

            string content = File.ReadAllText(environmentPath);
            Match match = Regex.Match(
                content,
                "(?im)^\\s*" + Regex.Escape(FullscreenOverlayEnvironmentKey) + "\\s*=\\s*(?<value>[^#\\r\\n]*)");
            if (!match.Success)
                return launcherSettings.FullscreenOverlayEnabled;

            return string.Equals(match.Groups["value"].Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private void SetFullscreenOverlayEnabled(bool enabled)
        {
            string environmentPath = GetTosuEnvironmentPath();
            if (!File.Exists(environmentPath))
                throw new FileNotFoundException(
                    UiText.Get("Файл tosu.env не найден. Запустите установщик для восстановления компонентов.", "tosu.env was not found. Run the installer to restore the components."),
                    environmentPath);

            string content = File.ReadAllText(environmentPath);
            string replacement = FullscreenOverlayEnvironmentKey + "=" + (enabled ? "true" : "false");
            string pattern = "(?im)^\\s*" + Regex.Escape(FullscreenOverlayEnvironmentKey) + "\\s*=[^\\r\\n]*";
            string updated = Regex.IsMatch(content, pattern)
                ? Regex.Replace(content, pattern, replacement)
                : content.TrimEnd('\r', '\n') + Environment.NewLine + replacement + Environment.NewLine;

            File.WriteAllText(environmentPath, updated, new UTF8Encoding(false));
        }

        private static string GetTosuEnvironmentPath()
        {
            return Path.Combine(Application.StartupPath, "tosu", "tosu.env");
        }

        private sealed class InGameOverlayConfiguration
        {
            public InGameOverlayConfiguration() { }
            public string obs_profile { get; set; }
            public string ingame_profile { get; set; }
            public List<InGameOverlayProfile> profiles { get; set; }
        }

        private sealed class InGameOverlayProfile
        {
            public InGameOverlayProfile() { }
            public string id { get; set; }
            public string name { get; set; }
            public List<InGameOverlayItem> overlays { get; set; }
        }

        private sealed class InGameOverlayItem
        {
            public InGameOverlayItem() { }
            public bool _enabled { get; set; }
            public bool _settings { get; set; }
            public int id { get; set; }
            public string folderName { get; set; }
            public string url { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public int top { get; set; }
            public int left { get; set; }
            public double scale { get; set; }
            public int z_index { get; set; }
        }
    }
}
