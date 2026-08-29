using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Monotonically allocated identity for one headless analysis execution.
/// A new id is required even when the beatmap and configuration are unchanged:
/// two overlapping executions are still different causal requests.
/// </summary>
public readonly record struct AnalysisRequestId(long Value)
{
    public bool IsValid => Value > 0;

    public override string ToString() => IsValid
        ? Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "legacy";
}

public enum AnalysisRequestStatus
{
    Running,
    Pending,
    Completed,
    Failed
}

/// <summary>
/// Application-owned causal slot for the latest headless analysis request.
/// The configuration identity is an opaque, canonical key supplied by the
/// adapter; it is not a user-facing string and is never parsed for control
/// flow by the reducer.
/// </summary>
public sealed record AnalysisRequestSlot(
    AnalysisRequestId RequestId,
    long BeatmapGeneration,
    string BeatmapId,
    string ConfigurationIdentity,
    AnalysisRequestStatus Status,
    AnalysisSnapshot? Snapshot = null,
    string? FailureCode = null,
    string? FailureMessage = null)
{
    public bool IsVersioned => RequestId.IsValid;
}
