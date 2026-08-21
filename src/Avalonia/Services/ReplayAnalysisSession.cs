using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    public async Task<AnalysisResult> AnalyzeAsync(
        TosuBeatmapSnapshot beatmap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        cancellationToken.ThrowIfCancellationRequested();

        if (_artifactHandle is null)
        {
            return AnalysisResult.Failure(
                CreateRequest(beatmap, artifactId: null),
                _engine.Descriptor,
                new AnalysisDiagnostic(
                    AnalysisDiagnosticSeverity.Error,
                    "replay.not_found",
                    "No replay file has been selected."));
        }

        var request = CreateRequest(beatmap, _artifactHandle.ArtifactId);
        // Parsing, decompression, judging, and metric calculation are CPU-bound;
        // keep the Avalonia UI thread free while the explicit replay is analyzed.
        return await Task.Run(
            () => _engine.AnalyzeAsync(request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static AnalysisRequest CreateRequest(TosuBeatmapSnapshot beatmap, string? artifactId)
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
