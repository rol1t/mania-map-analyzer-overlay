using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

public enum AnalysisOutcome
{
    Success,
    Partial,
    Failed,
    Cancelled
}

/// <summary>
/// Immutable result returned by a headless analyzer engine. It contains both
/// the requested algorithm and, for successful executions, the algorithm
/// actually selected by adaptive modes such as Mixed.
/// </summary>
public sealed record AnalysisResult
{
    public AnalysisResult(
        AnalysisRequestKey requestKey,
        string engineId,
        string requestedAlgorithm,
        string? actualAlgorithm,
        IEnumerable<SemanticMetric>? metrics = null,
        IEnumerable<AnalysisDiagnostic>? diagnostics = null,
        AnalysisOutcome outcome = AnalysisOutcome.Success)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            throw new ArgumentException("An analyzer engine id is required.", nameof(engineId));
        }

        if (string.IsNullOrWhiteSpace(requestedAlgorithm))
        {
            throw new ArgumentException("A requested algorithm is required.", nameof(requestedAlgorithm));
        }

        if ((outcome is AnalysisOutcome.Success or AnalysisOutcome.Partial)
            && string.IsNullOrWhiteSpace(actualAlgorithm))
        {
            throw new ArgumentException(
                "Successful and partial results must identify the actual algorithm.",
                nameof(actualAlgorithm));
        }

        if ((outcome is AnalysisOutcome.Failed or AnalysisOutcome.Cancelled)
            && !string.IsNullOrWhiteSpace(actualAlgorithm))
        {
            throw new ArgumentException(
                "Failed and cancelled results cannot claim an actual algorithm.",
                nameof(actualAlgorithm));
        }

        RequestKey = requestKey;
        EngineId = engineId.Trim();
        RequestedAlgorithm = requestedAlgorithm.Trim();
        ActualAlgorithm = string.IsNullOrWhiteSpace(actualAlgorithm) ? null : actualAlgorithm.Trim();
        Metrics = (metrics ?? Array.Empty<SemanticMetric>())
            .ToImmutableDictionary(metric => metric.Id, StringComparer.OrdinalIgnoreCase);
        Diagnostics = (diagnostics ?? Array.Empty<AnalysisDiagnostic>()).ToImmutableArray();
        Outcome = outcome;
    }

    public AnalysisRequestKey RequestKey
    {
        get;
    }

    public string EngineId
    {
        get;
    }

    public string RequestedAlgorithm
    {
        get;
    }

    public string? ActualAlgorithm
    {
        get;
    }

    public ImmutableDictionary<string, SemanticMetric> Metrics
    {
        get;
    }

    public ImmutableArray<AnalysisDiagnostic> Diagnostics
    {
        get;
    }

    public AnalysisOutcome Outcome
    {
        get;
    }

    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == AnalysisDiagnosticSeverity.Error);

    public static AnalysisResult Failure(
        AnalysisRequest request,
        AnalyzerEngineDescriptor descriptor,
        AnalysisDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new AnalysisResult(
            request.Key,
            descriptor.Id,
            request.RequestedAlgorithm,
            actualAlgorithm: null,
            diagnostics: [diagnostic],
            outcome: AnalysisOutcome.Failed);
    }

    public static AnalysisResult Cancelled(
        AnalysisRequest request,
        AnalyzerEngineDescriptor descriptor,
        AnalysisDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new AnalysisResult(
            request.Key,
            descriptor.Id,
            request.RequestedAlgorithm,
            actualAlgorithm: null,
            diagnostics: [diagnostic],
            outcome: AnalysisOutcome.Cancelled);
    }
}
