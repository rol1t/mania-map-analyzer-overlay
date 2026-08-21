using System.Text.Json;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class ReplayJsonFixtureTests
{
    [Fact]
    public void ProvenanceAndJudgedEventsRoundTripViaJson()
    {
        var provenance = new ReplayProvenance(ReplaySourceKind.StableOsr, ReplayAnalysisFidelity.Exact, "mania", "1.0.0");

        var judged = new JudgedHitEvent(
            beatmapObjectId: "obj-10",
            expectedMapTimeMs: 1234,
            column: 2,
            phase: ReplayHitPhase.Note,
            judgement: ReplayJudgement.Great,
            confidence: 0.95,
            sourceSequence: 10,
            observedMapTimeMs: 1239,
            offsetMs: 5,
            sourcePrecision: "stable.osr.frames");

        string json = JsonSerializer.Serialize(new
        {
            SourceKind = provenance.SourceKind.ToString(),
            Fidelity = provenance.Fidelity.ToString(),
            provenance.RulesetId,
            judged.BeatmapObjectId,
            judged.ExpectedMapTimeMs,
            judged.ObservedMapTimeMs,
            judged.OffsetMs,
            judged.Column,
            Phase = judged.Phase.ToString(),
            Judgement = judged.Judgement.ToString()
        });

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("StableOsr", doc.RootElement.GetProperty("SourceKind").GetString());
        Assert.Equal("obj-10", doc.RootElement.GetProperty("BeatmapObjectId").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("OffsetMs").GetInt32());
    }

    [Fact]
    public void SnapshotMetricsKeepSemanticIdsWithoutLeakingBytes()
    {
        var provenance = ReplayProvenance.ExactStable("mania", "1.0.0");
        var snapshot = new ReplayAnalysisSnapshot(
            replayArtifactId: "art-1",
            beatmapHash: "hash-1",
            provenance: provenance,
            metrics: new Dictionary<string, JsonElement>
            {
                ["replay.timing.ur"] = JsonSerializer.SerializeToElement(42.5),
                ["replay.column.0.biasMs"] = JsonSerializer.SerializeToElement(-3.2)
            });

        string json = JsonSerializer.Serialize(snapshot.Metrics.ToDictionary(pair => pair.Key, pair => pair.Value.GetDouble()));
        Assert.Contains("replay.timing.ur", json);
        Assert.Contains("replay.column.0.biasMs", json);
        Assert.DoesNotContain("base64", json, StringComparison.OrdinalIgnoreCase);
    }
}
