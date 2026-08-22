using System;
using System.Collections.Generic;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Reports that the headless analysis pipeline produced a snapshot for a new
/// beatmap. The UI layer maps the contained outcome, diagnostics, and metadata
/// to localized status text and overlay state.
/// </summary>
public sealed class HeadlessAnalysisResultEventArgs : EventArgs
{
    public HeadlessAnalysisResultEventArgs(
        TosuBeatmapSnapshot beatmap,
        AnalysisOutcome outcome,
        string? actualAlgorithm,
        IReadOnlyList<AnalysisDiagnostic> diagnostics,
        AnalysisSnapshot snapshot,
        bool isSceneResult)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Beatmap = beatmap;
        Outcome = outcome;
        ActualAlgorithm = actualAlgorithm;
        Diagnostics = diagnostics;
        Snapshot = snapshot;
        IsSceneResult = isSceneResult;
    }

    public TosuBeatmapSnapshot Beatmap
    {
        get;
    }

    public AnalysisOutcome Outcome
    {
        get;
    }

    public string? ActualAlgorithm
    {
        get;
    }

    public IReadOnlyList<AnalysisDiagnostic> Diagnostics
    {
        get;
    }

    public AnalysisSnapshot Snapshot
    {
        get;
    }

    public bool IsSceneResult
    {
        get;
    }
}
