namespace ManiaMapAnalyzerOverlay.RealtimeAnalysis;

/// <summary>
/// Transport-neutral diagnostic metadata retained at the normalization boundary.
/// The realtime domain consumes the normalized sample and does not inspect the
/// provider's raw payload.
/// </summary>
public sealed record RealtimeSourceDiagnostics(
    string StateName,
    int? StateNumber,
    bool? Paused);

/// <summary>
/// One normalized realtime update delivered from an infrastructure adapter to
/// the application runtime.
/// </summary>
public sealed record RealtimeTelemetryUpdate(
    string Source,
    RealtimeSourceDiagnostics Diagnostics,
    RealtimeTelemetrySample Sample,
    RealtimeAnalysisSnapshot Snapshot)
{
    public RealtimeTelemetryUpdate(
        string source,
        string rawStateName,
        int? rawStateNumber,
        bool? rawPaused,
        RealtimeTelemetrySample sample,
        RealtimeAnalysisSnapshot snapshot)
        : this(
            source,
            new RealtimeSourceDiagnostics(rawStateName, rawStateNumber, rawPaused),
            sample,
            snapshot)
    {
    }

    public string RawStateName => Diagnostics.StateName;

    public int? RawStateNumber => Diagnostics.StateNumber;

    public bool? RawPaused => Diagnostics.Paused;

    public int JudgementTotal => Sample.Judgements.Total;

    public int HitErrorSampleCount => Sample.HitErrorArray.Length;
}

/// <summary>
/// Transport-neutral source for normalized realtime telemetry.
/// </summary>
public interface IRealtimeTelemetrySource
{
    Task<RealtimeTelemetryUpdate?> ReadAsync(CancellationToken cancellationToken = default);

    void Reset();
}
