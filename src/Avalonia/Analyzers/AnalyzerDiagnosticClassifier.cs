using System;
using System.Collections.Generic;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

public enum AnalyzerDiagnosticKind
{
    Unknown,
    BeatmapParse,
    WorkerCrashed,
    RuntimeIncompatible,
    TransientRuntime
}

/// <summary>
/// Classifies analyzer diagnostics by structured code/properties. The
/// analyzer protocol is external, so the bridge may attach a failure kind
/// when an older engine only supplies human-readable text; consumers never
/// need to inspect that text to make a control-flow decision.
/// </summary>
public static class AnalyzerDiagnosticClassifier
{
    public const string FailureKindProperty = "failureKind";
    public const string BeatmapParseFailureKind = "beatmap_parse";

    private static readonly HashSet<string> _runtimeIncompatibleCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "engine.bootstrap_failed",
        "engine.runtime_incompatible",
        "RUNTIME_BOOTSTRAP_FAILED",
        "PIPELINE_COMPATIBILITY_FAILED",
        "PROTOCOL_MESSAGE_FAILED",
        "INVALID_WORKER_RESPONSE",
        "INVALID_PROTOCOL_MESSAGE"
    };

    private static readonly HashSet<string> _transientCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "engine.runtime_reset",
        "engine.runtime_reset_failed",
        "engine.runtime_disposed",
        "engine.runtime_dispose_failed",
        "engine.request_dispatch_failed",
        "engine.analysis_bridge_failed",
        "analysis.cancelled",
        "ANALYSIS_CANCELLED",
        "ANALYSIS_SUPERSEDED",
        "ANALYSIS_STALE_GENERATION",
        "STALE_ANALYSIS"
    };

    public static AnalyzerDiagnosticKind Classify(AnalysisDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        if (diagnostic.Properties.TryGetValue(FailureKindProperty, out var failureKind) &&
            string.Equals(failureKind, BeatmapParseFailureKind, StringComparison.OrdinalIgnoreCase))
        {
            return AnalyzerDiagnosticKind.BeatmapParse;
        }

        return ClassifyCode(diagnostic.Code);
    }

    public static AnalyzerDiagnosticKind Classify(AnalyzerEngineDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return ClassifyCode(diagnostic.Code);
    }

    private static AnalyzerDiagnosticKind ClassifyCode(string code)
    {
        if (string.Equals(code, "WORKER_CRASHED", StringComparison.OrdinalIgnoreCase))
        {
            return AnalyzerDiagnosticKind.WorkerCrashed;
        }

        if (_transientCodes.Contains(code))
        {
            return AnalyzerDiagnosticKind.TransientRuntime;
        }

        return _runtimeIncompatibleCodes.Contains(code)
            ? AnalyzerDiagnosticKind.RuntimeIncompatible
            : AnalyzerDiagnosticKind.Unknown;
    }

    public static bool IsTransientEngineFailure(AnalysisDiagnostic diagnostic)
    {
        var kind = Classify(diagnostic);
        return kind is AnalyzerDiagnosticKind.WorkerCrashed or
            AnalyzerDiagnosticKind.RuntimeIncompatible or
            AnalyzerDiagnosticKind.TransientRuntime;
    }
}
