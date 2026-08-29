using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Owns the native Tosu realtime source and its continuous polling lifecycle.
/// Presentation surfaces may start or stop the host, but they do not own the
/// raw payload transport or the collector instance.
/// </summary>
public sealed class TosuRealtimeRuntimeHost : IDisposable
{
    private readonly TosuRealtimeTelemetrySource _source;
    private readonly OverlayGameplayPollingController _polling;
    private readonly Action<Action> _lifecycleDispatch;
    private TosuService? _tosu;
    private long _latestTransportGeneration;
    private bool _disposed;

    public TosuRealtimeRuntimeHost(
        Func<CancellationToken, Task<JsonElement?>> readPayload,
        Action<RealtimeTelemetryUpdate> applyTelemetry,
        TimeSpan interval,
        string source = "native-http",
        Action<string>? logBoundary = null,
        Action<Exception>? handleFailure = null,
        Action<Action>? dispatch = null,
        Func<TimeSpan, IOverlayGameplayPollingTimer>? timerFactory = null,
        Action<Action>? lifecycleDispatch = null)
    {
        _source = new TosuRealtimeTelemetrySource(readPayload, source);
        _polling = new OverlayGameplayPollingController(
            _source.ReadAsync,
            applyTelemetry,
            interval,
            logBoundary,
            handleFailure,
            dispatch,
            timerFactory);
        _lifecycleDispatch = lifecycleDispatch ?? DispatchOnUi;
    }

    public bool IsRunning => _polling.IsRunning;

    public bool IsRequestInFlight => _polling.IsRequestInFlight;

    public bool IsCancellationRequested => _polling.IsCancellationRequested;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _polling.Start();
    }

    public void Stop() => _polling.Stop();

    /// <summary>
    /// Binds collection to the Tosu process lifecycle. The host remains active
    /// when a presentation surface is hidden or recreated.
    /// </summary>
    public void Attach(TosuService tosu)
    {
        ArgumentNullException.ThrowIfNull(tosu);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_tosu, tosu))
        {
            ApplyConnectionState(tosu.IsRunning ? TosuConnectionState.Running : tosu.ConnectionState);
            return;
        }

        if (_tosu is not null)
        {
            _tosu.StateChanged -= Tosu_StateChanged;
        }

        _tosu = tosu;
        Volatile.Write(ref _latestTransportGeneration, tosu.TransportGeneration);
        _tosu.StateChanged += Tosu_StateChanged;
        ApplyConnectionState(tosu.IsRunning ? TosuConnectionState.Running : tosu.ConnectionState);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_tosu is not null)
        {
            _tosu.StateChanged -= Tosu_StateChanged;
            _tosu = null;
        }

        _polling.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Tosu_StateChanged(object? sender, TosuStateChangedEventArgs e)
    {
        if (!TryAdvanceTransportGeneration(e.TransportGeneration))
        {
            return;
        }

        _lifecycleDispatch(() =>
        {
            if (!_disposed && IsCurrentTransportGeneration(e.TransportGeneration))
            {
                ApplyConnectionState(e.State);
            }
        });
    }

    private bool TryAdvanceTransportGeneration(long generation)
    {
        if (generation == 0)
        {
            return Volatile.Read(ref _latestTransportGeneration) == 0;
        }

        long observed;
        do
        {
            observed = Volatile.Read(ref _latestTransportGeneration);
            if (generation < observed)
            {
                return false;
            }

            if (generation == observed)
            {
                return true;
            }
        }
        while (Interlocked.CompareExchange(ref _latestTransportGeneration, generation, observed) != observed);

        return true;
    }

    private bool IsCurrentTransportGeneration(long generation) =>
        generation == 0
            ? Volatile.Read(ref _latestTransportGeneration) == 0
            : generation == Volatile.Read(ref _latestTransportGeneration);

    private void ApplyConnectionState(TosuConnectionState state)
    {
        if (state == TosuConnectionState.Running)
        {
            if (!IsRunning)
            {
                Start();
            }

            return;
        }

        if (IsRunning)
        {
            Stop();
        }
    }

    private static void DispatchOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
