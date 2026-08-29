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
    private readonly Func<
        AnalysisSnapshot,
        ManiaMapAnalyzerOverlay.Application.AnalysisRequestId?,
        string?,
        CancellationToken,
        Task> _acceptAsync;

    public RuntimeAnalysisSnapshotPresenter(Action<AnalysisSnapshot> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        _acceptAsync = (snapshot, _, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            accept(snapshot);
            return Task.CompletedTask;
        };
    }

    public RuntimeAnalysisSnapshotPresenter(Func<AnalysisSnapshot, CancellationToken, Task> acceptAsync)
    {
        ArgumentNullException.ThrowIfNull(acceptAsync);
        _acceptAsync = (snapshot, _, _, cancellationToken) => acceptAsync(snapshot, cancellationToken);
    }

    public RuntimeAnalysisSnapshotPresenter(
        Func<AnalysisSnapshot, ManiaMapAnalyzerOverlay.Application.AnalysisRequestId?, CancellationToken, Task> acceptAsync)
    {
        ArgumentNullException.ThrowIfNull(acceptAsync);
        _acceptAsync = (snapshot, requestId, _, cancellationToken) =>
            acceptAsync(snapshot, requestId, cancellationToken);
    }

    public RuntimeAnalysisSnapshotPresenter(
        Func<
            AnalysisSnapshot,
            ManiaMapAnalyzerOverlay.Application.AnalysisRequestId?,
            string?,
            CancellationToken,
            Task> acceptAsync)
    {
        _acceptAsync = acceptAsync ?? throw new ArgumentNullException(nameof(acceptAsync));
    }

    public Task PresentAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
        => PresentAsync(snapshot, requestId: null, configurationIdentity: null, cancellationToken);

    public Task PresentAsync(
        AnalysisSnapshot snapshot,
        ManiaMapAnalyzerOverlay.Application.AnalysisRequestId requestId,
        CancellationToken cancellationToken = default)
        => PresentAsync(
            snapshot,
            (ManiaMapAnalyzerOverlay.Application.AnalysisRequestId?)requestId,
            configurationIdentity: null,
            cancellationToken);

    public Task PresentAsync(
        AnalysisSnapshot snapshot,
        ManiaMapAnalyzerOverlay.Application.AnalysisRequestId requestId,
        string configurationIdentity,
        CancellationToken cancellationToken = default)
        => PresentAsync(
            snapshot,
            (ManiaMapAnalyzerOverlay.Application.AnalysisRequestId?)requestId,
            configurationIdentity,
            cancellationToken);

    private Task PresentAsync(
        AnalysisSnapshot snapshot,
        ManiaMapAnalyzerOverlay.Application.AnalysisRequestId? requestId,
        string? configurationIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return _acceptAsync(snapshot, requestId, configurationIdentity, cancellationToken);
    }
}
