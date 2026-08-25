using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Read-only projection of the transitional MainWindow UI mirror. It is
/// deliberately a data contract so parity checks do not call UI or platform
/// APIs from the Application layer.
/// </summary>
public sealed record OverlayRuntimeLegacyProjection(
    bool OverlayMode,
    bool GameplayStateKnown,
    bool IsPlaying,
    bool? IsPaused,
    string VisibilityPolicy,
    bool OsuWindowMinimized,
    bool PresentationReady,
    bool PresentationVisible,
    bool NativeWindowVisible);

public sealed record OverlayRuntimeParityResult(IReadOnlyList<string> Differences)
{
    public bool IsMatch => Differences.Count == 0;
}

/// <summary>
/// Compares coordinator state with the transitional UI mirror. The coordinator
/// remains authoritative; this comparer is diagnostic and cannot mutate either
/// side.
/// </summary>
public static class OverlayRuntimeParityComparer
{
    public static OverlayRuntimeParityResult Compare(
        OverlayRuntimeState shadow,
        OverlayRuntimeLegacyProjection legacy,
        bool includePresentation = true)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        ArgumentNullException.ThrowIfNull(legacy);

        var differences = new List<string>();
        AddDifference(differences, "overlayMode", shadow.OverlayMode, legacy.OverlayMode);
        AddDifference(differences, "gameplayStateKnown", shadow.GameplayStateKnown, legacy.GameplayStateKnown);
        AddDifference(differences, "visibilityPolicy", shadow.VisibilityPolicy, OverlayVisibilityPolicy.Normalize(legacy.VisibilityPolicy));
        AddDifference(differences, "osuWindowMinimized", shadow.OsuWindowMinimized, legacy.OsuWindowMinimized);
        if (includePresentation)
        {
            AddDifference(differences, "presentationReady", shadow.PresentationReady, legacy.PresentationReady);
            AddDifference(differences, "presentationVisible", shadow.PresentationVisible, legacy.PresentationVisible);
        }

        if (shadow.GameplayStateKnown && legacy.GameplayStateKnown)
        {
            AddDifference(differences, "isPlaying", shadow.IsPlaying, legacy.IsPlaying);
            AddDifference(differences, "isPaused", shadow.IsPaused, legacy.IsPaused);
        }

        if (shadow.OverlayMode && legacy.OverlayMode)
        {
            bool expectedNativeVisible = OverlayVisibilityDerivation.ShouldShowNativeOverlay(shadow);
            AddDifference(differences, "nativeWindowVisible", expectedNativeVisible, legacy.NativeWindowVisible);
        }

        return new OverlayRuntimeParityResult(differences);
    }

    private static void AddDifference<T>(List<string> differences, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            differences.Add($"{field}: shadow={FormatValue(expected)}; legacy={FormatValue(actual)}");
        }
    }

    private static string FormatValue<T>(T value) => value is null ? "null" : value.ToString() ?? "null";
}
