using System.Reflection;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class AnalyzerEngineSupervisorResetGateTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "ManiaMapAnalyzerOverlay-SupervisorResetTests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingDiagnosticSink _diagnosticSink = new();

    public AnalyzerEngineSupervisorResetGateTests() => Directory.CreateDirectory(_rootDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task NotifyDuringBootstrapping_IsIgnored_AndDoesNotDisruptStartup()
    {
        var packageDirectory = CreatePackage();
        var catalog = new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink);
        var deployer = new AnalyzerEnginePackageDeployer(_diagnosticSink);
        var host = new TrackingFakeScriptHost();
        await using var supervisor = new AnalyzerEngineSupervisor(catalog, deployer, host);

        var startTask = supervisor.StartAsync();

        // Wait for bootstrap script to be injected (Bootstrapping state).
        var bootstrapScript = await WaitForScriptWithTimeoutAsync(host, TimeSpan.FromSeconds(5));
        Assert.Contains("const sessionId", bootstrapScript);

        // Supervisor should be in Bootstrapping now.
        Assert.Equal(AnalyzerEngineSupervisorStatus.Bootstrapping, supervisor.CurrentState.Status);

        var resetBefore = host.ResetCallCount;
        var injectBefore = host.InjectCallCount;

        // These should be ignored during Bootstrapping.
        await supervisor.NotifyNavigationAsync();
        await supervisor.NotifyTosuRestartAsync();

        Assert.Equal(resetBefore, host.ResetCallCount);
        // Bridge.ResetAsync would inject a reset script + host.ResetAsync; ensure no injection happened.
        Assert.Equal(injectBefore, host.InjectCallCount);
        Assert.Equal(AnalyzerEngineSupervisorStatus.Bootstrapping, supervisor.CurrentState.Status);

        // Complete bootstrap.
        host.Publish(ReadyMessage());

        // Probe script.
        var probeScript = await WaitForScriptWithTimeoutAsync(host, TimeSpan.FromSeconds(5));
        var correlationId = ReadCorrelationId(probeScript);
        host.Publish(ResultMessage(correlationId, "ok", """{"difficulty.star":{"id":"difficulty.star","value":5.0}}"""));

        var finalState = await WaitWithTimeoutAsync(startTask, TimeSpan.FromSeconds(5));

        Assert.Equal(AnalyzerEngineSupervisorStatus.Ready, finalState.Status);
        Assert.True(finalState.IsReady);
        Assert.Equal(AnalyzerEngineSupervisorStatus.Ready, supervisor.CurrentState.Status);
        // Still no reset during bootstrapping.
        Assert.Equal(resetBefore, host.ResetCallCount);
    }

    [Fact]
    public async Task NotifyDuringDeploying_IsIgnored()
    {
        var packageDirectory = CreatePackage();
        var catalog = new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink);
        var package = Assert.Single(catalog.Available());
        var host = new TrackingFakeScriptHost();
        var deployer = new AnalyzerEnginePackageDeployer(_diagnosticSink);
        await using var supervisor = new AnalyzerEngineSupervisor(catalog, deployer, host);

        // Inject a bridge and force Deploying state via reflection to simulate race window.
        var bridge = new AnalyzerEngineScriptBridge(package, host, diagnosticSink: _diagnosticSink);
        SetSupervisorState(supervisor, AnalyzerEngineSupervisorStatus.Deploying, bridge);

        await supervisor.NotifyNavigationAsync();
        await supervisor.NotifyTosuRestartAsync();

        Assert.Equal(0, host.ResetCallCount);

        // Cleanup bridge (supervisor owns it; dispose separately to avoid leak).
        await bridge.DisposeAsync();
    }

    [Fact]
    public async Task NotifyDuringBootstrappingViaReflection_IsIgnored()
    {
        var catalog = new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink);
        CreatePackage();
        catalog = new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink);
        var package = Assert.Single(catalog.Available());
        var host = new TrackingFakeScriptHost();
        var deployer = new AnalyzerEnginePackageDeployer(_diagnosticSink);
        await using var supervisor = new AnalyzerEngineSupervisor(catalog, deployer, host);
        var bridge = new AnalyzerEngineScriptBridge(package, host, diagnosticSink: _diagnosticSink);
        SetSupervisorState(supervisor, AnalyzerEngineSupervisorStatus.Bootstrapping, bridge);

        await supervisor.NotifyNavigationAsync();
        await supervisor.NotifyTosuRestartAsync();

        Assert.Equal(0, host.ResetCallCount);
        await bridge.DisposeAsync();
    }

    [Fact]
    public async Task NotifyWhenReady_StillResetsBridge()
    {
        var packageDirectory = CreatePackage();
        var catalog = new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink);
        var deployer = new AnalyzerEnginePackageDeployer(_diagnosticSink);
        var host = new TrackingFakeScriptHost();
        await using var supervisor = new AnalyzerEngineSupervisor(catalog, deployer, host);

        // Bring to Ready via real startup.
        var startTask = supervisor.StartAsync();
        var bootstrapScript = await WaitForScriptWithTimeoutAsync(host, TimeSpan.FromSeconds(5));
        host.Publish(ReadyMessage());
        var probeScript = await WaitForScriptWithTimeoutAsync(host, TimeSpan.FromSeconds(5));
        var correlationId = ReadCorrelationId(probeScript);
        host.Publish(ResultMessage(correlationId, "ok", """{"difficulty.star":{"id":"difficulty.star","value":5.0}}"""));
        var finalState = await WaitWithTimeoutAsync(startTask, TimeSpan.FromSeconds(5));
        Assert.Equal(AnalyzerEngineSupervisorStatus.Ready, finalState.Status);

        var resetBefore = host.ResetCallCount;
        await supervisor.NotifyNavigationAsync();
        Assert.Equal(resetBefore + 1, host.ResetCallCount);

        await supervisor.NotifyTosuRestartAsync();
        Assert.Equal(resetBefore + 2, host.ResetCallCount);
    }

    [Fact]
    public async Task NotifyWhenReady_ViaReflection_ResetsBridge()
    {
        var catalog = new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink);
        CreatePackage();
        catalog = new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink);
        var package = Assert.Single(catalog.Available());
        var host = new TrackingFakeScriptHost();
        var deployer = new AnalyzerEnginePackageDeployer(_diagnosticSink);
        await using var supervisor = new AnalyzerEngineSupervisor(catalog, deployer, host);
        var bridge = new AnalyzerEngineScriptBridge(package, host, diagnosticSink: _diagnosticSink);
        SetSupervisorState(supervisor, AnalyzerEngineSupervisorStatus.Ready, bridge);

        await supervisor.NotifyNavigationAsync();
        Assert.Equal(1, host.ResetCallCount);

        await supervisor.NotifyTosuRestartAsync();
        Assert.Equal(2, host.ResetCallCount);

        await bridge.DisposeAsync();
    }

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

    private static void SetSupervisorState(AnalyzerEngineSupervisor supervisor, AnalyzerEngineSupervisorStatus status, AnalyzerEngineScriptBridge? bridge)
    {
        var syncField = typeof(AnalyzerEngineSupervisor).GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var stateField = typeof(AnalyzerEngineSupervisor).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var bridgeField = typeof(AnalyzerEngineSupervisor).GetField("_bridge", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var coordinatorField = typeof(AnalyzerEngineSupervisor).GetField("_coordinator", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var sync = syncField.GetValue(supervisor)!;
        lock (sync)
        {
            var currentState = (AnalyzerEngineSupervisorState)stateField.GetValue(supervisor)!;
            var newState = new AnalyzerEngineSupervisorState(
                status,
                currentState.EngineId ?? bridge?.Descriptor.Id,
                currentState.EngineVersion ?? bridge?.Descriptor.Version,
                currentState.Message,
                currentState.Diagnostics,
                IsFallback: false,
                IsReady: status == AnalyzerEngineSupervisorStatus.Ready);

            // If we override to Ready, ensure diagnostics/message reflect readiness.
            if (status == AnalyzerEngineSupervisorStatus.Ready)
            {
                newState = new AnalyzerEngineSupervisorState(
                    AnalyzerEngineSupervisorStatus.Ready,
                    bridge!.Descriptor.Id,
                    bridge.Descriptor.Version,
                    $"Analyzer engine '{bridge.Descriptor.Id}' v{bridge.Descriptor.Version} is ready.",
                    currentState.Diagnostics,
                    IsFallback: false,
                    IsReady: true);
            }

            stateField.SetValue(supervisor, newState);
            bridgeField.SetValue(supervisor, bridge);
            if (bridge is not null && status == AnalyzerEngineSupervisorStatus.Ready)
            {
                // Coordinator is not used by reset gate, but keep consistent if needed.
                var existingCoordinator = coordinatorField.GetValue(supervisor) as AnalyzerExecutionCoordinator;
                existingCoordinator?.Dispose();
                var planner = new AnalyzerExecutionPlanner(new[] { bridge });
                var coordinator = new AnalyzerExecutionCoordinator(planner, new AppLoggerAnalysisDiagnosticsStub());
                coordinatorField.SetValue(supervisor, coordinator);
            }
        }
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

    private static string ResultMessage(string correlationId, string status, string metrics, string diagnostics = "[]")
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

    private static string ReadCorrelationId(string script)
    {
        const string marker = "const request = ";
        var start = script.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = script.IndexOf(';', start);
        using var document = JsonDocument.Parse(script[start..end]);
        return document.RootElement.GetProperty("correlationId").GetString()!;
    }

    private static async Task<string> WaitForScriptWithTimeoutAsync(TrackingFakeScriptHost host, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await host.WaitForScriptAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Timed out waiting for script after {timeout.TotalSeconds}s. Injected={host.InjectCallCount} Reset={host.ResetCallCount}");
        }
    }

    private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
    {
        var delay = Task.Delay(timeout);
        var completed = await Task.WhenAny(task, delay);
        if (completed == delay)
        {
            throw new TimeoutException($"Timed out waiting for task after {timeout.TotalSeconds}s");
        }

        return await task;
    }

    private sealed class RecordingDiagnosticSink : IAnalyzerEngineDiagnosticSink
    {
        public List<AnalyzerEngineDiagnostic> Entries { get; } = [];
        public void Report(string operation, AnalyzerEngineDiagnostic diagnostic, Exception? exception = null) => Entries.Add(diagnostic);
    }

    private sealed class AppLoggerAnalysisDiagnosticsStub : IAnalysisDiagnostics
    {
        public void Report(AnalysisDiagnostic diagnostic)
        {
        }
    }

    private sealed class TrackingFakeScriptHost : IAnalyzerScriptHost
    {
        private readonly object _sync = new();
        private readonly Queue<string> _scripts = new();
        private TaskCompletionSource<string>? _nextScript;
        private bool _disposed;

        public int ResetCallCount
        {
            get; private set;
        }
        public int InjectCallCount
        {
            get; private set;
        }

        public event EventHandler<AnalyzerScriptMessageEventArgs>? MessageReceived;

        public Task<string?> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                InjectCallCount++;
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

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ResetCallCount++;
            }

            return Task.CompletedTask;
        }

        public Task<string> WaitForScriptAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_scripts.TryDequeue(out var script))
                {
                    return Task.FromResult(script);
                }

                _nextScript = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationToken.Register(() => _nextScript.TrySetCanceled(cancellationToken));
                }

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
