using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Platform;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Minimal Tosu process lifecycle contract consumed by the native realtime
/// host. Keeping the event boundary small lets the host reject callbacks from
/// a detached service instance without depending on the concrete process
/// implementation in lifecycle tests.
/// </summary>
public interface ITosuRealtimeLifecycle
{
    event EventHandler<TosuStateChangedEventArgs>? StateChanged;

    bool IsRunning
    {
        get;
    }

    TosuConnectionState ConnectionState
    {
        get;
    }

    long TransportGeneration
    {
        get;
    }
}

public enum TosuInstanceOwnership
{
    None = 0,
    External = 1,
    Owned = 2
}

/// <summary>Connects to an existing Tosu server or starts and owns the bundled process.</summary>
public sealed class TosuService : IDisposable, ITosuRealtimeLifecycle
{
    private static readonly Uri _serverUri = TosuEndpointProbe.DefaultBaseUri;
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _findExecutable;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private Process? _process;
    private WindowsProcessJob? _processJob;
    private long _transportGeneration;
    private bool _disposed;

    public event EventHandler<TosuStateChangedEventArgs>? StateChanged;

    public TosuService()
        : this(
            new HttpClient { Timeout = TimeSpan.FromSeconds(1) },
            null,
            startInfo => Process.Start(startInfo))
    {
    }

    internal TosuService(
        HttpClient httpClient,
        Func<string?>? findExecutable,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _findExecutable = findExecutable ?? FindExecutable;
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
    }

    public string? ExecutablePath => _findExecutable();
    public bool IsRunning => Ownership == TosuInstanceOwnership.External ||
        (Ownership == TosuInstanceOwnership.Owned && _process is { HasExited: false });
    public TosuInstanceOwnership Ownership
    {
        get;
        private set;
    }
    public TosuConnectionState ConnectionState
    {
        get;
        private set;
    }

    public long TransportGeneration => Volatile.Read(ref _transportGeneration);

    /// <summary>
    /// Reads a complete Tosu v2 snapshot for the native realtime analyzer.
    /// This call intentionally remains independent from the presentation
    /// WebView, which may be hidden while osu! is playing.
    /// </summary>
    public async Task<JsonElement?> GetGameplayPayloadAsync(CancellationToken cancellationToken = default)
    {
        TosuRealtimePayloadResult result = await GetGameplayPayloadResultAsync(cancellationToken);
        return result.Payload;
    }

