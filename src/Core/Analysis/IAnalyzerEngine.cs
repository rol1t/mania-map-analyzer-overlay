namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Executes an analyzer without exposing a widget DOM, renderer, or platform
/// implementation to the application domain.
/// </summary>
public interface IAnalyzerEngine
{
    AnalyzerEngineDescriptor Descriptor
    {
        get;
    }

    Task<AnalysisResult> AnalyzeAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default);
}
