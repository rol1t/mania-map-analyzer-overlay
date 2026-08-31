using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Monotonically allocated identity for one exact replay analysis execution.
/// Replay requests have their own identity space so an imported replay cannot
/// be confused with a headless map-analysis completion.
/// </summary>
public readonly record struct ReplayRequestId(long Value)
{
    public bool IsValid => Value > 0;

    public override string ToString() => IsValid
        ? Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "legacy";
}

public enum ReplayRequestStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Application-owned causal slot for the latest exact replay request. The
/// result is kept as a replay-only contract and never converted into the
/// headless analysis slot.
/// </summary>
public sealed record ReplayAnalysisRequestSlot(
    ReplayRequestId RequestId,
    long BeatmapGeneration,
    string BeatmapId,
    string BeatmapHash,
    ReplayRequestStatus Status,
    ReplayOverlaySnapshot? Snapshot = null,
    string? FailureCode = null,
    string? FailureMessage = null)
{
    public bool IsVersioned => RequestId.IsValid;
}
