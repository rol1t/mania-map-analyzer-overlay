using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class AnalyzerEngineScriptBridgeTests : IAsyncLifetime
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "ManiaMapAnalyzerOverlay-BridgeTests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingDiagnosticSink _diagnosticSink = new();
    private FakeScriptHost _host = null!;
    private AnalyzerEngineScriptBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_rootDirectory);
        var packageDirectory = CreatePackage();
        var package = Assert.Single(new AnalyzerEngineCatalog(_rootDirectory).Available());
        _host = new FakeScriptHost();
        _bridge = new AnalyzerEngineScriptBridge(package, _host, diagnosticSink: _diagnosticSink);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_bridge is not null)
        {
            await _bridge.DisposeAsync();
        }

        await _host.DisposeAsync();
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BootstrapsWithSameOriginDynamicImportAndMapsStructuredMetrics()
    {
        var analysisTask = _bridge.AnalyzeAsync(CreateRequest());
        var bootstrap = await _host.WaitForScriptAsync();

        Assert.Contains("await import(\"/ManiaMapAnalyzerOverlay/engines/test-headless/runtime.mjs\")", bootstrap);
        Assert.Contains("ManiaMapAnalyser", bootstrap);
        Assert.Contains("globalThis.location.href", bootstrap);
        _host.Publish(ReadyMessage());

        var requestScript = await _host.WaitForScriptAsync();
        var correlationId = ReadCorrelationId(requestScript);
        _host.Publish(ResultMessage(correlationId, "ok", metrics: """
            {"difficulty.star":{"id":"difficulty.star","value":5.5,"unit":"SR"},"feature.zero":{"id":"feature.zero","value":0},"feature.false":{"id":"feature.false","value":false}}
            """));

        var result = await analysisTask;

        Assert.Equal(AnalysisOutcome.Success, result.Outcome);
        Assert.Equal(5.5, result.Metrics["difficulty.star"].Value.GetDouble());
        Assert.Equal(0, result.Metrics["feature.zero"].Value.GetInt32());
        Assert.False(result.Metrics["feature.false"].Value.GetBoolean());
        Assert.Empty(_diagnosticSink.Entries);
    }

    [Fact]
    public async Task MapsPartialResponseAndKeepsDiagnostics()
    {
        var analysisTask = _bridge.AnalyzeAsync(CreateRequest());
        await _host.WaitForScriptAsync();
        _host.Publish(ReadyMessage());
        var requestScript = await _host.WaitForScriptAsync();
        var correlationId = ReadCorrelationId(requestScript);
        _host.Publish(ResultMessage(
            correlationId,
            "partial",
            """{"difficulty.star":{"id":"difficulty.star","value":4.25,"unit":"SR"}}""",
            """[{"code":"ETTERNA_STAGE_FAILED","message":"Etterna unavailable","severity":"warning"}]"""));

        var result = await analysisTask;

        Assert.Equal(AnalysisOutcome.Partial, result.Outcome);
        Assert.Equal(4.25, result.Metrics["difficulty.star"].Value.GetDouble());
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ETTERNA_STAGE_FAILED");
        Assert.Contains(_diagnosticSink.Entries, diagnostic => diagnostic.Code == "ETTERNA_STAGE_FAILED");
    }

    [Fact]
    public async Task MapsStructuredErrorToFailure()
    {
        var analysisTask = _bridge.AnalyzeAsync(CreateRequest());
        await _host.WaitForScriptAsync();
        _host.Publish(ReadyMessage());
        var requestScript = await _host.WaitForScriptAsync();
        var correlationId = ReadCorrelationId(requestScript);
        _host.Publish(ErrorMessage(correlationId));

        var result = await analysisTask;

        Assert.Equal(AnalysisOutcome.Failed, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PIPELINE_IMPORT_FAILED");
        Assert.Contains(_diagnosticSink.Entries, diagnostic => diagnostic.Code == "PIPELINE_IMPORT_FAILED");
    }

    [Fact]
    public async Task CancellationIsCorrelationScopedAndDispatchesCancelScript()
    {
        using var cancellation = new CancellationTokenSource();
        var analysisTask = _bridge.AnalyzeAsync(CreateRequest(), cancellation.Token);
        await _host.WaitForScriptAsync();
        _host.Publish(ReadyMessage());
        var requestScript = await _host.WaitForScriptAsync();
        var correlationId = ReadCorrelationId(requestScript);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysisTask);
        var cancelScript = await _host.WaitForScriptAsync();
        Assert.Contains(correlationId, cancelScript);
    }

    [Fact]
    public async Task IgnoresUnrelatedAndDuplicateMessages()
    {
        var analysisTask = _bridge.AnalyzeAsync(CreateRequest());
        await _host.WaitForScriptAsync();
        _host.Publish("analysis:legacy:{\"ignored\":true}");
        _host.Publish(ReadyMessage());
        var requestScript = await _host.WaitForScriptAsync();
        var correlationId = ReadCorrelationId(requestScript);
        var resultMessage = ResultMessage(correlationId, "ok", """{"difficulty.star":{"id":"difficulty.star","value":3.0}}""");
        _host.Publish(resultMessage);
        _host.Publish(resultMessage);

        var result = await analysisTask;

        Assert.Equal(AnalysisOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Metrics["difficulty.star"].Value.GetDouble());
    }

    [Fact]
    public async Task ResetCancelsPendingCorrelationAndIgnoresStaleSessionMessages()
    {
        var analysisTask = _bridge.AnalyzeAsync(CreateRequest());
        await _host.WaitForScriptAsync();
        _host.Publish(ReadyMessage());
        var requestScript = await _host.WaitForScriptAsync();
        var correlationId = ReadCorrelationId(requestScript);

        await _bridge.ResetAsync();

        var result = await analysisTask;
        Assert.Equal(AnalysisOutcome.Cancelled, result.Outcome);
        _host.Publish(ResultMessage(correlationId, "ok", """{"difficulty.star":{"value":99}}"""));
    }

    [Fact]
    public void DeployerCopiesAndAtomicallyReplacesValidatedPackage()
    {
        var tosuDirectory = Path.Combine(_rootDirectory, "tosu");
        var firstPackageDirectory = CreatePackage();
        var firstPackage = Assert.Single(new AnalyzerEngineCatalog(_rootDirectory).Available());
        var deployer = new AnalyzerEnginePackageDeployer(_diagnosticSink);

        var first = deployer.Deploy(firstPackage, tosuDirectory);
        Assert.False(first.ReplacedExisting);
        Assert.Equal("runtime-v1", File.ReadAllText(Path.Combine(first.TargetDirectory, "runtime.mjs")));

        File.WriteAllText(Path.Combine(firstPackageDirectory, "runtime.mjs"), "runtime-v2");
        var secondPackage = Assert.Single(new AnalyzerEngineCatalog(_rootDirectory).Available());
        var second = deployer.Deploy(secondPackage, tosuDirectory);

        Assert.True(second.ReplacedExisting);
        Assert.Equal(first.TargetDirectory, second.TargetDirectory);
        Assert.Equal("runtime-v2", File.ReadAllText(Path.Combine(second.TargetDirectory, "runtime.mjs")));
        Assert.False(Directory.Exists(second.TargetDirectory + ".staging"));
    }

    private AnalysisRequest CreateRequest() => new(
        "test-headless",
        new BeatmapIdentity("42", "hash"),
        "osu file content",
        "Sunny",
        "test-profile");

    private string CreatePackage(string directoryName = "package")
    {
        var packageDirectory = Path.Combine(_rootDirectory, directoryName);
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "runtime.mjs"), "runtime-v1");
        File.WriteAllText(Path.Combine(packageDirectory, "worker.mjs"), "worker");
        File.WriteAllText(Path.Combine(packageDirectory, "manifest.json"), """
            {
              "manifestSchemaVersion": 1,
              "kind": "analyzer-engine",
              "id": "test-headless",
              "name": "Test headless",
              "version": "1.0.0",
              "protocol": "test.headless",
              "protocolVersion": 1,
              "runtime": "runtime.mjs",
              "worker": "worker.mjs",
              "upstream": {
                "name": "Test upstream",
                "repository": "https://example.test/upstream",
                "license": "MIT",
                "integration": "dynamic-import",
                "supportedVersions": ["2.0.0"]
              },
              "capabilities": {
                "algorithms": ["Sunny"],
                "optionalAlgorithms": {},
                "semanticMetricIds": ["difficulty.star"]
              }
            }
            """);
        return packageDirectory;
    }

    private static string ReadyMessage() =>
        AnalyzerEngineScriptBridge.NativeMessagePrefix + JsonSerializer.Serialize(new
        {
            protocol = "test.headless",
            protocolVersion = 1,
            type = "runtime.ready",
            status = "ok",
            diagnostics = Array.Empty<object>()
        });

    private static string ResultMessage(
        string correlationId,
        string status,
         string metrics,
         string diagnostics = "[]")
    {
        using var metricsDocument = JsonDocument.Parse(metrics);
        using var diagnosticsDocument = JsonDocument.Parse(diagnostics);
        return AnalyzerEngineScriptBridge.NativeMessagePrefix + JsonSerializer.Serialize(new
        {
            protocol = "test.headless",
            protocolVersion = 1,
            type = "analysis.result",
            correlationId,
            status,
            analysis = new
            {
                requestedAlgorithm = "Sunny",
                actualAlgorithm = "Sunny",
                metrics = metricsDocument.RootElement,
                diagnostics = diagnosticsDocument.RootElement
            },
            diagnostics = diagnosticsDocument.RootElement
        });
    }

    private static string ErrorMessage(string correlationId) =>
        AnalyzerEngineScriptBridge.NativeMessagePrefix + JsonSerializer.Serialize(new
        {
            protocol = "test.headless",
            protocolVersion = 1,
            type = "analysis.error",
            correlationId,
            status = "error",
            error = new
            {
                code = "PIPELINE_IMPORT_FAILED",
                message = "Pipeline import failed",
                stage = "pipeline-import"
            }
        });

    private static string ReadCorrelationId(string script)
    {
        const string marker = "const request = ";
        var start = script.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = script.IndexOf(';', start);
        using var document = JsonDocument.Parse(script[start..end]);
        return document.RootElement.GetProperty("correlationId").GetString()!;
    }

    private sealed class RecordingDiagnosticSink : IAnalyzerEngineDiagnosticSink
    {
        public List<AnalyzerEngineDiagnostic> Entries { get; } = [];

        public void Report(string operation, AnalyzerEngineDiagnostic diagnostic, Exception? exception = null) =>
            Entries.Add(diagnostic);
    }

    private sealed class FakeScriptHost : IAnalyzerScriptHost
    {
        private readonly object _sync = new();
        private readonly Queue<string> _scripts = [];
        private TaskCompletionSource<string>? _nextScript;
        private bool _disposed;

        public event EventHandler<AnalyzerScriptMessageEventArgs>? MessageReceived;

        public Task<string?> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_nextScript is not null)
                {
                    _nextScript.TrySetResult(script);
                    _nextScript = null;
                }
                else
                {
                    _scripts.Enqueue(script);
                }
            }

            return Task.FromResult<string?>(null);
        }

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> WaitForScriptAsync()
        {
            lock (_sync)
            {
                if (_scripts.TryDequeue(out var script))
                {
                    return Task.FromResult(script);
                }

                _nextScript = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _nextScript.Task;
            }
        }

        public void Publish(string body)
        {
            MessageReceived?.Invoke(this, new AnalyzerScriptMessageEventArgs(body));
        }

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                _disposed = true;
                _nextScript?.TrySetCanceled();
                _nextScript = null;
            }

            MessageReceived = null;
            return ValueTask.CompletedTask;
        }
    }
}
