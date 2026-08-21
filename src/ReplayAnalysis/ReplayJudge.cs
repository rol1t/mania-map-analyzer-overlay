using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record ReplayJudgeOptions
{
    public string RulesetId { get; init; } = "mania";

    public string RulesetVersion { get; init; } = "1.0.0-rice";

    /// <summary>
    /// Judgement windows for this ruleset version. Defaults to
    /// <see cref="ReplayJudgementWindows.Default"/> (OD8 stable classic).
    /// Per-fixture overrides should be used for maps with a different OD
    /// to keep the fidelity gate reproducible.
    /// </summary>
    public ReplayJudgementWindows Windows { get; init; } = ReplayJudgementWindows.Default;

    public bool RejectLongNotes { get; init; } = true;
}

public sealed record ReplayJudgeResult
{
    public ReplayJudgeResult(
        ImmutableArray<JudgedHitEvent> judgedHits,
        ImmutableArray<ReplayDiagnostic> diagnostics,
        ReplayProvenance provenance)
    {
        JudgedHits = judgedHits;
        Diagnostics = diagnostics;
        Provenance = provenance;
    }

    public ImmutableArray<JudgedHitEvent> JudgedHits
    {
        get;
    }
    public ImmutableArray<ReplayDiagnostic> Diagnostics
    {
        get;
    }
    public ReplayProvenance Provenance
    {
        get;
    }
}

public static class ReplayJudge
{
    public static ReplayJudgeResult JudgeRice(
        OsuManiaBeatmap beatmap,
        IReadOnlyList<ReplayInputEvent> inputEvents,
        ReplayJudgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(inputEvents);
        options ??= new ReplayJudgeOptions();

        if (options.RejectLongNotes && beatmap.HasLongNotes)
        {
            var provenance = new ReplayProvenance(
                ReplaySourceKind.StableOsr,
                ReplayAnalysisFidelity.Unsupported,
                options.RulesetId,
                options.RulesetVersion,
                reason: "LN analysis is not enabled - map contains long notes.");

            var diagnostic = new ReplayDiagnostic(
                ReplayDiagnosticSeverity.Error,
                "replay.ln_not_supported",
                "Long-note analysis is not enabled for this phase. Only rice maps are supported.");

            return new ReplayJudgeResult([], [diagnostic], provenance);
        }

        IReadOnlyList<OsuManiaHitObject> riceNotes = beatmap.RiceNotes;
        IReadOnlyList<ReplayInputEvent> orderedInputs = ReplayInputOrdering.Order(
            inputEvents.Where(item => item.Kind == ReplayInputKind.Press).ToArray());

        List<JudgedHitEvent> judged = new(capacity: riceNotes.Count);
        List<ReplayDiagnostic> diagnostics = [];
        HashSet<int> matchedInputIndices = [];

        foreach (OsuManiaHitObject note in riceNotes)
        {
            int bestIndex = -1;
            int bestAbsOffset = int.MaxValue;
            List<int> tieIndices = [];

            for (int index = 0; index < orderedInputs.Count; index++)
            {
                if (matchedInputIndices.Contains(index))
                {
                    continue;
                }

                ReplayInputEvent input = orderedInputs[index];
                if (input.Column != note.Column)
                {
                    continue;
                }

                int offset = input.MapTimeMs - note.StartTimeMs;
                int absOffset = Math.Abs(offset);

                if (absOffset > options.Windows.MissMs)
                {
                    continue;
                }

                if (absOffset < bestAbsOffset)
                {
                    bestAbsOffset = absOffset;
                    bestIndex = index;
                    tieIndices.Clear();
                    tieIndices.Add(index);
                }
                else if (absOffset == bestAbsOffset)
                {
                    tieIndices.Add(index);
                }
            }

            if (tieIndices.Count > 1)
            {
                diagnostics.Add(new ReplayDiagnostic(
                    ReplayDiagnosticSeverity.Warning,
                    "replay.ambiguous_match",
                    $"Ambiguous press match for note '{note.Id}' at {note.StartTimeMs}ms col {note.Column}: {tieIndices.Count} equally close inputs (offset {bestAbsOffset}ms).",
                    properties: new Dictionary<string, string>
                    {
                        ["beatmapObjectId"] = note.Id,
                        ["column"] = note.Column.ToString(),
                        ["expectedTime"] = note.StartTimeMs.ToString(),
                        ["offsetMs"] = bestAbsOffset.ToString()
                    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)));
            }

            if (bestIndex >= 0)
            {
                ReplayInputEvent best = orderedInputs[bestIndex];
                matchedInputIndices.Add(bestIndex);
                int offset = best.MapTimeMs - note.StartTimeMs;
                ReplayJudgement judgement = options.Windows.Judge(Math.Abs(offset));

                judged.Add(new JudgedHitEvent(
                    beatmapObjectId: note.Id,
                    expectedMapTimeMs: note.StartTimeMs,
                    column: note.Column,
                    phase: ReplayHitPhase.Note,
                    judgement: judgement,
                    confidence: 1.0,
                    sourceSequence: best.SourceSequence,
                    observedMapTimeMs: best.MapTimeMs,
                    offsetMs: offset,
                    sourcePrecision: best.SourcePrecision));
            }
            else
            {
                judged.Add(new JudgedHitEvent(
                    beatmapObjectId: note.Id,
                    expectedMapTimeMs: note.StartTimeMs,
                    column: note.Column,
                    phase: ReplayHitPhase.Note,
                    judgement: ReplayJudgement.Miss,
                    confidence: 1.0,
                    sourceSequence: -1));
            }
        }

        int unmatchedPresses = orderedInputs.Count - matchedInputIndices.Count;
        if (unmatchedPresses > 0)
        {
            diagnostics.Add(new ReplayDiagnostic(
                ReplayDiagnosticSeverity.Information,
                "replay.unmatched_input",
                $"{unmatchedPresses} press inputs did not match any rice note (within ±{options.Windows.MissMs}ms, same column).",
                properties: new Dictionary<string, string>
                {
                    ["unmatchedPresses"] = unmatchedPresses.ToString(),
                    ["totalPresses"] = orderedInputs.Count.ToString()
                }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)));
        }

        // Preserve beatmap order for judged hits (not input order) so chords share expected time.
        bool hasWarnings = diagnostics.Any(item => item.Severity is ReplayDiagnosticSeverity.Warning or ReplayDiagnosticSeverity.Error);

        ReplayProvenance finalProvenance = hasWarnings
            ? new ReplayProvenance(ReplaySourceKind.StableOsr, ReplayAnalysisFidelity.Partial, options.RulesetId, options.RulesetVersion, reason: "Ambiguous or unmatched inputs detected.")
            : ReplayProvenance.ExactStable(options.RulesetId, options.RulesetVersion);

        return new ReplayJudgeResult(judged.ToImmutableArray(), diagnostics.ToImmutableArray(), finalProvenance);
    }
}
