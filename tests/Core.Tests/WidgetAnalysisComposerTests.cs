using System.Collections.Concurrent;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class WidgetAnalysisComposerTests
{
    [Fact]
    public void ComposesMmaStarRatingAndSecondEngineLnMetric()
    {
        var mma = CreateSource(
            "mma",
            "mma-engine",
            engineVersion: "2.1",
            upstreamVersion: "mma-5.4",
            configurationVersion: "mma-config-3");
        var ln = CreateSource(
            "ln",
            "ln-engine",
            engineVersion: "1.4",
            upstreamVersion: "ln-2.0",
            requestedAlgorithm: "LnRank");
        var spec = new WidgetAnalysisSpec(
            "combined-widget",
            [mma, ln],
            [
                Binding("difficulty.star", ("mma", "difficulty.star")),
                Binding("difficulty.ln", ("ln", "difficulty.ln"))
            ]);
        var mmaResult = Success(
            mma,
            actualAlgorithm: "Roxy",
            SemanticMetric.FromValue("difficulty.star", 6.25, unit: "SR"));
        var lnResult = Success(
            ln,
            actualAlgorithm: "LnRank",
            SemanticMetric.FromValue("difficulty.ln", 7.1, unit: "LN"));

        var snapshot = new WidgetAnalysisComposer().Compose(
            spec,
            [new AnalysisSourceResult("mma", mmaResult), new AnalysisSourceResult("ln", lnResult)]);

        Assert.Equal(AnalysisOutcome.Success, snapshot.Outcome);
        Assert.Equal(6.25, snapshot.Metrics["difficulty.star"].Metric.Value.GetDouble());
        Assert.Equal(7.1, snapshot.Metrics["difficulty.ln"].Metric.Value.GetDouble());

        var provenance = snapshot.Metrics["difficulty.star"].Provenance;
        Assert.Equal("mma", provenance.SourceId);
        Assert.Equal("mma-engine", provenance.EngineId);
        Assert.Equal("Mixed", provenance.RequestedAlgorithm);
        Assert.Equal("Roxy", provenance.ActualAlgorithm);
        Assert.Equal("2.1", provenance.EngineVersion);
        Assert.Equal("mma-5.4", provenance.UpstreamVersion);
        Assert.Equal("mma-config-3", provenance.ConfigurationVersion);
        Assert.Equal(AnalysisOutcome.Success, provenance.SourceOutcome);
    }

    [Fact]
    public void FallsBackFromFailedAndMissingPreferredSources()
    {
        var failed = CreateSource("failed", "failed-engine");
        var missing = CreateSource("missing", "missing-engine");
        var fallback = CreateSource("fallback", "fallback-engine");
        var spec = new WidgetAnalysisSpec(
            "fallback-widget",
            [failed, missing, fallback],
            [
                Binding(
                    "difficulty.star",
                    ("failed", "difficulty.star"),
                    ("fallback", "difficulty.star")),
                Binding(
                    "difficulty.ln",
                    ("missing", "difficulty.ln"),
                    ("fallback", "difficulty.ln"))
            ]);
        var sourceFailure = new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Error,
            "engine.unavailable",
            "The preferred engine failed.");
        var failedResult = new AnalysisResult(
            failed.Request.Key,
            failed.Engine.Id,
            failed.Request.RequestedAlgorithm,
            actualAlgorithm: null,
            diagnostics: [sourceFailure],
            outcome: AnalysisOutcome.Failed);
        var fallbackResult = Success(
            fallback,
            "Sunny",
            SemanticMetric.FromValue("difficulty.star", 5.5),
            SemanticMetric.FromValue("difficulty.ln", 6.0));

        var snapshot = new WidgetAnalysisComposer().Compose(
            spec,
            [
                new AnalysisSourceResult("failed", failedResult),
                new AnalysisSourceResult("fallback", fallbackResult)
            ]);

        Assert.Equal(AnalysisOutcome.Partial, snapshot.Outcome);
        Assert.Equal("fallback", snapshot.Metrics["difficulty.star"].Provenance.SourceId);
        Assert.Equal("fallback", snapshot.Metrics["difficulty.ln"].Provenance.SourceId);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "composition.source_failed");
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "composition.source_missing");
        Assert.Equal(
            2,
            snapshot.Diagnostics.Count(diagnostic => diagnostic.Code == "composition.metric_fallback"));
        Assert.Contains(
            snapshot.Diagnostics,
            diagnostic => diagnostic.Code == "engine.unavailable"
                && diagnostic.Properties["sourceId"] == "failed");
    }

    [Fact]
    public void PresentFalseZeroAndNullValuesDoNotTriggerFallback()
    {
        var preferred = CreateSource("preferred", "preferred-engine");
        var fallback = CreateSource("fallback", "fallback-engine");
        var spec = new WidgetAnalysisSpec(
            "truthiness-widget",
            [preferred, fallback],
            [
                Binding("metric.false", ("preferred", "metric.false"), ("fallback", "metric.false")),
                Binding("metric.zero", ("preferred", "metric.zero"), ("fallback", "metric.zero")),
                NullableBinding("metric.null", ("preferred", "metric.null"), ("fallback", "metric.null"))
            ]);
        var preferredResult = Success(
            preferred,
            "Mixed",
            SemanticMetric.FromValue("metric.false", false),
            SemanticMetric.FromValue("metric.zero", 0),
            SemanticMetric.FromValue<object?>("metric.null", null));
        var fallbackResult = Success(
            fallback,
            "Sunny",
            SemanticMetric.FromValue("metric.false", true),
            SemanticMetric.FromValue("metric.zero", 99),
            SemanticMetric.FromValue("metric.null", "fallback"));

        var snapshot = new WidgetAnalysisComposer().Compose(
            spec,
            [
                new AnalysisSourceResult("preferred", preferredResult),
                new AnalysisSourceResult("fallback", fallbackResult)
            ]);

        Assert.Equal(AnalysisOutcome.Success, snapshot.Outcome);
        Assert.False(snapshot.Metrics["metric.false"].Metric.Value.GetBoolean());
        Assert.Equal(0, snapshot.Metrics["metric.zero"].Metric.Value.GetInt32());
        Assert.Equal(JsonValueKind.Null, snapshot.Metrics["metric.null"].Metric.Value.ValueKind);
        Assert.All(snapshot.Metrics.Values, metric => Assert.Equal("preferred", metric.Provenance.SourceId));
        Assert.DoesNotContain(
            snapshot.Diagnostics,
            diagnostic => diagnostic.Code == "composition.metric_fallback");
    }

    [Fact]
    public void NullUsesFallbackWhenTargetContractDoesNotAllowNull()
    {
        var preferred = CreateSource("preferred", "preferred-engine");
        var fallback = CreateSource("fallback", "fallback-engine");
        var spec = new WidgetAnalysisSpec(
            "non-null-widget",
            [preferred, fallback],
            [Binding("difficulty.ln", ("preferred", "difficulty.ln"), ("fallback", "difficulty.ln"))]);
        var preferredResult = Success(
            preferred,
            "Mixed",
            SemanticMetric.FromValue<object?>("difficulty.ln", null));
        var fallbackResult = Success(
            fallback,
            "Sunny",
            SemanticMetric.FromValue("difficulty.ln", 6.0));

        var snapshot = new WidgetAnalysisComposer().Compose(
            spec,
            [
                new AnalysisSourceResult("preferred", preferredResult),
                new AnalysisSourceResult("fallback", fallbackResult)
            ]);

        Assert.Equal(AnalysisOutcome.Partial, snapshot.Outcome);
        Assert.Equal("fallback", snapshot.Metrics["difficulty.ln"].Provenance.SourceId);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "composition.metric_fallback");
    }

    [Fact]
    public void RejectsDuplicateTargetBindings()
    {
        var source = CreateSource("mma", "mma-engine");

        var exception = Assert.Throws<ArgumentException>(() => new WidgetAnalysisSpec(
            "invalid-widget",
            [source],
            [
                Binding("difficulty.star", ("mma", "difficulty.star")),
                Binding("difficulty.star", ("mma", "difficulty.alternate"))
            ]));

        Assert.Contains("bound more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateSourceResultsInsteadOfOverwriting()
    {
        var source = CreateSource("mma", "mma-engine");
        var spec = new WidgetAnalysisSpec(
            "invalid-results-widget",
            [source],
            [Binding("difficulty.star", ("mma", "difficulty.star"))]);
        var result = Success(
            source,
            "Mixed",
            SemanticMetric.FromValue("difficulty.star", 5.0));

        var exception = Assert.Throws<ArgumentException>(() => new WidgetAnalysisComposer().Compose(
            spec,
            [new AnalysisSourceResult("mma", result), new AnalysisSourceResult("mma", result)]));

        Assert.Contains("supplied more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedBindingsPreservePartialAndFailedOutcomes()
    {
        var source = CreateSource("mma", "mma-engine");
        var partialSpec = new WidgetAnalysisSpec(
            "partial-widget",
            [source],
            [
                Binding("difficulty.star", ("mma", "difficulty.star")),
                Binding("difficulty.ln", ("mma", "difficulty.ln"))
            ]);
        var failedSpec = new WidgetAnalysisSpec(
            "failed-widget",
            [source],
            [Binding("difficulty.ln", ("mma", "difficulty.ln"))]);
        var result = Success(
            source,
            "Mixed",
            SemanticMetric.FromValue("difficulty.star", 5.0));
        var composer = new WidgetAnalysisComposer();

        var partial = composer.Compose(partialSpec, [new AnalysisSourceResult("mma", result)]);
        var failed = composer.Compose(failedSpec, [new AnalysisSourceResult("mma", result)]);

        Assert.Equal(AnalysisOutcome.Partial, partial.Outcome);
        Assert.Equal(AnalysisOutcome.Failed, failed.Outcome);
        Assert.Contains(partial.Diagnostics, diagnostic => diagnostic.Code == "composition.metric_missing");
        Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Code == "composition.metric_missing");
    }

    [Fact]
    public void ReportsCompositionDiagnosticsToCoreSink()
    {
        var source = CreateSource("mma", "mma-engine");
        var spec = new WidgetAnalysisSpec(
            "logged-widget",
            [source],
            [Binding("difficulty.star", ("mma", "difficulty.star"))]);
        var sink = new RecordingDiagnostics();

        var snapshot = new WidgetAnalysisComposer(sink).Compose(spec, []);

        Assert.Equal(AnalysisOutcome.Failed, snapshot.Outcome);
        Assert.NotEmpty(sink.Entries);
        Assert.Contains(sink.Entries, diagnostic => diagnostic.Code == "composition.source_missing");
    }

    [Fact]
    public void PreservesSourceDiagnosticWithoutReportingItAgain()
    {
        var source = CreateSource("mma", "mma-engine");
        var spec = new WidgetAnalysisSpec(
            "diagnostic-fanout-widget",
            [source],
            [Binding("difficulty.star", ("mma", "difficulty.star"))]);
        var sourceDiagnostic = new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Warning,
            "engine.approximation",
            "The analyzer used an approximation.");
        var result = new AnalysisResult(
            source.Request.Key,
            source.Engine.Id,
            source.Request.RequestedAlgorithm,
            "Mixed",
            [SemanticMetric.FromValue("difficulty.star", 5.0)],
            [sourceDiagnostic]);
        var sink = new RecordingDiagnostics();

        var snapshot = new WidgetAnalysisComposer(sink).Compose(
            spec,
            [new AnalysisSourceResult("mma", result)]);

        Assert.Contains(
            snapshot.Diagnostics,
            diagnostic => diagnostic.Code == "engine.approximation"
                && diagnostic.Properties["sourceId"] == "mma");
        Assert.DoesNotContain(sink.Entries, diagnostic => diagnostic.Code == "engine.approximation");
    }

    private static AnalysisSourceSpec CreateSource(
        string sourceId,
        string engineId,
        string engineVersion = "1.0",
        string upstreamVersion = "upstream-1",
        string configurationVersion = "config-1",
        string requestedAlgorithm = "Mixed")
    {
        var request = new AnalysisRequest(
            engineId,
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents",
            requestedAlgorithm,
            "widget-profile",
            configurationVersion: configurationVersion);
        var descriptor = new AnalyzerEngineDescriptor(
            engineId,
            engineId,
            engineVersion,
            upstreamVersion: upstreamVersion);
        return new AnalysisSourceSpec(sourceId, request, descriptor);
    }

    private static WidgetMetricBinding Binding(
        string targetMetricId,
        params (string SourceId, string MetricId)[] candidates)
    {
        return new WidgetMetricBinding(
            targetMetricId,
            candidates.Select(candidate => new SourceMetricCandidate(candidate.SourceId, candidate.MetricId)));
    }

    private static WidgetMetricBinding NullableBinding(
        string targetMetricId,
        params (string SourceId, string MetricId)[] candidates)
    {
        return new WidgetMetricBinding(
            targetMetricId,
            candidates.Select(candidate => new SourceMetricCandidate(candidate.SourceId, candidate.MetricId)),
            allowsNull: true);
    }

    private static AnalysisResult Success(
        AnalysisSourceSpec source,
        string actualAlgorithm,
        params SemanticMetric[] metrics)
    {
        return new AnalysisResult(
            source.Request.Key,
            source.Engine.Id,
            source.Request.RequestedAlgorithm,
            actualAlgorithm,
            metrics);
    }

    private sealed class RecordingDiagnostics : IAnalysisDiagnostics
    {
        public ConcurrentBag<AnalysisDiagnostic> Entries { get; } = [];

        public void Report(AnalysisDiagnostic diagnostic)
        {
            Entries.Add(diagnostic);
        }
    }
}
