namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Describes when an overlay preset should be visible. The values are part of
/// the editable preset manifest contract and are intentionally analyzer-neutral.
/// </summary>
public static class OverlayVisibilityPolicy
{
    public const string Always = "always";
    public const string OutsidePlay = "outside-play";
    public const string OutsideOnly = "outside-only";
    public const string DuringPlay = "during-play";
    public const string DuringAndPaused = "during-and-paused";
    public const string OutsideAndDuringPlay = "outside-and-during-play";
    public const string PausedOnly = "paused-only";
    public const string Never = "never";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            OutsidePlay => OutsidePlay,
            OutsideOnly => OutsideOnly,
            DuringPlay => DuringPlay,
            DuringAndPaused => DuringAndPaused,
            OutsideAndDuringPlay => OutsideAndDuringPlay,
            PausedOnly => PausedOnly,
            Never => Never,
            _ => Always
        };
    }

    public static bool ShouldShow(string? policy, bool isPlaying, bool? isPaused)
    {
        return Normalize(policy) switch
        {
            OutsidePlay => !isPlaying || isPaused == true,
            OutsideOnly => !isPlaying && isPaused != true,
            DuringPlay => isPlaying && isPaused != true,
            DuringAndPaused => isPlaying,
            OutsideAndDuringPlay => !isPlaying || isPaused != true,
            PausedOnly => isPlaying && isPaused == true,
            Never => false,
            _ => true
        };
    }

    /// <summary>
    /// A minimized osu! window is an editing state for the desktop overlay.
    /// Keep the surface available even when the selected preset normally hides
    /// itself outside active play.
    /// </summary>
    public static bool ShouldShow(string? policy, bool isPlaying, bool? isPaused, bool osuMinimized) =>
        osuMinimized || ShouldShow(policy, isPlaying, isPaused);

    public static bool ShouldShowBeforeGameplayStateIsKnown(string? policy) => Normalize(policy) != Never;

    /// <summary>
    /// Converts three independent UI choices into the stable manifest/runtime
    /// policy contract without making the view an alternate source of truth.
    /// </summary>
    public static string FromVisibleStates(bool outsidePlay, bool duringPlay, bool paused)
    {
        return (outsidePlay, duringPlay, paused) switch
        {
            (true, true, true) => Always,
            (true, true, false) => OutsideAndDuringPlay,
            (true, false, true) => OutsidePlay,
            (true, false, false) => OutsideOnly,
            (false, true, true) => DuringAndPaused,
            (false, true, false) => DuringPlay,
            (false, false, true) => PausedOnly,
            _ => Never
        };
    }
}
