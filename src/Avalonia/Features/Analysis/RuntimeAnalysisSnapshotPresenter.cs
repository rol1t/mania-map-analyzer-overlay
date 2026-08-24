using System;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Keeps headless analysis presentation inside the application runtime event
/// path. It intentionally does not know about WebView or navigation.
/// </summary>
public sealed class RuntimeAnalysisSnapshotPresenter : IAnalysisSnapshotPresenter
{
    private readonly Action<AnalysisSnapshot> _accept;

    public RuntimeAnalysisSnapshotPresenter(Action<AnalysisSnapshot> accept)
    {
        _accept = accept ?? throw new ArgumentNullException(nameof(accept));
    }

    public Task PresentAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        _accept(snapshot);
        return Task.CompletedTask;
    }
}
