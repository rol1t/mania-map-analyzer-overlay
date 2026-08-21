using System.Collections.Immutable;
using System.Text.Json;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record ReplayAnalysisSnapshot
{
    public ReplayAnalysisSnapshot(
        string replayArtifactId,
        string beatmapHash,
        ReplayProvenance provenance,
        IEnumerable<ReplayDiagnostic>? diagnostics = null,
        IEnumerable<KeyValuePair<string, JsonElement>>? metrics = null,
        IEnumerable<JudgedHitEvent>? judgedHits = null,
        IEnumerable<ReplayInputEvent>? inputEvents = null)
    {
        if (string.IsNullOrWhiteSpace(replayArtifactId))
        {
            throw new ArgumentException("A replay artifact id is required.", nameof(replayArtifactId));
        }

        if (string.IsNullOrWhiteSpace(beatmapHash))
        {
            throw new ArgumentException("A beatmap hash is required.", nameof(beatmapHash));
        }

        ArgumentNullException.ThrowIfNull(provenance);

        ReplayArtifactId = replayArtifactId.Trim();
        BeatmapHash = beatmapHash.Trim();
        Provenance = provenance;
        Diagnostics = (diagnostics ?? Array.Empty<ReplayDiagnostic>()).ToImmutableArray();
        Metrics = (metrics ?? Array.Empty<KeyValuePair<string, JsonElement>>())
            .ToImmutableDictionary(
                pair => pair.Key.Trim(),
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        JudgedHits = (judgedHits ?? Array.Empty<JudgedHitEvent>()).ToImmutableArray();
        InputEvents = (inputEvents ?? Array.Empty<ReplayInputEvent>()).ToImmutableArray();
    }

    public string ReplayArtifactId
    {
        get;
    }

    public string BeatmapHash
    {
        get;
    }

    public ReplayProvenance Provenance
    {
        get;
    }

    public ImmutableArray<ReplayDiagnostic> Diagnostics
    {
        get;
    }

    public ImmutableDictionary<string, JsonElement> Metrics
    {
        get;
    }

    public ImmutableArray<JudgedHitEvent> JudgedHits
    {
        get;
    }

    public ImmutableArray<ReplayInputEvent> InputEvents
    {
        get;
    }

    public bool HasErrors => Diagnostics.Any(item => item.Severity == ReplayDiagnosticSeverity.Error);
}
