using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Platform;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>Starts and owns the bundled tosu process without platform-specific window APIs.</summary>
public sealed class TosuService : IDisposable
{
    private const string OverlayUrl = "http://127.0.0.1:24050/ManiaMapAnalyser/";
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(1) };
    private Process? process;
    private WindowsProcessJob? processJob;
    private bool disposed;

    public event EventHandler<TosuStateChangedEventArgs>? StateChanged;

    public string? ExecutablePath => FindExecutable();
    public bool IsRunning => process is { HasExited: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (process is { HasExited: false })
        {
            Publish("tosu is already running", true);
            return;
        }

        var executable = FindExecutable();
        if (executable is null)
        {
            Publish("tosu was not found. Run the installer or place it in the tosu folder next to the application.", false);
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
            Publish("tosu is starting…", false);

            var ready = await WaitForServerAsync(cancellationToken);
            Publish(ready ? "tosu is running" : "tosu started, but its local server did not become available", ready);
        }
        catch (Exception exception)
        {
            Stop();
            Publish("Could not start tosu: " + exception.Message, false);
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
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill request.
        }
        finally
        {
            runningProcess.Dispose();
        }

        Publish("tosu has stopped", false);
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
                using var response = await httpClient.GetAsync(OverlayUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }

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
            Path.Combine(Directory.GetCurrentDirectory(), "tosu", name)
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        return null;
    }

    private static void StopStaleBundledInstances(string expectedPath)
    {
        if (!OperatingSystem.IsWindows()) return;
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
            catch
            {
                // Access can fail for unrelated elevated processes.
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
            Publish("tosu has stopped", false);
    }

    private void Publish(string message, bool isRunning) => StateChanged?.Invoke(this, new TosuStateChangedEventArgs(message, isRunning));

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(TosuService));
    }

    public void Dispose()
    {
        if (disposed) return;
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

    public string Message { get; }
    public bool IsRunning { get; }
}
