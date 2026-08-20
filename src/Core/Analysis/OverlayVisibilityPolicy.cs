namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Describes when an overlay preset should be visible. The values are part of
/// the editable preset manifest contract and are intentionally analyzer-neutral.
/// </summary>
public static class OverlayVisibilityPolicy
{
    public const string Always = "always";
    public const string OutsidePlay = "outside-play";
    public const string DuringPlay = "during-play";
    public const string PausedOnly = "paused-only";
    public const string Never = "never";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            OutsidePlay => OutsidePlay,
            DuringPlay => DuringPlay,
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
            DuringPlay => isPlaying && isPaused != true,
            PausedOnly => isPlaying && isPaused == true,
            Never => false,
            _ => true
        };
    }
}
