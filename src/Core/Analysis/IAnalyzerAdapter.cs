namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Converts data emitted by a concrete analyzer integration into the stable
/// application contract. UI and domain services depend on this abstraction,
/// never on a widget DOM or transport-specific payload.
/// </summary>
public interface IAnalyzerAdapter
{
    AnalyzerDescriptor Descriptor
    {
        get;
    }
    bool TryNormalize(string payload, out AnalysisSnapshot? snapshot);
}
