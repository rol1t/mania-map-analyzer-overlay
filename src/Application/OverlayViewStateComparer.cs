using System.Text.Json;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Compares the rendered view-state contract while ignoring transport
/// metadata that must not cause a new render by itself. Using the serialized
/// contract keeps this boundary aligned with the fields consumed by the
/// renderer, including nested arrays and diagnostic blocks.
/// </summary>
public static class OverlayViewStateComparer
{
    private static readonly JsonSerializerOptions _options = new();

    public static bool ContentEquals(OverlayViewState? left, OverlayViewState? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return JsonSerializer.Serialize(Normalize(left), _options)
            .Equals(JsonSerializer.Serialize(Normalize(right), _options), StringComparison.Ordinal);
    }

    private static OverlayViewState Normalize(OverlayViewState state) => state with
    {
        Version = 0,
        RuntimeVersion = 0,
        PresentationEpoch = string.Empty,
        // These timestamps are useful diagnostics but are not rendered. A
        // collector heartbeat must not create a new presentation version when
        // every visible metric is unchanged.
        Realtime = state.Realtime is { } realtime
            ? realtime with
            {
                UpdatedAt = DateTimeOffset.UnixEpoch
            }
            : null,
        PauseCoach = state.PauseCoach is { } pauseCoach
            ? pauseCoach with
            {
                UpdatedAt = DateTimeOffset.UnixEpoch
            }
            : null
    };
}