    public async Task<TosuRealtimePayloadResult> GetGameplayPayloadResultAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            using var response = await _httpClient.GetAsync(
                new Uri(_serverUri, "json/v2?overlay_realtime=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return TosuRealtimePayloadResult.Failure(
                    TosuRealtimePayloadFailureKind.HttpFailure,
                    response.StatusCode,
                    $"HTTP {(int)response.StatusCode} from json/v2.");
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return TosuRealtimePayloadResult.Failure(
                    TosuRealtimePayloadFailureKind.EmptyResponse,
                    detail: "json/v2 returned an empty response.");
            }

            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return TosuRealtimePayloadResult.Failure(
                    TosuRealtimePayloadFailureKind.MalformedPayload,
                    detail: "json/v2 returned a non-object JSON root.");
            }

            return TosuRealtimePayloadResult.Success(document.RootElement.Clone());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException exception)
        {
            AppLogger.Warning("Reading tosu realtime payload", "The Tosu realtime request timed out.", exception);
            return TosuRealtimePayloadResult.Failure(
                TosuRealtimePayloadFailureKind.TransportUnavailable,
                detail: exception.Message);
        }
        catch (JsonException exception)
        {
            AppLogger.Warning("Reading tosu realtime payload", "Tosu returned malformed realtime JSON.", exception);
            return TosuRealtimePayloadResult.Failure(
                TosuRealtimePayloadFailureKind.MalformedPayload,
                detail: exception.Message);
        }
        catch (HttpRequestException exception)
        {
            AppLogger.Warning("Reading tosu realtime payload", "The Tosu realtime endpoint was unavailable.", exception);
            return TosuRealtimePayloadResult.Failure(
                TosuRealtimePayloadFailureKind.TransportUnavailable,
                detail: exception.Message);
        }
        catch (IOException exception)
        {
            AppLogger.Warning("Reading tosu realtime payload", "The Tosu realtime response stream failed.", exception);
            return TosuRealtimePayloadResult.Failure(
                TosuRealtimePayloadFailureKind.TransportUnavailable,
                detail: exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            AppLogger.Warning("Reading tosu realtime payload", "Tosu returned an invalid realtime response.", exception);
            return TosuRealtimePayloadResult.Failure(
                TosuRealtimePayloadFailureKind.MalformedPayload,
                detail: exception.Message);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsRunning)
        {
            Publish("status.tosu_already_running", TosuConnectionState.Running);
            return;
        }

        Interlocked.Increment(ref _transportGeneration);
        TosuEndpointCompatibility compatibility = await TosuEndpointProbe.CheckAsync(
            _httpClient,
            _serverUri,
            cancellationToken).ConfigureAwait(false);
        if (compatibility == TosuEndpointCompatibility.ApplicationCompatible)
        {
            Ownership = TosuInstanceOwnership.External;
            Publish("status.tosu_connected_existing", TosuConnectionState.Running);
            return;
        }

        if (compatibility == TosuEndpointCompatibility.ApiOnly)
        {
            Publish("status.tosu_existing_incompatible", TosuConnectionState.Unavailable);
            return;
        }

        var executable = _findExecutable();
        if (executable is null)
        {
            Publish("status.tosu_not_found", TosuConnectionState.Unavailable);
            return;
        }

        try
        {
            _processJob?.Dispose();
            _processJob = new WindowsProcessJob();
            _process = _startProcess(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (_process is null)
            {
                throw new InvalidOperationException("The operating system did not start tosu.");
            }

            Ownership = TosuInstanceOwnership.Owned;
            _processJob.Attach(_process);

            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
            Publish("status.tosu_starting", TosuConnectionState.Starting);

            var ready = await WaitForServerAsync(cancellationToken);
            if (!ready)
            {
                AppLogger.Error(
                    "Starting tosu",
                    new TimeoutException("tosu started, but its local server did not become available."));
            }

            Publish(
                ready ? "status.tosu_running" : "status.tosu_started_server_unavailable",
                ready ? TosuConnectionState.Running : TosuConnectionState.Unavailable);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Starting tosu", exception);
            Stop();
            Publish("status.tosu_start_failed|" + exception.Message, TosuConnectionState.Failed);
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        await StartAsync(cancellationToken);
    }

    public void Stop()
    {
        bool wasConnected = IsRunning || Ownership != TosuInstanceOwnership.None;
        var runningProcess = _process;
        _process = null;
        var ownership = Ownership;
        Ownership = TosuInstanceOwnership.None;
        _processJob?.Dispose();
        _processJob = null;
        if (runningProcess is null || ownership != TosuInstanceOwnership.Owned)
        {
            if (wasConnected)
            {
                Publish("status.tosu_disconnected", TosuConnectionState.Stopped);
            }

            return;
        }

        runningProcess.Exited -= OnProcessExited;
        try
        {
            if (!runningProcess.HasExited)
            {
                runningProcess.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException exception)
        {
            AppLogger.Warning("Stopping tosu", "The process exited before it could be terminated.", exception);
        }
        finally
        {
            runningProcess.Dispose();
        }

        Publish("status.tosu_stopped", TosuConnectionState.Stopped);
    }

    private async Task<bool> WaitForServerAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
            {
                return false;
            }

            try
            {
                if (await TosuEndpointProbe.CheckAsync(_httpClient, _serverUri, cancellationToken)
                    .ConfigureAwait(false) == TosuEndpointCompatibility.ApplicationCompatible)
                {
                    return true;
                }
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
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        // Process.Exited may be queued after Stop/Restart has already installed
        // a different process. The event sender is the only reliable identity
        // at this boundary; the current transport generation alone is not
        // sufficient because stale callbacks would otherwise carry the new
        // generation and stop the new polling host.
        if (!_disposed && ReferenceEquals(sender, _process))
        {
            var exitedProcess = _process;
            _process = null;
            Ownership = TosuInstanceOwnership.None;
            _processJob?.Dispose();
            _processJob = null;
            if (exitedProcess is not null)
            {
                exitedProcess.Exited -= OnProcessExited;
                exitedProcess.Dispose();
            }

            Publish("status.tosu_stopped", TosuConnectionState.Stopped);
        }
    }

    private void Publish(string message, TosuConnectionState state)
    {
        ConnectionState = state;
        StateChanged?.Invoke(
            this,
            new TosuStateChangedEventArgs(
                message,
                state,
                TransportGeneration));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TosuService));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _httpClient.Dispose();
    }
}
public sealed class TosuStateChangedEventArgs : EventArgs
{
    public TosuStateChangedEventArgs(
        string message,
        TosuConnectionState state,
        long transportGeneration)
    {
        Message = message;
        State = state;
        IsRunning = state == TosuConnectionState.Running;
        TransportGeneration = transportGeneration;
    }

    public TosuStateChangedEventArgs(string message, bool isRunning)
        : this(
            message,
            isRunning ? TosuConnectionState.Running : TosuConnectionState.Stopped,
            0)
    {
    }

    public string Message
    {
        get;
    }
    public bool IsRunning
    {
        get;
    }

    public TosuConnectionState State
    {
        get;
    }

    public long TransportGeneration
    {
        get;
    }
}
