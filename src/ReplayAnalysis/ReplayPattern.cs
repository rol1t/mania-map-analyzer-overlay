using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public enum ReplayPatternKind
{
    Single = 0,
    Jack = 1,
    Minijack = 2,
    Chord = 3,
    Stream = 4,
    Jump = 5
}

/// <summary>
/// Weighted pattern memberships for one beatmap object; a note may belong to
/// multiple patterns with weights that sum to ≤1. No forced single label.
/// </summary>
public sealed record ReplayPatternMembership
{
    public ReplayPatternMembership(string beatmapObjectId, IReadOnlyDictionary<ReplayPatternKind, double> weights)
    {
        if (string.IsNullOrWhiteSpace(beatmapObjectId))
        {
            throw new ArgumentException("A beatmap object id is required.", nameof(beatmapObjectId));
        }

        ArgumentNullException.ThrowIfNull(weights);
        double sum = weights.Values.Sum();
        if (sum > 1.0001)
        {
            throw new ArgumentException($"Pattern weights must sum to ≤1, but was {sum}.", nameof(weights));
        }

        if (weights.Any(pair => pair.Value is < 0 or > 1))
        {
            throw new ArgumentException("Pattern weights must be within [0, 1].", nameof(weights));
        }

        BeatmapObjectId = beatmapObjectId.Trim();
        Weights = weights.ToImmutableDictionary(pair => pair.Key, pair => pair.Value);
    }

    public string BeatmapObjectId
    {
        get;
    }
    public ImmutableDictionary<ReplayPatternKind, double> Weights
    {
        get;
    }

    public bool HasPattern(ReplayPatternKind kind, double threshold = 0.1) =>
        Weights.TryGetValue(kind, out double weight) && weight >= threshold;
}

public static class ReplayPatternClassifier
{
    /// <summary>
    /// Lightweight rule-based classifier for rice maps (no ManiaMapAnalyser dependency
    /// required). Real strain values can be supplied via <see cref="ReplayPatternCorrelation.WithExternalDifficulty"/>.
    /// </summary>
    public static IReadOnlyList<ReplayPatternMembership> Classify(OsuManiaBeatmap beatmap)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        OsuManiaHitObject[] notes = beatmap.HitObjects.Where(item => !item.IsLongNote).OrderBy(item => item.StartTimeMs).ThenBy(item => item.Column).ToArray();
        List<ReplayPatternMembership> memberships = new(capacity: notes.Length);

        for (int index = 0; index < notes.Length; index++)
        {
            OsuManiaHitObject note = notes[index];
            Dictionary<ReplayPatternKind, double> weights = new();

            // Chord: ≥2 notes at same StartTimeMs.
            int chordSize = notes.Count(item => item.StartTimeMs == note.StartTimeMs);
            if (chordSize >= 2)
            {
                weights[ReplayPatternKind.Chord] = chordSize == 2 ? 0.7 : 1.0;
                if (chordSize == 2)
                {
                    weights[ReplayPatternKind.Jump] = 0.3;
                }
            }

            // Jack / minijack: same column within 120ms.
            OsuManiaHitObject? previousSameColumn = notes.Take(index).LastOrDefault(item => item.Column == note.Column);
            if (previousSameColumn is not null)
            {
                int delta = note.StartTimeMs - previousSameColumn.StartTimeMs;
                if (delta is > 0 and <= 50)
                {
                    weights[ReplayPatternKind.Minijack] = 0.9;
                }
                else if (delta is > 50 and <= 120)
                {
                    weights[ReplayPatternKind.Jack] = 0.8;
                }
            }

            // Stream: ≥3 notes within 400ms window with alternating columns.
            int streamCount = notes.Count(item => Math.Abs(item.StartTimeMs - note.StartTimeMs) <= 200);
            if (streamCount >= 3 && chordSize == 1 && !weights.ContainsKey(ReplayPatternKind.Minijack) && !weights.ContainsKey(ReplayPatternKind.Jack))
            {
                // Simple density heuristic.
                weights[ReplayPatternKind.Stream] = Math.Min(1.0, (streamCount - 2) * 0.3);
            }

            if (weights.Count == 0)
            {
                weights[ReplayPatternKind.Single] = 1.0;
            }

            // Normalize if sum >1 (chord+minijack overlap).
            double sum = weights.Values.Sum();
            if (sum > 1)
            {
                foreach (ReplayPatternKind key in weights.Keys.ToArray())
                {
                    weights[key] /= sum;
                }
            }

            memberships.Add(new ReplayPatternMembership(note.Id, weights));
        }

        return memberships;
    }
}
