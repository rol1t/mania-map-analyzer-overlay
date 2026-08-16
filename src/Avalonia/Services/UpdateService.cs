using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public sealed class UpdateService
{
    public bool IsInstalled => OperatingSystem.IsWindows() && File.Exists(ScriptPath);

    public async Task<UpdateResult> CheckComponentsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
            return new UpdateResult { Success = true, CheckerInstalled = false };

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + Quote(ScriptPath) +
                " -InstallPath " + Quote(AppPaths.BaseDirectory) + " -ComponentsOnly -Json -Quiet",
            WorkingDirectory = AppPaths.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the update checker.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "The update checker returned no result." : error.Trim());
        var result = JsonSerializer.Deserialize<UpdateResult>(output.Trim(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return result ?? throw new InvalidOperationException("Could not read the update-check result.");
    }

    public bool StartSelfUpdate()
    {
        if (!IsInstalled) return false;
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + Quote(ScriptPath) +
                " -SelfUpdate -InstallPath " + Quote(AppPaths.BaseDirectory) +
                " -WaitForProcessId " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + " -Quiet",
            WorkingDirectory = AppPaths.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        return process is not null;
    }

    private static string ScriptPath => Path.Combine(AppPaths.BaseDirectory, "Update-ManiaMapAnalyzerOverlay.ps1");
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

public sealed class UpdateResult
{
    public bool Success { get; set; }
    public bool CheckerInstalled { get; set; } = true;
    public string? Error { get; set; }
    public string? Compatibility { get; set; }
    public bool LauncherUpdateAvailable { get; set; }
    public string? LatestLauncherVersion { get; set; }
    public bool UpdatedTosu { get; set; }
    public bool UpdatedAddon { get; set; }
    public string? LatestTosu { get; set; }
    public string? LatestAddon { get; set; }
    public string? LazerVersion { get; set; }
}
