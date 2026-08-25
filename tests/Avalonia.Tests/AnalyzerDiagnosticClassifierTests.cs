using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class AnalyzerDiagnosticClassifierTests
{
    [Fact]
    public void ClassifiesLegacyBeatmapParseDiagnosticByStructuredFailureKind()
    {
        var diagnostic = new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Warning,
            "ANALYSIS_FAILED",
            "Текст ошибки может измениться",
            properties:
            [
                new(
                    AnalyzerDiagnosticClassifier.FailureKindProperty,
                    AnalyzerDiagnosticClassifier.BeatmapParseFailureKind)
            ]);

        Assert.Equal(AnalyzerDiagnosticKind.BeatmapParse, AnalyzerDiagnosticClassifier.Classify(diagnostic));
        Assert.False(AnalyzerDiagnosticClassifier.IsTransientEngineFailure(diagnostic));
    }

    [Theory]
    [InlineData("WORKER_CRASHED", AnalyzerDiagnosticKind.WorkerCrashed)]
    [InlineData("engine.bootstrap_failed", AnalyzerDiagnosticKind.RuntimeIncompatible)]
    [InlineData("engine.runtime_reset", AnalyzerDiagnosticKind.TransientRuntime)]
    [InlineData("engine.analysis_bridge_failed", AnalyzerDiagnosticKind.TransientRuntime)]
    [InlineData("ANALYSIS_FAILED", AnalyzerDiagnosticKind.Unknown)]
    public void UsesDiagnosticCodesForRuntimeControlFlow(string code, AnalyzerDiagnosticKind expected)
    {
        var diagnostic = new AnalysisDiagnostic(AnalysisDiagnosticSeverity.Error, code, "arbitrary text");

        Assert.Equal(expected, AnalyzerDiagnosticClassifier.Classify(diagnostic));
    }
}
