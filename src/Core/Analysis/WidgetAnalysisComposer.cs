using System.Collections.Immutable;
using System.Text.Json;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Resolves semantic widget metrics from several analyzer sources without any
/// dependency on a renderer, transport, or platform UI.
/// </summary>
public sealed class WidgetAnalysisComposer
{
    private readonly IAnalysisDiagnostics _diagnostics;

    public WidgetAnalysisComposer(IAnalysisDiagnostics? diagnostics = null)
    {
        _diagnostics = diagnostics ?? NullAnalysisDiagnostics.Instance;
    }

    public ComposedWidgetSnapshot Compose(
        WidgetAnalysisSpec spec,
        IEnumerable<AnalysisSourceResult> sourceResults)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(sourceResults);

        var emittedDiagnostics = new List<AnalysisDiagnostic>();
        var results = IndexResults(sourceResults);
        var sourceStates = EvaluateSources(spec, results, emittedDiagnostics);
        var resolvedMetrics = new List<ResolvedSemanticMetric>();
        var usedFallback = false;

        foreach (var binding in spec.Bindings)
        {
            var resolution = ResolveBinding(binding, sourceStates);
            if (resolution.Metric is null)
            {
                AddDiagnostic(
                    emittedDiagnostics,
                    new AnalysisDiagnostic(
                        AnalysisDiagnosticSeverity.Error,
                        "composition.metric_missing",
                        $"No configured analyzer source produced metric '{binding.TargetMetricId}'.",
                        properties:
                        [
                            new KeyValuePair<string, string>("widgetId", spec.WidgetId),
                            new KeyValuePair<string, string>("targetMetricId", binding.TargetMetricId)
                        ]));
                continue;
            }

            resolvedMetrics.Add(resolution.Metric);
            if (resolution.CandidateIndex > 0)
            {
                usedFallback = true;
                AddDiagnostic(
                    emittedDiagnostics,
                    new AnalysisDiagnostic(
                        AnalysisDiagnosticSeverity.Warning,
                        "composition.metric_fallback",
                        $"Metric '{binding.TargetMetricId}' was resolved from fallback source " +
                        $"'{resolution.Metric.Provenance.SourceId}'.",
                        properties:
                        [
                            new KeyValuePair<string, string>("widgetId", spec.WidgetId),
                            new KeyValuePair<string, string>("targetMetricId", binding.TargetMetricId),
                            new KeyValuePair<string, string>("sourceId", resolution.Metric.Provenance.SourceId)
                        ]));
            }
        }

