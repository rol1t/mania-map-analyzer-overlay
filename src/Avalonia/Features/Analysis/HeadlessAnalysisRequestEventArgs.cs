using System;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>Raised before a headless analysis execution starts.</summary>
public sealed class HeadlessAnalysisRequestStartedEventArgs : EventArgs
{
    public HeadlessAnalysisRequestStartedEventArgs(
        AnalysisRequestId requestId,
        TosuBeatmapSnapshot beatmap,
        HeadlessAnalysisKey analysisKey)
    {
        if (!requestId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(analysisKey);

        RequestId = requestId;
        Beatmap = beatmap;
        AnalysisKey = analysisKey;
    }

    public AnalysisRequestId RequestId
    {
        get;
    }

    public TosuBeatmapSnapshot Beatmap
    {
        get;
    }

    public HeadlessAnalysisKey AnalysisKey
    {
        get;
    }
}

/// <summary>Raised when a versioned headless request cannot complete.</summary>
public sealed class HeadlessAnalysisRequestFailedEventArgs : EventArgs
{
    public HeadlessAnalysisRequestFailedEventArgs(
        AnalysisRequestId requestId,
        TosuBeatmapSnapshot beatmap,
        HeadlessAnalysisKey analysisKey,
        string failureCode,
        string? failureMessage)
    {
        if (!requestId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(analysisKey);

        RequestId = requestId;
        Beatmap = beatmap;
        AnalysisKey = analysisKey;
        FailureCode = string.IsNullOrWhiteSpace(failureCode)
            ? "analysis_failed"
            : failureCode.Trim();
        FailureMessage = failureMessage;
    }

    public AnalysisRequestId RequestId
    {
        get;
    }

    public TosuBeatmapSnapshot Beatmap
    {
        get;
    }

    public HeadlessAnalysisKey AnalysisKey
    {
        get;
    }

    public string FailureCode
    {
        get;
    }

    public string? FailureMessage
    {
        get;
    }
}
