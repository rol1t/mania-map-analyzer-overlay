using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Boundary used by <see cref="HeadlessAnalysisController"/> to push a domain
/// analysis snapshot to the renderer. Implementations own transport details
/// such as JSON serialization and the WebView script invocation protocol.
/// </summary>
public interface IAnalysisSnapshotPresenter
{
    Task PresentAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Presents a snapshot belonging to a versioned headless request. Legacy
    /// presenters can use the compatibility overload until their transport is
    /// migrated; the runtime presenter overrides it to preserve the request
    /// identity at the application boundary.
    /// </summary>
    Task PresentAsync(
        AnalysisSnapshot snapshot,
        ManiaMapAnalyzerOverlay.Application.AnalysisRequestId requestId,
        CancellationToken cancellationToken = default) =>
        PresentAsync(snapshot, cancellationToken);

    Task PresentAsync(
        AnalysisSnapshot snapshot,
        ManiaMapAnalyzerOverlay.Application.AnalysisRequestId requestId,
        string configurationIdentity,
        CancellationToken cancellationToken = default) =>
        PresentAsync(snapshot, requestId, cancellationToken);
}
