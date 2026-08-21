using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record OsuManiaBeatmap
{
    public OsuManiaBeatmap(
        string beatmapHash,
        int keyCount,
        IEnumerable<OsuManiaHitObject> hitObjects)
    {
        if (string.IsNullOrWhiteSpace(beatmapHash))
        {
            throw new ArgumentException("A beatmap hash is required.", nameof(beatmapHash));
        }

        if (keyCount is < 1 or > 18)
        {
            throw new ArgumentOutOfRangeException(nameof(keyCount), "Key count must be within [1, 18].");
        }

        ArgumentNullException.ThrowIfNull(hitObjects);

        BeatmapHash = beatmapHash.Trim();
        KeyCount = keyCount;
        HitObjects = hitObjects.OrderBy(item => item.StartTimeMs).ThenBy(item => item.Column).ToImmutableArray();
        HasLongNotes = HitObjects.Any(item => item.IsLongNote);
    }

    public string BeatmapHash
    {
        get;
    }

    public int KeyCount
    {
        get;
    }

    public ImmutableArray<OsuManiaHitObject> HitObjects
    {
        get;
    }

    public bool HasLongNotes
    {
        get;
    }

    public IReadOnlyList<OsuManiaHitObject> RiceNotes => HitObjects.Where(item => !item.IsLongNote).ToArray();
}
