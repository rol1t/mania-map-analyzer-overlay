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
}