        var outcome = ResolveOutcome(
            resolvedMetrics.Count,
            spec.Bindings.Length,
            sourceStates.Values,
            usedFallback);
        return new ComposedWidgetSnapshot(spec.WidgetId, outcome, resolvedMetrics, emittedDiagnostics);
    }

    private static Dictionary<string, AnalysisSourceResult> IndexResults(
        IEnumerable<AnalysisSourceResult> sourceResults)
    {
        var indexed = new Dictionary<string, AnalysisSourceResult>(StringComparer.Ordinal);
        foreach (var sourceResult in sourceResults)
        {
            ArgumentNullException.ThrowIfNull(sourceResult);
            if (!indexed.TryAdd(sourceResult.SourceId, sourceResult))
            {
                throw new ArgumentException(
                    $"Analysis source result '{sourceResult.SourceId}' was supplied more than once.",
                    nameof(sourceResults));
            }
        }

        return indexed;
    }

    private Dictionary<string, SourceState> EvaluateSources(
        WidgetAnalysisSpec spec,
        IReadOnlyDictionary<string, AnalysisSourceResult> sourceResults,
        List<AnalysisDiagnostic> emittedDiagnostics)
    {
        var configuredIds = spec.Sources
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var unconfigured in sourceResults.Keys.Where(sourceId => !configuredIds.Contains(sourceId)))
        {
            AddDiagnostic(
                emittedDiagnostics,
                new AnalysisDiagnostic(
                    AnalysisDiagnosticSeverity.Warning,
                    "composition.source_unconfigured",
                    $"Result from unconfigured analyzer source '{unconfigured}' was ignored.",
                    properties:
                    [
                        new KeyValuePair<string, string>("widgetId", spec.WidgetId),
                        new KeyValuePair<string, string>("sourceId", unconfigured)
                    ]));
        }

        var states = new Dictionary<string, SourceState>(StringComparer.Ordinal);
        foreach (var source in spec.Sources)
        {
            if (!sourceResults.TryGetValue(source.SourceId, out var sourceOutput) || sourceOutput.Result is null)
            {
                AddDiagnostic(
                    emittedDiagnostics,
                    new AnalysisDiagnostic(
                        AnalysisDiagnosticSeverity.Warning,
                        "composition.source_missing",
                        $"Analyzer source '{source.SourceId}' did not produce a result.",
                        properties:
                        [
                            new KeyValuePair<string, string>("widgetId", spec.WidgetId),
                            new KeyValuePair<string, string>("sourceId", source.SourceId)
                        ]));
                states.Add(source.SourceId, new SourceState(source, null, IsUsable: false, HasProblem: true));
                continue;
            }

            var result = sourceOutput.Result;
            var contractProblem = ValidateSourceResult(source, result, spec.WidgetId, emittedDiagnostics);
            AppendSourceDiagnostics(source.SourceId, result.Diagnostics, emittedDiagnostics);
            var usable = !contractProblem
                && result.Outcome is AnalysisOutcome.Success or AnalysisOutcome.Partial;
            var hasProblem = contractProblem
                || result.Outcome != AnalysisOutcome.Success
                || result.HasErrors;

            if (result.Outcome is AnalysisOutcome.Failed or AnalysisOutcome.Cancelled)
            {
                AddDiagnostic(
                    emittedDiagnostics,
                    new AnalysisDiagnostic(
                        result.Outcome == AnalysisOutcome.Failed
                            ? AnalysisDiagnosticSeverity.Error
                            : AnalysisDiagnosticSeverity.Warning,
                        result.Outcome == AnalysisOutcome.Failed
                            ? "composition.source_failed"
                            : "composition.source_cancelled",
                        $"Analyzer source '{source.SourceId}' finished with outcome '{result.Outcome}'.",
                        properties:
                        [
                            new KeyValuePair<string, string>("widgetId", spec.WidgetId),
                            new KeyValuePair<string, string>("sourceId", source.SourceId)
                        ]));
            }

            states.Add(source.SourceId, new SourceState(source, result, usable, hasProblem));
        }

        return states;
    }

    private bool ValidateSourceResult(
        AnalysisSourceSpec source,
        AnalysisResult result,
        string widgetId,
        List<AnalysisDiagnostic> emittedDiagnostics)
    {
        var mismatch = result.RequestKey != source.Request.Key
            || !string.Equals(result.EngineId, source.Engine.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                result.RequestedAlgorithm,
                source.Request.RequestedAlgorithm,
                StringComparison.Ordinal);
        if (!mismatch)
        {
            return false;
        }

        AddDiagnostic(
            emittedDiagnostics,
            new AnalysisDiagnostic(
                AnalysisDiagnosticSeverity.Error,
                "composition.source_contract_mismatch",
                $"Analyzer source '{source.SourceId}' returned a result for a different request.",
                properties:
                [
                    new KeyValuePair<string, string>("widgetId", widgetId),
                    new KeyValuePair<string, string>("sourceId", source.SourceId),
                    new KeyValuePair<string, string>("engineId", result.EngineId)
                ]));
        return true;
    }

    private static void AppendSourceDiagnostics(
        string sourceId,
        ImmutableArray<AnalysisDiagnostic> sourceDiagnostics,
        List<AnalysisDiagnostic> emittedDiagnostics)
    {
        foreach (var sourceDiagnostic in sourceDiagnostics)
        {
            var properties = sourceDiagnostic.Properties.SetItem("sourceId", sourceId);
            emittedDiagnostics.Add(new AnalysisDiagnostic(
                sourceDiagnostic.Severity,
                sourceDiagnostic.Code,
                sourceDiagnostic.Message,
                sourceDiagnostic.TechnicalDetails,
                properties));
        }
    }

    private static BindingResolution ResolveBinding(
        WidgetMetricBinding binding,
        IReadOnlyDictionary<string, SourceState> sourceStates)
    {
        for (var index = 0; index < binding.Candidates.Length; index++)
        {
            var candidate = binding.Candidates[index];
            var sourceState = sourceStates[candidate.SourceId];
            if (!sourceState.IsUsable
                || sourceState.Result is null
                || !sourceState.Result.Metrics.TryGetValue(candidate.MetricId, out var metric))
            {
                continue;
            }

            if (metric.Value.ValueKind == JsonValueKind.Null && !binding.AllowsNull)
            {
                continue;
            }

            var provenance = new AnalysisMetricProvenance(
                sourceState.Spec.SourceId,
                candidate.MetricId,
                sourceState.Result.EngineId,
                sourceState.Spec.Engine.Version,
                sourceState.Spec.Engine.UpstreamVersion,
                sourceState.Spec.Request.Configuration.Version,
                sourceState.Result.RequestedAlgorithm,
                sourceState.Result.ActualAlgorithm,
                sourceState.Result.Outcome);
            return new BindingResolution(
                new ResolvedSemanticMetric(binding.TargetMetricId, metric, provenance),
                index);
        }

        return new BindingResolution(null, -1);
    }

    private static AnalysisOutcome ResolveOutcome(
        int resolvedMetricCount,
        int bindingCount,
        IEnumerable<SourceState> sourceStates,
        bool usedFallback)
    {
        if (resolvedMetricCount == 0)
        {
            return AnalysisOutcome.Failed;
        }

        if (resolvedMetricCount != bindingCount
            || usedFallback
            || sourceStates.Any(source => source.HasProblem))
        {
            return AnalysisOutcome.Partial;
        }

        return AnalysisOutcome.Success;
    }

    private void AddDiagnostic(
        List<AnalysisDiagnostic> emittedDiagnostics,
        AnalysisDiagnostic diagnostic)
    {
        emittedDiagnostics.Add(diagnostic);
        _diagnostics.Report(diagnostic);
    }

    private sealed record SourceState(
        AnalysisSourceSpec Spec,
        AnalysisResult? Result,
        bool IsUsable,
        bool HasProblem);

    private sealed record BindingResolution(
        ResolvedSemanticMetric? Metric,
        int CandidateIndex);
}
