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
        private async Task<bool> CheckAndApplyUpdatesAsync()
        {
            string updateScript = Path.Combine(Application.StartupPath, "Update-ManiaMapAnalyzerOverlay.ps1");
            if (!File.Exists(updateScript))
            {
                startupUpdateSuffix = UiText.Get(" · проверка обновлений не установлена", " · update checker is not installed");
                return true;
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
                bool launcherUpdateAvailable = GetJsonBoolean(result, "LauncherUpdateAvailable");
                string latestLauncherVersion = GetJsonString(result, "LatestLauncherVersion");
                if (launcherUpdateAvailable)
                {
                    DialogResult choice = MessageBox.Show(
                        UiText.Get(
                            "Доступна новая версия Mania Map Analyzer Overlay " + latestLauncherVersion + ".\r\n\r\nОбновить приложение сейчас? Оно автоматически перезапустится, а настройки и пользовательский CSS сохранятся.",
                            "A new version of Mania Map Analyzer Overlay " + latestLauncherVersion + " is available.\r\n\r\nUpdate now? The application will restart automatically, while settings and custom CSS are preserved."),
                        UiText.Get("Доступно обновление", "Update available"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);

                    if (choice == DialogResult.Yes && StartSelfUpdate(updateScript))
                    {
                        Close();
                        return false;
                    }
                }

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
            return true;
        }

        private static bool StartSelfUpdate(string scriptPath)
        {
            try
            {
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = "powershell.exe";
                startInfo.Arguments =
                    "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(scriptPath) +
                    " -SelfUpdate -InstallPath " + QuoteArgument(Application.StartupPath) +
                    " -WaitForProcessId " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                    " -Quiet";
                startInfo.WorkingDirectory = Application.StartupPath;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                return Process.Start(startInfo) != null;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    UiText.Get("Не удалось запустить самообновление.\r\n\r\n", "Could not start self-update.\r\n\r\n") + ex.Message,
                    UiText.Get("Ошибка обновления", "Update error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
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

    }
}
