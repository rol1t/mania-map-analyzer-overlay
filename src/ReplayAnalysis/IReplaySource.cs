namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public interface IReplaySource
{
    ReplaySourceKind Kind
    {
        get;
    }

    Task<IReadOnlyList<ReplayInputEvent>> ReadInputEventsAsync(
        ReplayArtifact artifact,
        CancellationToken cancellationToken = default);
}

public interface IReplayBeatmapIdentity
{
    string BeatmapHash
    {
        get;
    }

    int KeyCount
    {
        get;
    }
}

public interface IReplayBeatmapProvider
{
    Task<IReplayBeatmapIdentity> LoadAsync(
        string beatmapPath,
        CancellationToken cancellationToken = default);
}
