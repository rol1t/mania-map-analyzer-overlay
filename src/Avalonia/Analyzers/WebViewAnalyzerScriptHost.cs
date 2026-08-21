using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

public sealed class WebViewAnalyzerScriptHost : IAnalyzerScriptHost
{
    private readonly NativeWebView _webView;
    private readonly IAnalyzerEngineDiagnosticSink _diagnosticSink;
    private bool _disposed;

    public WebViewAnalyzerScriptHost(
        NativeWebView webView,
        IAnalyzerEngineDiagnosticSink? diagnosticSink = null)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _diagnosticSink = diagnosticSink ?? new AppLoggerAnalyzerEngineDiagnosticSink();
        _webView.WebMessageReceived += WebView_WebMessageReceived;
    }

    public event EventHandler<AnalyzerScriptMessageEventArgs>? MessageReceived;

    public Task<string?> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        return ExecuteCoreAsync(script, cancellationToken);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    public void Publish(string body, string? source = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RaiseMessage(body, source);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _webView.WebMessageReceived -= WebView_WebMessageReceived;
        MessageReceived = null;
        await Task.CompletedTask;
        GC.SuppressFinalize(this);
    }

    private async Task<string?> ExecuteCoreAsync(string script, CancellationToken cancellationToken)
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        var result = await ExecuteOnUiAsync(script, cancellationToken).ConfigureAwait(false);
                        completion.TrySetResult(result);
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
                    return await completion.Task.ConfigureAwait(false);
                }
            }

            return await ExecuteOnUiAsync(script, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var diagnostic = new AnalyzerEngineDiagnostic(
                "engine.script_invoke_failed",
                "The analyzer script could not be executed in the WebView.",
                null,
                AnalyzerEngineDiagnosticSeverity.Error,
                exception);
            _diagnosticSink.Report("Executing analyzer script", diagnostic, exception);
            throw;
        }
    }

    private async Task<string?> ExecuteOnUiAsync(string script, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await _webView.InvokeScript(script).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var diagnostic = new AnalyzerEngineDiagnostic(
                "engine.script_invoke_failed",
                "The analyzer WebView could not execute a script.",
                null,
                AnalyzerEngineDiagnosticSeverity.Error,
                exception);
            _diagnosticSink.Report("Executing analyzer script", diagnostic, exception);
            throw;
        }
    }

    private void WebView_WebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.Body))
            {
                return;
            }

            RaiseMessage(e.Body);
        }
        catch (Exception exception)
        {
            var diagnostic = new AnalyzerEngineDiagnostic(
                "engine.message_forward_failed",
                "The analyzer host could not forward a WebView message.",
                null,
                AnalyzerEngineDiagnosticSeverity.Warning,
                exception);
            _diagnosticSink.Report("Forwarding analyzer WebView message", diagnostic, exception);
        }
    }

    private void RaiseMessage(string body, string? source = null)
    {
        var handlers = MessageReceived;
        if (handlers is null)
        {
            return;
        }

        var args = new AnalyzerScriptMessageEventArgs(body, source);
        foreach (EventHandler<AnalyzerScriptMessageEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                var diagnostic = new AnalyzerEngineDiagnostic(
                    "engine.message_handler_failed",
                    "An analyzer message handler threw an exception.",
                    null,
                    AnalyzerEngineDiagnosticSeverity.Warning,
                    exception);
                _diagnosticSink.Report("Delivering analyzer WebView message", diagnostic, exception);
            }
        }
    }
}
