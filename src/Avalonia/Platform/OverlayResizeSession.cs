using System;
using Avalonia;

namespace ManiaMapAnalyzerOverlay.Avalonia.Platform;

/// <summary>
/// Tracks one browser-to-window resize without entering a native Win32 move loop.
/// Browser screen coordinates are logical CSS pixels; Avalonia window positions
/// are physical screen pixels while client sizes are logical pixels.
/// </summary>
public sealed class OverlayResizeSession
{
    private long _gestureId;
    private long _pointerId;
    private long _lastSequence;
    private ResizeEdges _edges;
    private double _startX;
    private double _startY;
    private PixelPoint _startPosition;
    private Size _startClientSize;
    private double _renderScaling;
    private double _minimumWidth;
    private double _minimumHeight;
    private Size _maximumClientSize;

    public bool IsActive
    {
        get; private set;
    }

    public bool Start(
        long gestureId,
        long pointerId,
        long sequence,
        string direction,
        double screenX,
        double screenY,
        PixelPoint position,
        Size clientSize,
        double renderScaling,
        double minimumWidth,
        double minimumHeight,
        Size maximumClientSize)
    {
        var edges = ParseDirection(direction);
        if (gestureId <= 0 || pointerId < 0 || sequence < 0 || edges == ResizeEdges.None ||
            !IsFinite(screenX) || !IsFinite(screenY) || !IsFinite(renderScaling) || renderScaling <= 0 ||
            !IsFinite(clientSize.Width) || clientSize.Width <= 0 ||
            !IsFinite(clientSize.Height) || clientSize.Height <= 0 ||
            !IsFinite(minimumWidth) || minimumWidth <= 0 ||
            !IsFinite(minimumHeight) || minimumHeight <= 0)
        {
            return false;
        }

        if (!IsFinite(maximumClientSize.Width) || maximumClientSize.Width < minimumWidth ||
            !IsFinite(maximumClientSize.Height) || maximumClientSize.Height < minimumHeight)
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
        _edges = edges;
        _startX = screenX;
        _startY = screenY;
        _startPosition = position;
        _startClientSize = clientSize;
        _renderScaling = renderScaling;
        _minimumWidth = minimumWidth;
        _minimumHeight = minimumHeight;
        _maximumClientSize = maximumClientSize;
        IsActive = true;
        return true;
    }

    public bool TryMove(
        long gestureId,
        long pointerId,
        long sequence,
        double screenX,
        double screenY,
        out PixelPoint position,
        out Size clientSize)
    {
        position = default;
        clientSize = default;
        if (!IsActive || gestureId != _gestureId || pointerId != _pointerId || sequence <= _lastSequence ||
            !IsFinite(screenX) || !IsFinite(screenY))
        {
            return false;
        }

        var deltaX = (screenX - _startX) * _renderScaling;
        var deltaY = (screenY - _startY) * _renderScaling;
        var left = (double)_startPosition.X;
        var top = (double)_startPosition.Y;
        var right = left + _startClientSize.Width * _renderScaling;
        var bottom = top + _startClientSize.Height * _renderScaling;

        if ((_edges & ResizeEdges.Left) != 0)
        {
            left += deltaX;
        }
        else if ((_edges & ResizeEdges.Right) != 0)
        {
            right += deltaX;
        }

        if ((_edges & ResizeEdges.Top) != 0)
        {
            top += deltaY;
        }
        else if ((_edges & ResizeEdges.Bottom) != 0)
        {
            bottom += deltaY;
        }

        var minimumPhysicalWidth = _minimumWidth * _renderScaling;
        if (right - left < minimumPhysicalWidth)
        {
            if ((_edges & ResizeEdges.Left) != 0)
            {
                left = right - minimumPhysicalWidth;
            }
            else
            {
                right = left + minimumPhysicalWidth;
            }
        }

        var minimumPhysicalHeight = _minimumHeight * _renderScaling;
        if (bottom - top < minimumPhysicalHeight)
        {
            if ((_edges & ResizeEdges.Top) != 0)
            {
                top = bottom - minimumPhysicalHeight;
            }
            else
            {
                bottom = top + minimumPhysicalHeight;
            }
        }

        var maximumPhysicalWidth = _maximumClientSize.Width * _renderScaling;
        if (right - left > maximumPhysicalWidth)
        {
            if ((_edges & ResizeEdges.Left) != 0)
            {
                left = right - maximumPhysicalWidth;
            }
            else
            {
                right = left + maximumPhysicalWidth;
            }
        }

        var maximumPhysicalHeight = _maximumClientSize.Height * _renderScaling;
        if (bottom - top > maximumPhysicalHeight)
        {
            if ((_edges & ResizeEdges.Top) != 0)
            {
                top = bottom - maximumPhysicalHeight;
            }
            else
            {
                bottom = top + maximumPhysicalHeight;
            }
        }

        if (!IsInt32(left) || !IsInt32(top) || !IsFinite(right) || !IsFinite(bottom))
        {
            return false;
        }

        _lastSequence = sequence;
        position = new PixelPoint(
            (int)Math.Round(left, MidpointRounding.AwayFromZero),
            (int)Math.Round(top, MidpointRounding.AwayFromZero));
        clientSize = new Size((right - left) / _renderScaling, (bottom - top) / _renderScaling);
        return IsFinite(clientSize.Width) && IsFinite(clientSize.Height);
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

    private static ResizeEdges ParseDirection(string? direction)
    {
        return direction?.Trim().ToLowerInvariant() switch
        {
            "n" => ResizeEdges.Top,
            "s" => ResizeEdges.Bottom,
            "e" => ResizeEdges.Right,
            "w" => ResizeEdges.Left,
            "ne" => ResizeEdges.Top | ResizeEdges.Right,
            "nw" => ResizeEdges.Top | ResizeEdges.Left,
            "se" => ResizeEdges.Bottom | ResizeEdges.Right,
            "sw" => ResizeEdges.Bottom | ResizeEdges.Left,
            _ => ResizeEdges.None
        };
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsInt32(double value) =>
        IsFinite(value) && value >= int.MinValue && value <= int.MaxValue;

    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8
    }
}
