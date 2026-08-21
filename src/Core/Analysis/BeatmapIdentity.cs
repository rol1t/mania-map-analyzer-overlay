namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Stable identity for the beatmap supplied to an analyzer. The content hash
/// is intentionally also included in the execution key by <see cref="AnalysisRequest"/>
/// so a changed file cannot reuse a previous result.
/// </summary>
public sealed record BeatmapIdentity
{
    public BeatmapIdentity(string id, string hash, string? setId = null)
    {
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(hash))
        {
            throw new ArgumentException("A beatmap id or hash is required.");
        }

        Id = id?.Trim() ?? string.Empty;
        Hash = hash?.Trim() ?? string.Empty;
        SetId = setId?.Trim() ?? string.Empty;
    }

    public string Id
    {
        get;
    }

    public string Hash
    {
        get;
    }

    public string SetId
    {
        get;
    }

    public string StableKey => string.Join(
        "|",
        Id.ToUpperInvariant(),
        Hash.ToUpperInvariant(),
        SetId.ToUpperInvariant());
}
