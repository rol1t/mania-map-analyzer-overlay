using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public enum LnScoringPolicy
{
    Legacy = 0,
    ScoreV2 = 1
}

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

    /// <summary>
    /// LN scoring policy for Phase 5. Legacy uses separate head/release offsets;
    /// ScoreV2 is stubbed for future lazer parity and currently behaves like Legacy
    /// but carries a distinct version in provenance.
    /// </summary>
    public LnScoringPolicy LnPolicy { get; init; } = LnScoringPolicy.Legacy;
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
    public static ReplayJudgeResult Judge(
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

        if (beatmap.HasLongNotes)
        {
            return JudgeWithLongNotes(beatmap, inputEvents, options);
        }

        return JudgeRice(beatmap, inputEvents, options);
    }

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

    private static ReplayJudgeResult JudgeWithLongNotes(
        OsuManiaBeatmap beatmap,
        IReadOnlyList<ReplayInputEvent> inputEvents,
        ReplayJudgeOptions options)
    {
        IReadOnlyList<ReplayInputEvent> orderedPresses = ReplayInputOrdering.Order(
            inputEvents.Where(item => item.Kind == ReplayInputKind.Press).ToArray());
        IReadOnlyList<ReplayInputEvent> orderedReleases = ReplayInputOrdering.Order(
            inputEvents.Where(item => item.Kind == ReplayInputKind.Release).ToArray());

        List<JudgedHitEvent> judged = [];
        List<ReplayDiagnostic> diagnostics = [];
        HashSet<int> matchedPressIndices = [];
        HashSet<int> matchedReleaseIndices = [];

        foreach (OsuManiaHitObject note in beatmap.HitObjects)
        {
            if (!note.IsLongNote)
            {
                // Rice path reused for head-only.
                JudgedHitEvent rice = JudgeSingleNote(note, note.StartTimeMs, ReplayHitPhase.Note, orderedPresses, matchedPressIndices, diagnostics, options);
                judged.Add(rice);
                continue;
            }

            // LN head (press) and tail (release) analysed separately; report dropped holds distinctly.
            JudgedHitEvent head = JudgeSingleNote(note, note.StartTimeMs, ReplayHitPhase.LnHead, orderedPresses, matchedPressIndices, diagnostics, options, objectIdSuffix: "-head");
            judged.Add(head);

            int tailTime = note.EndTimeMs!.Value;
            JudgedHitEvent tail = JudgeSingleNote(note, tailTime, ReplayHitPhase.LnTail, orderedReleases, matchedReleaseIndices, diagnostics, options, objectIdSuffix: "-tail");

            // Dropped hold: head was hit but tail missed (release outside window or missing).
            if (head.Judgement != ReplayJudgement.Miss && tail.Judgement == ReplayJudgement.Miss)
            {
                diagnostics.Add(new ReplayDiagnostic(
                    ReplayDiagnosticSeverity.Information,
                    "replay.ln_dropped",
                    $"Dropped hold for '{note.Id}' col {note.Column}: head hit at {head.ObservedMapTimeMs}ms but tail missed at {tailTime}ms.",
                    properties: new Dictionary<string, string>
                    {
                        ["beatmapObjectId"] = note.Id,
                        ["column"] = note.Column.ToString(),
                        ["headOffsetMs"] = (head.OffsetMs?.ToString() ?? "null"),
                        ["tailExpectedTime"] = tailTime.ToString()
                    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)));
            }

            judged.Add(tail);
        }

        int unmatchedPresses = orderedPresses.Count - matchedPressIndices.Count;
        int unmatchedReleases = orderedReleases.Count - matchedReleaseIndices.Count;
        if (unmatchedPresses > 0 || unmatchedReleases > 0)
        {
            diagnostics.Add(new ReplayDiagnostic(
                ReplayDiagnosticSeverity.Information,
                "replay.unmatched_input",
                $"{unmatchedPresses} press / {unmatchedReleases} release inputs unmatched (within ±{options.Windows.MissMs}ms).",
                properties: new Dictionary<string, string>
                {
                    ["unmatchedPresses"] = unmatchedPresses.ToString(),
                    ["unmatchedReleases"] = unmatchedReleases.ToString()
                }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)));
        }

        bool hasWarnings = diagnostics.Any(item => item.Severity is ReplayDiagnosticSeverity.Warning or ReplayDiagnosticSeverity.Error);
        string version = options.LnPolicy == LnScoringPolicy.ScoreV2 ? options.RulesetVersion + "+scorev2" : options.RulesetVersion;
        ReplayProvenance provenance = hasWarnings
            ? new ReplayProvenance(ReplaySourceKind.StableOsr, ReplayAnalysisFidelity.Partial, options.RulesetId, version, reason: "LN holds with unmatched/dropped inputs.")
            : new ReplayProvenance(ReplaySourceKind.StableOsr, ReplayAnalysisFidelity.Exact, options.RulesetId, version);

        return new ReplayJudgeResult(judged.ToImmutableArray(), diagnostics.ToImmutableArray(), provenance);
    }

    private static JudgedHitEvent JudgeSingleNote(
        OsuManiaHitObject note,
        int expectedTimeMs,
        ReplayHitPhase phase,
        IReadOnlyList<ReplayInputEvent> orderedInputs,
        HashSet<int> matchedIndices,
        List<ReplayDiagnostic> diagnostics,
        ReplayJudgeOptions options,
        string objectIdSuffix = "")
    {
        int bestIndex = -1;
        int bestAbsOffset = int.MaxValue;
        List<int> tieIndices = [];

        for (int index = 0; index < orderedInputs.Count; index++)
        {
            if (matchedIndices.Contains(index))
            {
                continue;
            }

            ReplayInputEvent input = orderedInputs[index];
            if (input.Column != note.Column)
            {
                continue;
            }

            int offset = input.MapTimeMs - expectedTimeMs;
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
                $"Ambiguous {phase} match for note '{note.Id}' at {expectedTimeMs}ms col {note.Column}: {tieIndices.Count} inputs (offset {bestAbsOffset}ms).",
                properties: new Dictionary<string, string>
                {
                    ["beatmapObjectId"] = note.Id + objectIdSuffix,
                    ["column"] = note.Column.ToString(),
                    ["expectedTime"] = expectedTimeMs.ToString(),
                    ["phase"] = phase.ToString()
                }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)));
        }

        if (bestIndex >= 0)
        {
            ReplayInputEvent best = orderedInputs[bestIndex];
            matchedIndices.Add(bestIndex);
            int offset = best.MapTimeMs - expectedTimeMs;
            ReplayJudgement judgement = options.Windows.Judge(Math.Abs(offset));

            // Preserve both map and audio time: AudioTimeMs/Rate are carried on ReplayInputEvent
            // and already separated from MapTimeMs (non-negotiable contract). No scroll-velocity correction.
            return new JudgedHitEvent(
                beatmapObjectId: note.Id + objectIdSuffix,
                expectedMapTimeMs: expectedTimeMs,
                column: note.Column,
                phase: phase,
                judgement: judgement,
                confidence: 1.0,
                sourceSequence: best.SourceSequence,
                observedMapTimeMs: best.MapTimeMs,
                offsetMs: offset,
                sourcePrecision: best.SourcePrecision);
        }

        return new JudgedHitEvent(
            beatmapObjectId: note.Id + objectIdSuffix,
            expectedMapTimeMs: expectedTimeMs,
            column: note.Column,
            phase: phase,
            judgement: ReplayJudgement.Miss,
            confidence: 1.0,
            sourceSequence: -1);
    }
}
