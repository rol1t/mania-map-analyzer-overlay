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
    private readonly Func<AnalysisSnapshot, CancellationToken, Task> _acceptAsync;

    public RuntimeAnalysisSnapshotPresenter(Action<AnalysisSnapshot> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        _acceptAsync = (snapshot, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            accept(snapshot);
            return Task.CompletedTask;
        };
    }

    public RuntimeAnalysisSnapshotPresenter(Func<AnalysisSnapshot, CancellationToken, Task> acceptAsync)
    {
        _acceptAsync = acceptAsync ?? throw new ArgumentNullException(nameof(acceptAsync));
    }

    public Task PresentAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return _acceptAsync(snapshot, cancellationToken);
    }
}
