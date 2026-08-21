using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// One stable analyzer source participating in a widget composition.
/// </summary>
public sealed record AnalysisSourceSpec
{
    public AnalysisSourceSpec(
        string sourceId,
        AnalysisRequest request,
        AnalyzerEngineDescriptor engine)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("An analysis source id is required.", nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(engine);

        if (!string.Equals(request.EngineId, engine.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Source request engine '{request.EngineId}' does not match descriptor '{engine.Id}'.",
                nameof(engine));
        }

        SourceId = sourceId.Trim();
        Request = request;
        Engine = engine;
    }

    public string SourceId
    {
        get;
    }

    public AnalysisRequest Request
    {
        get;
    }

    public AnalyzerEngineDescriptor Engine
    {
        get;
    }
}

public sealed record SourceMetricCandidate
{
    public SourceMetricCandidate(string sourceId, string metricId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("A source id is required.", nameof(sourceId));
        }

        if (string.IsNullOrWhiteSpace(metricId))
        {
            throw new ArgumentException("A source metric id is required.", nameof(metricId));
        }

        SourceId = sourceId.Trim();
        MetricId = metricId.Trim();
    }

    public string SourceId
    {
        get;
    }

    public string MetricId
    {
        get;
    }
}

/// <summary>
/// Maps one widget semantic metric to ordered analyzer source candidates.
/// </summary>
public sealed record WidgetMetricBinding
{
    public WidgetMetricBinding(
        string targetMetricId,
        IEnumerable<SourceMetricCandidate> candidates,
        bool allowsNull = false)
    {
        if (string.IsNullOrWhiteSpace(targetMetricId))
        {
            throw new ArgumentException("A target metric id is required.", nameof(targetMetricId));
        }

        ArgumentNullException.ThrowIfNull(candidates);
        var normalizedCandidates = candidates.ToImmutableArray();
        if (normalizedCandidates.IsEmpty)
        {
            throw new ArgumentException("At least one metric candidate is required.", nameof(candidates));
        }

        var duplicateCandidate = normalizedCandidates
            .GroupBy(
                candidate => string.Concat(candidate.SourceId, "\n", candidate.MetricId),
                StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCandidate is not null)
        {
            throw new ArgumentException(
                $"Metric binding '{targetMetricId}' contains a duplicate source candidate.",
                nameof(candidates));
        }

        TargetMetricId = targetMetricId.Trim();
        Candidates = normalizedCandidates;
        AllowsNull = allowsNull;
    }

    public string TargetMetricId
    {
        get;
    }

    public ImmutableArray<SourceMetricCandidate> Candidates
    {
        get;
    }

    /// <summary>
    /// Indicates that JSON null is a valid, present value for this semantic
    /// target. Otherwise a null candidate is treated as unavailable and the
    /// next configured candidate is evaluated.
    /// </summary>
    public bool AllowsNull
    {
        get;
    }
}

/// <summary>
/// Immutable composition definition for a widget. Duplicate source ids,
/// duplicate targets, and bindings to unknown sources are rejected explicitly.
/// </summary>
public sealed record WidgetAnalysisSpec
{
    public WidgetAnalysisSpec(
        string widgetId,
        IEnumerable<AnalysisSourceSpec> sources,
        IEnumerable<WidgetMetricBinding> bindings)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new ArgumentException("A widget id is required.", nameof(widgetId));
        }

        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(bindings);

        var normalizedSources = sources.ToImmutableArray();
        var normalizedBindings = bindings.ToImmutableArray();
        if (normalizedSources.IsEmpty)
        {
            throw new ArgumentException("At least one analysis source is required.", nameof(sources));
        }

        if (normalizedBindings.IsEmpty)
        {
            throw new ArgumentException("At least one metric binding is required.", nameof(bindings));
        }

        EnsureUniqueSourceIds(normalizedSources, nameof(sources));
        EnsureUniqueTargetIds(normalizedBindings, nameof(bindings));
        EnsureKnownCandidateSources(normalizedSources, normalizedBindings, nameof(bindings));

        WidgetId = widgetId.Trim();
        Sources = normalizedSources;
        Bindings = normalizedBindings;
    }

    public string WidgetId
    {
        get;
    }

    public ImmutableArray<AnalysisSourceSpec> Sources
    {
        get;
    }

    public ImmutableArray<WidgetMetricBinding> Bindings
    {
        get;
    }

    private static void EnsureUniqueSourceIds(
        ImmutableArray<AnalysisSourceSpec> sources,
        string parameterName)
    {
        var duplicate = sources
            .GroupBy(source => source.SourceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Analysis source id '{duplicate.Key}' is configured more than once.",
                parameterName);
        }
    }

    private static void EnsureUniqueTargetIds(
        ImmutableArray<WidgetMetricBinding> bindings,
        string parameterName)
    {
        var duplicate = bindings
            .GroupBy(binding => binding.TargetMetricId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Target metric '{duplicate.Key}' is bound more than once.",
                parameterName);
        }
    }

    private static void EnsureKnownCandidateSources(
        ImmutableArray<AnalysisSourceSpec> sources,
        ImmutableArray<WidgetMetricBinding> bindings,
        string parameterName)
    {
        var sourceIds = sources
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var unknownCandidate = bindings
            .SelectMany(binding => binding.Candidates)
            .FirstOrDefault(candidate => !sourceIds.Contains(candidate.SourceId));
        if (unknownCandidate is not null)
        {
            throw new ArgumentException(
                $"Metric candidate references unknown source '{unknownCandidate.SourceId}'.",
                parameterName);
        }
    }
}

/// <summary>
/// Associates a completed analyzer result with its stable source id. A null
/// result explicitly represents a source that did not produce output.
/// </summary>
public sealed record AnalysisSourceResult
{
    public AnalysisSourceResult(string sourceId, AnalysisResult? result)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("An analysis source id is required.", nameof(sourceId));
        }

        SourceId = sourceId.Trim();
        Result = result;
    }

    public string SourceId
    {
        get;
    }

    public AnalysisResult? Result
    {
        get;
    }
}

public sealed record AnalysisMetricProvenance(
    string SourceId,
    string SourceMetricId,
    string EngineId,
    string EngineVersion,
    string UpstreamVersion,
    string ConfigurationVersion,
    string RequestedAlgorithm,
    string? ActualAlgorithm,
    AnalysisOutcome SourceOutcome);

public sealed record ResolvedSemanticMetric(
    string TargetMetricId,
    SemanticMetric Metric,
    AnalysisMetricProvenance Provenance);

public sealed record ComposedWidgetSnapshot
{
    public ComposedWidgetSnapshot(
        string widgetId,
        AnalysisOutcome outcome,
        IEnumerable<ResolvedSemanticMetric> metrics,
        IEnumerable<AnalysisDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new ArgumentException("A widget id is required.", nameof(widgetId));
        }

        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        WidgetId = widgetId.Trim();
        Outcome = outcome;
        Metrics = metrics.ToImmutableDictionary(
            metric => metric.TargetMetricId,
            StringComparer.Ordinal);
        Diagnostics = diagnostics.ToImmutableArray();
    }

    public string WidgetId
    {
        get;
    }

    public AnalysisOutcome Outcome
    {
        get;
    }

    public ImmutableDictionary<string, ResolvedSemanticMetric> Metrics
    {
        get;
    }

    public ImmutableArray<AnalysisDiagnostic> Diagnostics
    {
        get;
    }
}
