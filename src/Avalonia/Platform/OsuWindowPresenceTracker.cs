namespace ManiaMapAnalyzerOverlay.Avalonia.Platform;

/// <summary>
/// Debounces disappearance of the osu! top-level window. osu!lazer can keep
/// its process alive briefly (or indefinitely) after the game window closes,
/// while a window handle can also disappear momentarily during recreation.
/// </summary>
public sealed class OsuWindowPresenceTracker
{
    private readonly int _missingObservationThreshold;
    private int _consecutiveMissingObservations;
    private bool? _lastReportedPresence;

    public OsuWindowPresenceTracker(int missingObservationThreshold = 4)
    {
        if (missingObservationThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(missingObservationThreshold));
        }

        _missingObservationThreshold = missingObservationThreshold;
    }

    public bool? Observe(bool processRunning, bool? windowPresent)
    {
        if (!processRunning)
        {
            _consecutiveMissingObservations = _missingObservationThreshold;
            return Report(false);
        }

        if (windowPresent is null)
        {
            return null;
        }

        if (windowPresent.Value)
        {
            _consecutiveMissingObservations = 0;
            return Report(true);
        }

        _consecutiveMissingObservations++;
        return _consecutiveMissingObservations >= _missingObservationThreshold
            ? Report(false)
            : null;
    }

    public void Reset()
    {
        _consecutiveMissingObservations = 0;
        _lastReportedPresence = null;
    }

    private bool? Report(bool present)
    {
        if (_lastReportedPresence == present)
        {
            return null;
        }

        _lastReportedPresence = present;
        return present;
    }
}
