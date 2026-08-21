using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

/// <summary>
/// Runs a validated DOM-free analyzer package inside a same-origin script
/// host. The host only transports JSON messages; all analyzer-specific module
/// paths remain in the package manifest.
/// </summary>
public sealed partial class AnalyzerEngineScriptBridge : IAnalyzerEngine, IAsyncDisposable
{
    public const string NativeMessagePrefix = "analyzer-engine:";
    public const string DefaultStaticRoot = "/ManiaMapAnalyzerOverlay/engines/";
    public const string DefaultUpstreamRoot = "/ManiaMapAnalyser/";

    private readonly AnalyzerEnginePackage _package;
    private readonly IAnalyzerScriptHost _scriptHost;
    private readonly IAnalyzerEngineDiagnosticSink _diagnosticSink;
    private readonly string _staticRoot;
    private readonly string _upstreamRoot;
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingAnalysis> _pending = new(StringComparer.Ordinal);
    private readonly string _registryKey;
    private readonly string _protocol;
    private readonly int _protocolVersion;
    private Task<AnalyzerEngineBridgeReady>? _initializationTask;
    private TaskCompletionSource<AnalyzerEngineBridgeReady>? _readySource;
    private string? _activeSessionId;
    private bool _disposed;

    public AnalyzerEngineScriptBridge(
        AnalyzerEnginePackage package,
        IAnalyzerScriptHost scriptHost,
        string? staticRoot = null,
        string? upstreamRoot = null,
        IAnalyzerEngineDiagnosticSink? diagnosticSink = null)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _scriptHost = scriptHost ?? throw new ArgumentNullException(nameof(scriptHost));
        _diagnosticSink = diagnosticSink ?? new AppLoggerAnalyzerEngineDiagnosticSink();
        _staticRoot = NormalizeRoot(staticRoot ?? DefaultStaticRoot);
        _upstreamRoot = NormalizeRoot(upstreamRoot ?? DefaultUpstreamRoot);

        if (!_package.IsAvailable || _package.Manifest is null || string.IsNullOrWhiteSpace(_package.Id))
        {
            throw new ArgumentException(
                "An available analyzer engine package with a manifest is required.",
                nameof(package));
        }

