using System;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Reports a non-analysis state change from the headless beatmap poller so the
/// UI can show localized status for osu! not running, no beatmap, or errors.
/// </summary>
public sealed class HeadlessBeatmapSourceStateEventArgs : EventArgs
{
    public HeadlessBeatmapSourceStateEventArgs(
        HeadlessBeatmapSourceState state,
        string? message = null)
    {
        State = state;
        Message = message;
    }

    public HeadlessBeatmapSourceState State
    {
        get;
    }

    public string? Message
    {
        get;
    }
}
