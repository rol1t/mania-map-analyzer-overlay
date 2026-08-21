using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Platform;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>Starts and owns the bundled tosu process without platform-specific window APIs.</summary>
public sealed class TosuService : IDisposable
{
    private const string ServerUrl = "http://127.0.0.1:24050/";
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(1) };
    private Process? process;
    private WindowsProcessJob? processJob;
    private bool disposed;

    public event EventHandler<TosuStateChangedEventArgs>? StateChanged;

    public string? ExecutablePath => FindExecutable();
    public bool IsRunning => process is { HasExited: false };

    /// <summary>
    /// Reads the authoritative osu! gameplay state from tosu. The overlay uses
    /// this as a native fallback because a browser websocket can miss a
    /// state-only update while the game is switching screens.
    /// </summary>
    public async Task<TosuGameplayState?> GetGameplayStateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            using var response = await httpClient.GetAsync(
                ServerUrl + "json/v2?overlay_state=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("state", out var state))
                return null;

            var stateName = string.Empty;
            if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String)
            {
                stateName = name.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
            }

            int? stateNumber = null;
            if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("number", out var number))
            {
                if (number.TryGetInt32(out var numericState))
                    stateNumber = numericState;
            }

            bool? isPlaying = stateName switch
            {
                "play" or "gameplay" or "playing" or "spectating" or "watchingreplay" or "replay" => true,
                "menu" or "edit" or "selectplay" or "selectedit" or "selectdrawings" or "resultscreen" or "result" or "options" or "songselect" => false,
                _ when stateNumber is int numberValue => numberValue == 2,
                _ => null
            };

            bool? isPaused = null;
            if (document.RootElement.TryGetProperty("game", out var game) &&
                game.ValueKind == JsonValueKind.Object &&
                game.TryGetProperty("paused", out var paused) &&
                paused.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isPaused = paused.GetBoolean();
            }

            return new TosuGameplayState(stateName, stateNumber, isPlaying, isPaused);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            AppLogger.Warning("Reading tosu gameplay state", "The gameplay state could not be read.", exception);
        }

        return null;
    }

    public async Task<bool?> GetIsPlayingAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetGameplayStateAsync(cancellationToken);
        return state?.IsPlaying;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (process is { HasExited: false })
        {
            Publish("status.tosu_already_running", true);
            return;
        }

        var executable = FindExecutable();
        if (executable is null)
        {
            Publish("status.tosu_not_found", false);
            return;
        }

        try
        {
            StopStaleBundledInstances(executable);
            processJob?.Dispose();
            processJob = new WindowsProcessJob();
            process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
                throw new InvalidOperationException("The operating system did not start tosu.");

            processJob.Attach(process);

            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;
            Publish("status.tosu_starting", false);

            var ready = await WaitForServerAsync(cancellationToken);
            if (!ready)
                AppLogger.Error(
                    "Starting tosu",
                    new TimeoutException("tosu started, but its local server did not become available."));
            Publish(ready ? "status.tosu_running" : "status.tosu_started_server_unavailable", ready);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Starting tosu", exception);
            Stop();
            Publish("status.tosu_start_failed|" + exception.Message, false);
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        await StartAsync(cancellationToken);
    }

    public void Stop()
    {
        var runningProcess = process;
        process = null;
        processJob?.Dispose();
        processJob = null;
        if (runningProcess is null)
            return;

        runningProcess.Exited -= OnProcessExited;
        try
        {
            if (!runningProcess.HasExited)
                runningProcess.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException exception)
        {
            AppLogger.Warning("Stopping tosu", "The process exited before it could be terminated.", exception);
        }
        finally
        {
            runningProcess.Dispose();
        }

        Publish("status.tosu_stopped", false);
    }

    private async Task<bool> WaitForServerAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process is null || process.HasExited)
                return false;

            try
            {
                using var response = await httpClient.GetAsync(ServerUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (HttpRequestException exception)
            {
                AppLogger.Warning("Waiting for tosu server", exception.Message, exception);
            }
            catch (IOException exception)
            {
                AppLogger.Warning("Waiting for tosu server", exception.Message, exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                AppLogger.Warning("Waiting for tosu server", exception.Message, exception);
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private string? FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "tosu.exe" : "tosu";
        var candidates = new[]
        {
            Path.Combine(AppPaths.TosuDirectory, name),
            Path.Combine(AppPaths.LegacyTosuDirectory, name),
            Path.Combine(Directory.GetCurrentDirectory(), "tosu", name)
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        return null;
    }

    private static void StopStaleBundledInstances(string expectedPath)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var expected = Path.GetFullPath(expectedPath);
        foreach (var stale in Process.GetProcessesByName("tosu"))
        {
            try
            {
                var path = stale.MainModule?.FileName;
                if (path is not null && string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
                {
                    stale.Kill(entireProcessTree: true);
                    stale.WaitForExit(3000);
                }
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Stopping stale tosu process", "Could not inspect or stop an unrelated process.", exception);
            }
            finally
            {
                stale.Dispose();
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (!disposed)
            Publish("status.tosu_stopped", false);
    }

    private void Publish(string message, bool isRunning) => StateChanged?.Invoke(this, new TosuStateChangedEventArgs(message, isRunning));

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(TosuService));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Stop();
        httpClient.Dispose();
    }
}

public sealed class TosuStateChangedEventArgs : EventArgs
{
    public TosuStateChangedEventArgs(string message, bool isRunning)
    {
        Message = message;
        IsRunning = isRunning;
    }

    public string Message
    {
        get;
    }
    public bool IsRunning
    {
        get;
    }
}

public sealed record TosuGameplayState(string Name, int? Number, bool? IsPlaying, bool? IsPaused);
