using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Owns the user-selected replay artifact and runs exact post-play analysis.
/// The UI supplies file bytes explicitly; the session keeps them behind an
/// opaque artifact handle and never puts them into settings or WebView scripts.
/// </summary>
public sealed class ReplayAnalysisSession
{
    private const int MaximumReplayBytes = 64 * 1024 * 1024;
    private readonly InMemoryReplayArtifactStore _artifactStore = new();
    private readonly ReplayAnalysisEngine _engine;
    private static long _nextRequestId;
    private ReplayArtifactHandle? _artifactHandle;

    public ReplayAnalysisSession()
    {
        _engine = new ReplayAnalysisEngine(_artifactStore);
    }

    public string? SelectedFileName
    {
        get;
        private set;
    }

    public bool HasSelectedReplay => _artifactHandle is not null;

    public void Import(ReadOnlyMemory<byte> replayBytes, string fileName)
    {
        if (replayBytes.IsEmpty)
        {
            throw new ReplayCorruptException("The selected replay file is empty.");
        }

        if (replayBytes.Length > MaximumReplayBytes)
        {
            throw new ReplayUnsupportedException(
                $"The selected replay file is larger than the {MaximumReplayBytes / (1024 * 1024)} MB safety limit.");
        }

        if (string.IsNullOrWhiteSpace(fileName)
            || !fileName.EndsWith(".osr", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplayUnsupportedException("Only stable .osr replay files are supported.");
        }

        _artifactHandle = _artifactStore.Create(replayBytes.ToArray(), fileName.Trim());
        SelectedFileName = fileName.Trim();
    }

    /// <summary>
    /// Captures the current replay artifact and beatmap identity for one
    /// explicit execution. Capturing the artifact id prevents a later import
    /// from changing an already-running request's input.
    /// </summary>
    public ReplayAnalysisRequest CreateRequest(
        TosuBeatmapSnapshot beatmap,
        long beatmapGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(beatmap);

        if (beatmapGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beatmapGeneration));
        }

        return new ReplayAnalysisRequest(
            new ReplayRequestId(Interlocked.Increment(ref _nextRequestId)),
            beatmap,
            beatmapGeneration,
            _artifactHandle?.ArtifactId);
    }

    /// <summary>
    /// Compatibility overload for callers that do not need to observe the
    /// request identity. Production UI code should create a request first so
    /// start/completion/failure events can carry the same id.
    /// </summary>
    public Task<AnalysisResult> AnalyzeAsync(
        TosuBeatmapSnapshot beatmap,
        CancellationToken cancellationToken = default) =>
        AnalyzeAsync(CreateRequest(beatmap), cancellationToken);

    public async Task<AnalysisResult> AnalyzeAsync(
        ReplayAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.RequestId.IsValid)
        {
            throw new ArgumentException("A valid replay request id is required.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.ArtifactId))
        {
            return AnalysisResult.Failure(
                CreateEngineRequest(request.Beatmap, artifactId: null),
                _engine.Descriptor,
                new AnalysisDiagnostic(
                    AnalysisDiagnosticSeverity.Error,
                    "replay.not_found",
                    "No replay file has been selected."));
        }

        var engineRequest = CreateEngineRequest(request.Beatmap, request.ArtifactId);
        // Parsing, decompression, judging, and metric calculation are CPU-bound;
        // keep the Avalonia UI thread free while the explicit replay is analyzed.
        return await Task.Run(
            () => _engine.AnalyzeAsync(engineRequest, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static AnalysisRequest CreateEngineRequest(TosuBeatmapSnapshot beatmap, string? artifactId)
    {
        var options = new Dictionary<string, JsonElement>();
        if (!string.IsNullOrWhiteSpace(artifactId))
        {
            options["replayArtifactId"] = JsonSerializer.SerializeToElement(artifactId);
        }

        return new AnalysisRequest(
            "replay.analysis",
            beatmap.Identity,
            beatmap.RawBeatmap,
            new AnalysisConfiguration("replay.rice", "1.0.0", options),
            "replay-post-play",
            beatmap.Rate,
            beatmap.Mods);
    }
}

/// <summary>
/// Immutable request envelope used by the replay service boundary. It keeps
/// the application request id and causal beatmap generation alongside the
/// captured beatmap/artifact without exposing replay bytes.
/// </summary>
public sealed record ReplayAnalysisRequest
{
    public ReplayAnalysisRequest(
        ReplayRequestId requestId,
        TosuBeatmapSnapshot beatmap,
        long beatmapGeneration,
        string? artifactId)
    {
        if (!requestId.IsValid)
        {
            throw new ArgumentException("A valid replay request id is required.", nameof(requestId));
        }

        ArgumentNullException.ThrowIfNull(beatmap);
        if (beatmapGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beatmapGeneration));
        }

        RequestId = requestId;
        Beatmap = beatmap;
        BeatmapGeneration = beatmapGeneration;
        ArtifactId = artifactId?.Trim();
    }

    public ReplayRequestId RequestId
    {
        get;
    }

    public TosuBeatmapSnapshot Beatmap
    {
        get;
    }

    public long BeatmapGeneration
    {
        get;
    }

    public string? ArtifactId
    {
        get;
    }

    public string BeatmapId => Beatmap.Identity.Id;

    public string BeatmapHash => Beatmap.Identity.Hash;
}
