using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

public interface IOverlayGameplayPollingTimer : IDisposable
{
    event EventHandler? Tick;

    TimeSpan Interval
    {
        get;
        set;
    }

    void Start();

    void Stop();
}

public sealed class AvaloniaOverlayGameplayPollingTimer : IOverlayGameplayPollingTimer
{
    private readonly DispatcherTimer _timer;

    public AvaloniaOverlayGameplayPollingTimer(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += OnTick;
    }

    public event EventHandler? Tick;

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e) => Tick?.Invoke(this, e);
}

/// <summary>
/// Owns the native realtime polling timer and its lifecycle generation.
/// Collection is independent from the presentation surface; a stopped or
/// replaced generation cannot deliver a telemetry frame into a later one.
/// </summary>
public sealed class OverlayGameplayPollingController : IDisposable
{
    private readonly Func<CancellationToken, Task<TosuRealtimeTelemetry?>> _readTelemetry;
    private readonly Action<TosuRealtimeTelemetry> _applyTelemetry;
    private readonly Action<string>? _logBoundary;
    private readonly Action<Exception>? _handleFailure;
    private readonly TimeSpan _interval;
    private readonly Action<Action> _dispatch;
    private readonly Func<TimeSpan, IOverlayGameplayPollingTimer> _timerFactory;
    private readonly object _gate = new();

    private IOverlayGameplayPollingTimer? _timer;
    private CancellationTokenSource? _cancellation;
    private readonly HashSet<long> _pollGenerationsInFlight = [];
    private long _generation;
    private bool _disposed;

    public OverlayGameplayPollingController(
        Func<CancellationToken, Task<TosuRealtimeTelemetry?>> readTelemetry,
        Action<TosuRealtimeTelemetry> applyTelemetry,
        TimeSpan interval,
        Action<string>? logBoundary = null,
        Action<Exception>? handleFailure = null,
        Action<Action>? dispatch = null,
        Func<TimeSpan, IOverlayGameplayPollingTimer>? timerFactory = null)
    {
        _readTelemetry = readTelemetry ?? throw new ArgumentNullException(nameof(readTelemetry));
        _applyTelemetry = applyTelemetry ?? throw new ArgumentNullException(nameof(applyTelemetry));
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The polling interval must be positive.");
        }

        _interval = interval;
        _logBoundary = logBoundary;
        _handleFailure = handleFailure;
        _dispatch = dispatch ?? (action => Dispatcher.UIThread.Post(action));
        _timerFactory = timerFactory ?? (pollingInterval => new AvaloniaOverlayGameplayPollingTimer(pollingInterval));
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _timer is not null;
            }
        }
    }

    public bool IsRequestInFlight
    {
        get
        {
            lock (_gate)
            {
                return _pollGenerationsInFlight.Count > 0;
            }
        }
    }

    public bool IsCancellationRequested
    {
        get
        {
            CancellationTokenSource? cancellation;
            lock (_gate)
            {
                cancellation = _cancellation;
            }

            return cancellation is not null && IsCancellationRequestedSafe(cancellation);
        }
    }

    public void Start()
    {
        Stop();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cancellation = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _generation);
        IOverlayGameplayPollingTimer timer = _timerFactory(_interval);
        timer.Tick += (_, _) => _ = PollAsync(generation, cancellation);

        lock (_gate)
        {
            _cancellation = cancellation;
            _timer = timer;
        }

        _logBoundary?.Invoke("started");
        timer.Start();
        _ = PollAsync(generation, cancellation);
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        IOverlayGameplayPollingTimer? timer;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
            cancellation = _cancellation;
            _cancellation = null;
        }

        timer?.Stop();
        timer?.Dispose();
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A queued callback may have completed the source cleanup first.
        }

        cancellation.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    private async Task PollAsync(long generation, CancellationTokenSource pollingCancellation)
    {
        if (!TryBeginPoll(generation))
        {
            return;
        }

        CancellationToken cancellationToken = default;
        try
        {
            if (!IsActive(generation, pollingCancellation))
            {
                return;
            }

            try
            {
                cancellationToken = pollingCancellation.Token;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            TosuRealtimeTelemetry? telemetry = await _readTelemetry(cancellationToken).ConfigureAwait(false);
            if (telemetry is null)
            {
                _logBoundary?.Invoke("payload-null-or-normalization-failed");
                return;
            }

            _dispatch(() =>
            {
                if (cancellationToken.IsCancellationRequested || !IsActive(generation, pollingCancellation))
                {
                    return;
                }

                try
                {
                    _applyTelemetry(telemetry);
                }
                catch (Exception exception)
                {
                    _handleFailure?.Invoke(exception);
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The active polling generation was stopped or replaced.
        }
        catch (Exception exception)
        {
            _handleFailure?.Invoke(exception);
        }
        finally
        {
            EndPoll(generation);
        }
    }

    private bool TryBeginPoll(long generation)
    {
        lock (_gate)
        {
            return _pollGenerationsInFlight.Add(generation);
        }
    }

    private void EndPoll(long generation)
    {
        lock (_gate)
        {
            _pollGenerationsInFlight.Remove(generation);
        }
    }

    private bool IsActive(long generation, CancellationTokenSource pollingCancellation) =>
        generation == Volatile.Read(ref _generation)
        && IsCurrentCancellation(pollingCancellation)
        && !IsCancellationRequestedSafe(pollingCancellation);

    private bool IsCurrentCancellation(CancellationTokenSource pollingCancellation)
    {
        lock (_gate)
        {
            return ReferenceEquals(_cancellation, pollingCancellation);
        }
    }

    private static bool IsCancellationRequestedSafe(CancellationTokenSource cancellation)
    {
        try
        {
            return cancellation.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
