using System;
using System.Threading;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

/// <summary>
/// Minimal browser/script boundary used by analyzer engines. The bridge does
/// not depend on a concrete WebView control and therefore remains testable on
/// every supported platform.
/// </summary>
public interface IAnalyzerScriptHost : IAsyncDisposable
{
    event EventHandler<AnalyzerScriptMessageEventArgs>? MessageReceived;

    Task<string?> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default);

    /// <summary>
    /// Injects a script into the current document. Hosts that expose only one
    /// script invocation primitive can use the default implementation.
    /// </summary>
    Task InjectScriptAsync(string script, CancellationToken cancellationToken = default) =>
        ExecuteScriptAsync(script, cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class AnalyzerScriptMessageEventArgs : EventArgs
{
    public AnalyzerScriptMessageEventArgs(string body, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("A script-host message body is required.", nameof(body));
        }

        Body = body;
        Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
    }

    public string Body
    {
        get;
    }

    public string? Source
    {
        get;
    }
}

/// <summary>
/// Delegate-backed host adapter for WebView integrations and tests. A native
/// WebView event handler can forward its body through <see cref="Publish"/>.
/// </summary>
public sealed class DelegateAnalyzerScriptHost : IAnalyzerScriptHost
{
    private readonly Func<string, CancellationToken, Task<string?>> _executeScript;
    private readonly Func<CancellationToken, Task> _reset;
    private readonly Action<Exception>? _reportSubscriberFailure;
    private bool _disposed;

    public DelegateAnalyzerScriptHost(
        Func<string, CancellationToken, Task<string?>> executeScript,
        Func<CancellationToken, Task>? reset = null,
        Action<Exception>? reportSubscriberFailure = null)
    {
        _executeScript = executeScript ?? throw new ArgumentNullException(nameof(executeScript));
        _reset = reset ?? ((_) => Task.CompletedTask);
        _reportSubscriberFailure = reportSubscriberFailure;
    }

    public event EventHandler<AnalyzerScriptMessageEventArgs>? MessageReceived;

    public Task<string?> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        return _executeScript(script, cancellationToken);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _reset(cancellationToken);
    }

    public void Publish(string body, string? source = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var args = new AnalyzerScriptMessageEventArgs(body, source);
        var handlers = MessageReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<AnalyzerScriptMessageEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                _reportSubscriberFailure?.Invoke(exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MessageReceived = null;
        GC.SuppressFinalize(this);
        await Task.CompletedTask;
    }
}
