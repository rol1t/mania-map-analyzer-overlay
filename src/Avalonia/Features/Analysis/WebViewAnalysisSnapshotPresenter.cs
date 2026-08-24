using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
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
    private readonly Func<NativeWebView> _webViewProvider;

    public WebViewAnalysisSnapshotPresenter(NativeWebView webView)
        : this(CreateProvider(webView))
    {
    }

    public WebViewAnalysisSnapshotPresenter(Func<NativeWebView> webViewProvider)
    {
        _webViewProvider = webViewProvider ?? throw new ArgumentNullException(nameof(webViewProvider));
    }

    private static Func<NativeWebView> CreateProvider(NativeWebView webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        return () => webView;
    }

    public async Task PresentAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
        // The renderer subscribes to analysis:snapshot. Do not also invoke
        // __overlayRenderAnalysisSnapshot directly: doing both renders every
        // headless frame twice and visibly flickers the Pause Coach DOM.
        var script = $"window.dispatchEvent(new CustomEvent('analysis:snapshot', {{detail: {json}}}));";

        if (Dispatcher.UIThread.CheckAccess())
        {
            await InvokeOnUiAsync(_webViewProvider(), script, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    // The launcher replaces the NativeWebView when leaving
                    // overlay mode. Resolve it on the UI thread so headless
                    // analysis never keeps invoking the detached overlay
                    // control after osu! exits.
                    await InvokeOnUiAsync(_webViewProvider(), script, cancellationToken).ConfigureAwait(false);
                    completion.TrySetResult(null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                await completion.Task.ConfigureAwait(false);
            }
        }
        AppLogger.Info(
            "Headless snapshot push",
            $"Pushed headless snapshot for beatmap {snapshot.Beatmap.Title} [{snapshot.Beatmap.Version}] to WebView.");
    }

    private static async Task InvokeOnUiAsync(NativeWebView webView, string script, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await webView.InvokeScript(script).ConfigureAwait(false);
    }
}
