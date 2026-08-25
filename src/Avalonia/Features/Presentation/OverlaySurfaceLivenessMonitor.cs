using System;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Presentation;

/// <summary>
/// Detects a presentation surface that stopped producing frames while it is
/// expected to be visible. Recovery is edge-triggered and may be retried after
/// the stale interval when an earlier recovery attempt fails.
/// </summary>
public sealed class OverlaySurfaceLivenessMonitor
{
    private readonly TimeSpan _staleAfter;
    private readonly Action _recover;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Action<Exception>? _recoveryFailed;
    private readonly object _gate = new();
    private DateTimeOffset _lastActivity;
    private bool _expected;
    private bool _recoveryInProgress;

    public OverlaySurfaceLivenessMonitor(
        TimeSpan staleAfter,
        Action recover,
        Func<DateTimeOffset>? getUtcNow = null,
        Action<Exception>? recoveryFailed = null)
    {
        if (staleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(staleAfter),
                staleAfter,
                "The stale-frame interval must be positive.");
        }

        _staleAfter = staleAfter;
        _recover = recover ?? throw new ArgumentNullException(nameof(recover));
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        _recoveryFailed = recoveryFailed;
        _lastActivity = _getUtcNow();
    }

    public void ObserveFrame()
    {
        lock (_gate)
        {
            _lastActivity = _getUtcNow();
        }
    }

    public void SetExpected(bool expected)
    {
        lock (_gate)
        {
            if (expected && !_expected)
            {
                // A newly visible or newly attached surface gets one complete
                // stale interval to produce its first frame.
                _lastActivity = _getUtcNow();
            }

            _expected = expected;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _expected = false;
            _recoveryInProgress = false;
            _lastActivity = _getUtcNow();
        }
    }

    public void Check()
    {
        lock (_gate)
        {
            if (!_expected || _recoveryInProgress || _getUtcNow() - _lastActivity < _staleAfter)
            {
                return;
            }

            _recoveryInProgress = true;
            // Prevent a failed recovery from causing a tight retry loop.
            _lastActivity = _getUtcNow();
        }

        try
        {
            _recover();
        }
        catch (Exception exception)
        {
            try
            {
                _recoveryFailed?.Invoke(exception);
            }
            catch
            {
                // Diagnostics must not break the watchdog or UI dispatcher.
            }
        }
        finally
        {
            lock (_gate)
            {
                _recoveryInProgress = false;
            }
        }
    }
}
