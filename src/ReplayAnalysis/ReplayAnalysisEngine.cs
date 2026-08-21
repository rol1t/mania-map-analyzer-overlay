using System.Collections.Immutable;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// First-party replay analysis engine. Operates off-UI-thread, never exposes
/// raw replay bytes, and surfaces parser/judge/version failures as diagnostics
/// rather than default values.
/// </summary>
public sealed class ReplayAnalysisEngine : IAnalyzerEngine
{
    private readonly IReplayArtifactStore _artifactStore;
    private readonly Func<ReplayArtifact, IReadOnlyList<ReplayInputEvent>> _inputResolver;
    private readonly ReplayJudgeOptions _judgeOptions;

    public ReplayAnalysisEngine(
        IReplayArtifactStore artifactStore,
        Func<ReplayArtifact, IReadOnlyList<ReplayInputEvent>> inputResolver,
        ReplayJudgeOptions? judgeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(inputResolver);
        _artifactStore = artifactStore;
        _inputResolver = inputResolver;
        _judgeOptions = judgeOptions ?? new ReplayJudgeOptions();
        Descriptor = new AnalyzerEngineDescriptor(
            id: "replay.analysis",
            name: "Replay Analysis",
            version: "1.0.0-rice",
            capabilities: new AnalyzerEngineCapabilities(
                supportedAlgorithms: ["replay.rice"],
                supportedMetricIds: [
                    ReplayMetrics.TimingUr, ReplayMetrics.TimingMean, ReplayMetrics.TimingMedian,
                    "replay.column.*", "replay.section.*", "replay.insights.count"
                ]),
            upstreamVersion: "stable.osr.1");
    }

    public AnalyzerEngineDescriptor Descriptor
    {
        get;
    }

    public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Beatmap is required; AnalysisRequest.BeatmapContent carries .osu text.
            OsuManiaBeatmap beatmap;
            try
            {
                beatmap = OsuBeatmapParser.Parse(request.BeatmapContent, request.BeatmapContentHash);
            }
            catch (ReplayAnalysisException exception)
            {
                return Task.FromResult(AnalysisResult.Failure(
                    request,
                    Descriptor,
                    new AnalysisDiagnostic(AnalysisDiagnosticSeverity.Error, exception.Code, exception.Message, exception.ToString())));
            }

            ReplayArtifact? artifact = ResolveArtifact(request);
            if (artifact is null)
            {
                return Task.FromResult(AnalysisResult.Failure(
                    request,
                    Descriptor,
                    new AnalysisDiagnostic(AnalysisDiagnosticSeverity.Error, "replay.not_found", "No replay artifact was provided for this request.")));
            }

            IReadOnlyList<ReplayInputEvent> inputs;
            try
            {
                inputs = _inputResolver(artifact);
            }
            catch (ReplayAnalysisException exception)
            {
                return Task.FromResult(AnalysisResult.Failure(
                    request,
                    Descriptor,
                    new AnalysisDiagnostic(AnalysisDiagnosticSeverity.Error, exception.Code, exception.Message, exception.ToString())));
            }

            ReplayJudgeResult judgeResult = ReplayJudge.JudgeRice(beatmap, inputs, _judgeOptions);
            ReplayTimingStats? timing = ReplayTimingStats.Calculate(judgeResult.JudgedHits);
            IReadOnlyList<ReplayColumnStats> columns = ReplayColumnStats.Calculate(judgeResult.JudgedHits, beatmap.KeyCount);
            IReadOnlyList<ReplaySection> sections = ReplaySection.Build(judgeResult.JudgedHits);
            IReadOnlyList<ReplayInsight> insights = ReplayInsights.BuildColumnUrInsights(columns);

            Dictionary<string, JsonElement> metricElements = ReplayMetrics.BuildMetrics(timing, columns, sections, insights);

            List<SemanticMetric> metrics = metricElements
                .Select(pair => new SemanticMetric(pair.Key, pair.Value))
                .ToList();

            // Attach provenance and diagnostics.
            List<AnalysisDiagnostic> diagnostics = judgeResult.Diagnostics
                .Select(item => new AnalysisDiagnostic(
                    (AnalysisDiagnosticSeverity)item.Severity,
                    item.Code,
                    item.Message,
                    item.TechnicalDetails))
                .ToList();

            if (judgeResult.Provenance.Fidelity != ReplayAnalysisFidelity.Exact)
            {
                diagnostics.Add(new AnalysisDiagnostic(
                    AnalysisDiagnosticSeverity.Warning,
                    "replay.fidelity." + judgeResult.Provenance.Fidelity.ToString().ToLowerInvariant(),
                    $"{judgeResult.Provenance.Fidelity}: {judgeResult.Provenance.Reason}"));
            }

            AnalysisOutcome outcome = diagnostics.Any(item => item.Severity == AnalysisDiagnosticSeverity.Error)
                ? AnalysisOutcome.Partial
                : AnalysisOutcome.Success;

            // For Unsupported (LN) we surface as Failed with diagnostic.
            if (judgeResult.Provenance.Fidelity == ReplayAnalysisFidelity.Unsupported)
            {
                outcome = AnalysisOutcome.Failed;
            }

            string? actualAlgorithm = outcome is AnalysisOutcome.Failed or AnalysisOutcome.Cancelled
                ? null
                : request.RequestedAlgorithm;

            return Task.FromResult(new AnalysisResult(
                request.Key,
                Descriptor.Id,
                request.RequestedAlgorithm,
                actualAlgorithm: actualAlgorithm,
                metrics: metrics,
                diagnostics: diagnostics,
                outcome: outcome));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(AnalysisResult.Cancelled(
                request,
                Descriptor,
                new AnalysisDiagnostic(AnalysisDiagnosticSeverity.Warning, "replay.cancelled", "Replay analysis was cancelled.")));
        }
        catch (Exception exception)
        {
            return Task.FromResult(AnalysisResult.Failure(
                request,
                Descriptor,
                AnalysisDiagnostic.Error("replay.unexpected", "Unexpected replay analysis failure.", exception)));
        }
    }

    private ReplayArtifact? ResolveArtifact(AnalysisRequest request)
    {
        // Resolve via explicit option "replayArtifactId" (opaque handle id, never raw bytes).
        if (request.Options.TryGetValue("replayArtifactId", out JsonElement artifactIdElement)
            && artifactIdElement.ValueKind == JsonValueKind.String)
        {
            string artifactId = artifactIdElement.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(artifactId))
            {
                // The host holds the store; we synthesize a handle that the resolver can map.
                // Raw bytes are never read from the request itself. Validate existence via
                // _artifactStore so an unknown id surfaces as replay.not_found instead of
                // silently producing empty metrics.
                var handle = new ReplayArtifactHandle([], artifactId, fileName: null, contentHash: null);
                if (!_artifactStore.TryGetBytes(handle, out _))
                {
                    return null;
                }

                return new ReplayArtifact(handle, ReplaySourceKind.StableOsr);
            }
        }

        return null;
    }
}
