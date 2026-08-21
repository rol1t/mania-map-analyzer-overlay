namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public enum ReplaySourceKind
{
    StableOsr = 0,
    LazerReplay = 1,
    ProvisionalLive = 2
}

public enum ReplayAnalysisFidelity
{
    Exact = 0,
    Provisional = 1,
    Partial = 2,
    Unsupported = 3
}

public enum ReplayInputKind
{
    Press = 0,
    Release = 1
}

public enum ReplayHitPhase
{
    Note = 0,
    LnHead = 1,
    LnTail = 2
}

public enum ReplayJudgement
{
    Perfect = 0,
    Great = 1,
    Good = 2,
    Ok = 3,
    Meh = 4,
    Miss = 5
}
