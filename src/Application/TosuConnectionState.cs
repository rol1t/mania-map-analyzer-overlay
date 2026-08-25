namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Lifecycle state of the Tosu transport. This is deliberately independent of
/// a particular HTTP/WebSocket implementation so the runtime can reject stale
/// reconnect callbacks before they reach presentation.
/// </summary>
public enum TosuConnectionState
{
    Unknown = 0,
    Starting = 1,
    Running = 2,
    Stopped = 3,
    Unavailable = 4,
    Failed = 5
}
