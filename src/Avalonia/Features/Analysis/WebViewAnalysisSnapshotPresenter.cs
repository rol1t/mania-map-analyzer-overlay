using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// WebView-backed presenter that pushes an <see cref="AnalysisSnapshot"/> to
/// the overlay renderer using the same JSON payload and script transport the
/// legacy MainWindow implementation used.
/// </summary>
public sealed class WebViewAnalysisSnapshotPresenter : IAnalysisSnapshotPresenter
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NativeWebView _webView;

    public WebViewAnalysisSnapshotPresenter(NativeWebView webView)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
    }

    public async Task PresentAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
        var script =
            $"window.dispatchEvent(new CustomEvent('analysis:snapshot', {{detail: {json}}})); " +
            $"if (typeof window._overlayRenderAnalysisSnapshot === 'function') window._overlayRenderAnalysisSnapshot({json});";

        await _webView.InvokeScript(script);
        AppLogger.Info(
            "Headless snapshot push",
            $"Pushed headless snapshot for beatmap {snapshot.Beatmap.Title} [{snapshot.Beatmap.Version}] to WebView.");
    }
}