        _registryKey = _package.Id.Trim();
        _protocol = _package.Protocol ?? throw new ArgumentException(
            "The analyzer engine package does not declare a protocol.",
            nameof(package));
        _protocolVersion = _package.Manifest.ProtocolVersion;
        Descriptor = CreateDescriptor(_package.Manifest);
        _scriptHost.MessageReceived += ScriptHost_MessageReceived;
    }

    public AnalyzerEngineDescriptor Descriptor
    {
        get;
    }

    public AnalyzerEnginePackage Package => _package;

    public async Task<AnalysisResult> AnalyzeAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        if (!string.Equals(request.EngineId, Descriptor.Id, StringComparison.OrdinalIgnoreCase))
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "engine.request_engine_mismatch",
                $"Analyzer engine '{Descriptor.Id}' received a request for engine '{request.EngineId}'.");
            Report(diagnostic);
            return AnalysisResult.Failure(request, Descriptor, diagnostic);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var correlationId = CreateCorrelationId();
            var completion = new TaskCompletionSource<AnalysisResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingAnalysis(request, completion);
            lock (_sync)
            {
                ThrowIfDisposed();
                if (!_pending.TryAdd(correlationId, pending))
                {
                    throw new InvalidOperationException(
                        $"Generated duplicate analyzer correlation id '{correlationId}'.");
                }
            }

            using var cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var callback = (CancellationCallbackState)state!;
                    callback.Bridge.CancelPending(callback.CorrelationId);
                },
                new CancellationCallbackState(this, correlationId));

            try
            {
                await _scriptHost.InjectScriptAsync(
                    BuildAnalyzeScript(request, correlationId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RemovePending(correlationId);
                throw;
            }
            catch (Exception exception)
            {
                RemovePending(correlationId);
                var diagnostic = AnalysisDiagnostic.Error(
                    "engine.request_dispatch_failed",
                    $"Analyzer engine '{Descriptor.Id}' could not receive an analysis request.",
                    exception,
                    [
                        new KeyValuePair<string, string>("correlationId", correlationId),
                        new KeyValuePair<string, string>("requestKey", request.Key.Value)
                    ]);
                Report(diagnostic, exception);
                return AnalysisResult.Failure(request, Descriptor, diagnostic);
            }

            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AnalyzerEngineBridgeException exception)
        {
            var diagnostic = AnalysisDiagnostic.Error(
                exception.Diagnostic.Code,
                exception.Diagnostic.Message,
                exception,
                exception.Diagnostic.Properties);
            Report(diagnostic, exception);
            return AnalysisResult.Failure(request, Descriptor, diagnostic);
        }
        catch (Exception exception)
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "engine.analysis_bridge_failed",
                $"Analyzer engine '{Descriptor.Id}' failed before returning an analysis result.",
                exception,
                [new KeyValuePair<string, string>("requestKey", request.Key.Value)]);
            Report(diagnostic, exception);
            return AnalysisResult.Failure(request, Descriptor, diagnostic);
        }
    }

    /// <summary>
    /// Resets the page/runtime boundary, cancelling every correlation owned
    /// by this bridge. It is intended for WebView navigation and tosu restart.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        List<PendingAnalysis> pending;
        TaskCompletionSource<AnalyzerEngineBridgeReady>? readySource;
        lock (_sync)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
            _initializationTask = null;
            readySource = _readySource;
            _readySource = null;
            _activeSessionId = null;
        }

        var diagnostic = new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Information,
            "engine.runtime_reset",
            "The analyzer runtime was reset by the script host.");
        readySource?.TrySetException(new AnalyzerEngineBridgeException(diagnostic.Message, null, diagnostic));
        foreach (var entry in pending)
        {
            entry.Completion.TrySetResult(AnalysisResult.Cancelled(entry.Request, Descriptor, diagnostic));
        }

        try
        {
            await _scriptHost.InjectScriptAsync(BuildResetScript(), cancellationToken).ConfigureAwait(false);
            await _scriptHost.ResetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = AnalysisDiagnostic.Error(
                "engine.runtime_reset_failed",
                $"Analyzer engine '{Descriptor.Id}' could not reset its script runtime.",
                exception);
            Report(failure, exception);
            throw new AnalyzerEngineBridgeException(failure.Message, exception, failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<PendingAnalysis> pending;
        TaskCompletionSource<AnalyzerEngineBridgeReady>? readySource;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pending.Values.ToList();
            _pending.Clear();
            _initializationTask = null;
            readySource = _readySource;
            _readySource = null;
            _activeSessionId = null;
        }

        _scriptHost.MessageReceived -= ScriptHost_MessageReceived;
        var diagnostic = new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Information,
            "engine.runtime_disposed",
            "The analyzer runtime was disposed.");
        readySource?.TrySetException(new AnalyzerEngineBridgeException(diagnostic.Message, null, diagnostic));
        foreach (var entry in pending)
        {
            entry.Completion.TrySetResult(AnalysisResult.Cancelled(entry.Request, Descriptor, diagnostic));
        }

        try
        {
            await _scriptHost.InjectScriptAsync(BuildResetScript()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failure = AnalysisDiagnostic.Error(
                "engine.runtime_dispose_failed",
                $"Analyzer engine '{Descriptor.Id}' could not dispose its script runtime.",
                exception);
            Report(failure, exception);
        }
    }

    private async Task<AnalyzerEngineBridgeReady> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        Task<AnalyzerEngineBridgeReady> initialization;
        lock (_sync)
        {
            ThrowIfDisposed();
            _initializationTask ??= InitializeCoreAsync();
            initialization = _initializationTask;
        }

        return await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AnalyzerEngineBridgeReady> InitializeCoreAsync()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var readySource = new TaskCompletionSource<AnalyzerEngineBridgeReady>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _readySource = readySource;
            _activeSessionId = sessionId;
        }

        try
        {
            await _scriptHost.InjectScriptAsync(BuildBootstrapScript(sessionId)).ConfigureAwait(false);
            return await readySource.Task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not AnalyzerEngineBridgeException)
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "engine.bootstrap_failed",
                $"Analyzer engine '{Descriptor.Id}' could not bootstrap its runtime.",
                exception);
            Report(diagnostic, exception);
            lock (_sync)
            {
                _initializationTask = null;
            }

            readySource.TrySetException(new AnalyzerEngineBridgeException(diagnostic.Message, exception, diagnostic));
            throw new AnalyzerEngineBridgeException(diagnostic.Message, exception, diagnostic);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_readySource, readySource))
                {
                    _readySource = null;
                }
            }
        }
    }

    private void CancelPending(string correlationId)
    {
        PendingAnalysis? pending;
        lock (_sync)
        {
            _pending.TryGetValue(correlationId, out pending);
            if (pending is not null)
            {
                _pending.Remove(correlationId);
            }
        }

        if (pending is null)
        {
            return;
        }

        var diagnostic = new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Information,
            "analysis.cancelled",
            "The analyzer request was cancelled by its caller.");
        pending.Completion.TrySetCanceled();
        _ = SendCancelScriptAsync(correlationId, diagnostic.Message);
    }

    private async Task SendCancelScriptAsync(string correlationId, string reason)
    {
        try
        {
            await _scriptHost.InjectScriptAsync(
                BuildCancelScript(correlationId, reason)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "engine.cancel_dispatch_failed",
                $"Analyzer engine '{Descriptor.Id}' could not receive cancellation for '{correlationId}'.",
                exception);
            Report(diagnostic, exception);
        }
    }

    private void RemovePending(string correlationId)
    {
        lock (_sync)
        {
            _pending.Remove(correlationId);
        }
    }

    private static AnalyzerEngineDescriptor CreateDescriptor(AnalyzerEngineManifest manifest)
    {
        var capabilities = manifest.Capabilities;
        var descriptorCapabilities = new AnalyzerEngineCapabilities(
            supportsProfiles: true,
            supportsMods: true,
            supportsRate: true,
            supportsCancellation: true,
            supportedAlgorithms: capabilities?.Algorithms,
            supportedMetricIds: capabilities?.SemanticMetricIds);
        return new AnalyzerEngineDescriptor(
            manifest.Id,
            manifest.Name ?? manifest.Id,
            manifest.Version,
            descriptorCapabilities,
            supportedProfiles: capabilities?.OptionalAlgorithms?.Keys,
            upstreamVersion: manifest.Upstream?.SupportedVersions?.FirstOrDefault() ?? manifest.Upstream?.Version,
            maxConcurrency: 4,
            threadSafety: AnalyzerEngineThreadSafety.Concurrent);
    }

    private static string CreateCorrelationId() => "analysis-" + Guid.NewGuid().ToString("N");

    private void Report(AnalysisDiagnostic diagnostic, Exception? exception = null)
    {
        var severity = diagnostic.Severity switch
        {
            AnalysisDiagnosticSeverity.Error => AnalyzerEngineDiagnosticSeverity.Error,
            AnalysisDiagnosticSeverity.Information => AnalyzerEngineDiagnosticSeverity.Information,
            _ => AnalyzerEngineDiagnosticSeverity.Warning
        };
        _diagnosticSink.Report(
            "Analyzer engine script bridge",
            new AnalyzerEngineDiagnostic(
                diagnostic.Code,
                diagnostic.Message,
                severity: severity,
                exception: exception),
            exception);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class PendingAnalysis
    {
        public PendingAnalysis(AnalysisRequest request, TaskCompletionSource<AnalysisResult> completion)
        {
            Request = request;
            Completion = completion;
        }

        public AnalysisRequest Request
        {
            get;
        }

        public TaskCompletionSource<AnalysisResult> Completion
        {
            get;
        }
    }

    private sealed record CancellationCallbackState(
        AnalyzerEngineScriptBridge Bridge,
        string CorrelationId);
}
