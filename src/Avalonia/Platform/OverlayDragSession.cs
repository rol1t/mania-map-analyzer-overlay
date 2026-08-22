using System;
using Avalonia;

namespace ManiaMapAnalyzerOverlay.Avalonia.Platform;

/// <summary>
/// Tracks one browser-to-window drag without invoking a native Windows move loop.
/// Browser screen coordinates are logical CSS pixels; the resulting position is
/// physical screen pixels after applying the current Avalonia render scale.
/// </summary>
public sealed class OverlayDragSession
{
    private long _gestureId;
    private long _pointerId;
    private long _lastSequence;
    private double _startX;
    private double _startY;
    private PixelPoint _startPosition;
    private double _renderScaling;

    public bool IsActive
    {
        get; private set;
    }

    public bool Start(
        long gestureId,
        long pointerId,
        long sequence,
        double screenX,
        double screenY,
        PixelPoint position,
        double renderScaling)
    {
        if (gestureId <= 0 || pointerId < 0 || sequence < 0 || !IsFinite(screenX) || !IsFinite(screenY) ||
            !IsFinite(renderScaling) || renderScaling <= 0)
        {
            return false;
        }

        if (IsActive && _gestureId == gestureId)
        {
            return false;
        }

        _gestureId = gestureId;
        _pointerId = pointerId;
        _lastSequence = sequence;
        _startX = screenX;
        _startY = screenY;
        _startPosition = position;
        _renderScaling = renderScaling;
        IsActive = true;
        return true;
    }

    public bool TryMove(
        long gestureId,
        long pointerId,
        long sequence,
        double screenX,
        double screenY,
        out PixelPoint position)
    {
        position = default;
        if (!IsActive || gestureId != _gestureId || pointerId != _pointerId || sequence <= _lastSequence ||
            !IsFinite(screenX) || !IsFinite(screenY) || !IsFinite(_renderScaling) || _renderScaling <= 0)
        {
            return false;
        }

        var nextX = _startPosition.X + (screenX - _startX) * _renderScaling;
        var nextY = _startPosition.Y + (screenY - _startY) * _renderScaling;
        if (nextX < int.MinValue || nextX > int.MaxValue || nextY < int.MinValue || nextY > int.MaxValue)
        {
            return false;
        }

        _lastSequence = sequence;
        position = new PixelPoint(
            (int)Math.Round(nextX, MidpointRounding.AwayFromZero),
            (int)Math.Round(nextY, MidpointRounding.AwayFromZero));
        return true;
    }

    public bool End(long gestureId, long pointerId, long sequence)
    {
        if (!IsActive || gestureId != _gestureId || pointerId != _pointerId || sequence < _lastSequence)
        {
            return false;
        }

        IsActive = false;
        return true;
    }

    public void Cancel() => IsActive = false;

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
