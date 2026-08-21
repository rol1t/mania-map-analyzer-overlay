using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

public enum AnalyzerEngineSupervisorStatus
{
    NotStarted,
    Deploying,
    Bootstrapping,
    Ready,
    ProbeFailed,
    Fallback,
    Error,
    Disposed
}

public sealed record AnalyzerEngineSupervisorState(
    AnalyzerEngineSupervisorStatus Status,
    string? EngineId,
    string? EngineVersion,
    string Message,
    IReadOnlyList<AnalyzerEngineDiagnostic> Diagnostics,
    bool IsFallback,
    bool IsReady)
{
    public static AnalyzerEngineSupervisorState NotStartedState =>
        new(AnalyzerEngineSupervisorStatus.NotStarted, null, null, "Analyzer engine supervisor has not been started.", [], false, false);
}

public sealed class AnalyzerEngineSupervisor : IAsyncDisposable
{
    private const string FallbackCode = "engine.fallback_to_dom_adapter";
    private const string ProbeBeatmapContent = "osu file format v14\n[General]\nAudioFilename: audio.mp3\nAudioLeadIn: 0\nPreviewTime: -1\nCountdown: 0\nSampleSet: Normal\nStackLeniency: 0.7\nMode: 3\nLetterboxInBreaks: 0\nSpecialStyle: 0\nWidescreenStoryboard: 0\n\n[Editor]\nDistanceSpacing: 1\nBeatDivisor: 4\nGridSize: 8\nTimelineZoom: 1\n\n[Metadata]\nTitle:Probe\nTitleUnicode:Probe\nArtist:Test\nArtistUnicode:Test\nCreator:Test\nVersion:Probe\nSource:\nTags:\nBeatmapID:0\nBeatmapSetID:-1\n\n[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n\n[Events]\n//Background and Video events\n\n[TimingPoints]\n0,500,4,1,0,100,1,0\n\n[HitObjects]\n64,192,1000,1,0,0:0:0:0:\n192,192,1500,1,0,0:0:0:0:\n320,192,2000,1,0,0:0:0:0:\n448,192,2500,1,0,0:0:0:0:\n64,192,3000,1,0,0:0:0:0:\n192,192,3500,1,0,0:0:0:0:\n320,192,4000,1,0,0:0:0:0:\n448,192,4500,1,0,0:0:0:0:\n";
    private const string DefaultProbeAlgorithm = "Mixed";
    private readonly AnalyzerEngineCatalog _catalog;
    private readonly AnalyzerEnginePackageDeployer _deployer;
    private readonly IAnalyzerScriptHost _scriptHost;
    private readonly IAnalyzerEngineDiagnosticSink _diagnosticSink;
    private readonly IAnalysisDiagnostics _analysisDiagnostics;
    private readonly object _sync = new();
    private readonly List<AnalyzerEngineDiagnostic> _diagnostics = [];
    private AnalyzerEngineScriptBridge? _bridge;
    private AnalyzerExecutionCoordinator? _coordinator;
    private AnalyzerEngineSupervisorState _state = AnalyzerEngineSupervisorState.NotStartedState;
    private AnalyzerEnginePackage? _activePackage;
    private int _restartAttempts;
    private bool _disposed;

    public AnalyzerEngineSupervisor(
        AnalyzerEngineCatalog catalog,
        AnalyzerEnginePackageDeployer deployer,
        IAnalyzerScriptHost scriptHost,
        IAnalyzerEngineDiagnosticSink? diagnosticSink = null,
        IAnalysisDiagnostics? analysisDiagnostics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _deployer = deployer ?? throw new ArgumentNullException(nameof(deployer));
        _scriptHost = scriptHost ?? throw new ArgumentNullException(nameof(scriptHost));
        _diagnosticSink = diagnosticSink ?? new AppLoggerAnalyzerEngineDiagnosticSink();
        _analysisDiagnostics = analysisDiagnostics ?? new AppLoggerAnalysisDiagnosticsAdapter();
    }

    public event EventHandler<AnalyzerEngineSupervisorState>? StateChanged;

    public AnalyzerEngineSupervisorState CurrentState
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public IAnalyzerEngine? ActiveEngine
    {
        get
        {
            lock (_sync)
            {
                return _bridge;
            }
        }
    }

    public AnalyzerEngineDescriptor? ActiveDescriptor
    {
        get
        {
            lock (_sync)
            {
                return _bridge?.Descriptor;
            }
        }
    }

    public bool IsFallbackActive
    {
        get
        {
            lock (_sync)
            {
                return _state.IsFallback;
            }
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _state.IsReady;
            }
        }
    }

    public async Task<AnalyzerEngineSupervisorState> StartAsync(
        string? preferredEngineId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        AnalyzerEnginePackage? package;
        try
        {
            package = SelectPackage(preferredEngineId);
        }
        catch (Exception exception)
        {
            return EnterFallback(
                "engine.package_selection_failed",
                $"Analyzer engine selection failed: {exception.Message}",
                exception);
        }

        if (package is null || !package.IsAvailable)
        {
            var reason = package is null
                ? "No analyzer engine package was discovered."
                : $"Analyzer engine '{package.Id}' is unavailable: {string.Join("; ", package.Diagnostics.Select(diagnostic => diagnostic.Code))}";
            foreach (var diagnostic in package?.Diagnostics ?? _catalog.Diagnostics)
            {
                AddDiagnostic(diagnostic);
            }

            return EnterFallback(
                "engine.no_available_engine",
                reason + " Falling back to the legacy DOM adapter. This is an explicit fallback and not a silent failure.",
                null,
                package?.Diagnostics);
        }

        lock (_sync)
        {
            _activePackage = package;
        }

        UpdateState(new AnalyzerEngineSupervisorState(
            AnalyzerEngineSupervisorStatus.Deploying,
            package.Id,
            package.Version,
            $"Deploying analyzer engine '{package.Id}' v{package.Version}...",
            GetDiagnosticsSnapshot(),
            IsFallback: false,
            IsReady: false));

        try
        {
            await DeployAsync(package, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return EnterFallback(
                "engine.deploy_failed",
                $"Analyzer engine '{package.Id}' could not be deployed and will not be used. Falling back to DOM adapter.",
                exception);
        }

        UpdateState(new AnalyzerEngineSupervisorState(
            AnalyzerEngineSupervisorStatus.Bootstrapping,
            package.Id,
            package.Version,
            $"Starting analyzer engine '{package.Id}'...",
            GetDiagnosticsSnapshot(),
            IsFallback: false,
            IsReady: false));

        AnalyzerEngineScriptBridge bridge;
        try
        {
            bridge = new AnalyzerEngineScriptBridge(
                package,
                _scriptHost,
                diagnosticSink: _diagnosticSink);
        }
        catch (Exception exception)
        {
            return EnterFallback(
                "engine.bridge_creation_failed",
                $"Analyzer engine '{package.Id}' could not be initialized. Falling back to DOM adapter.",
                exception);
        }

        lock (_sync)
        {
            _bridge?.DisposeAsync().AsTask().ConfigureAwait(false);
            _bridge = bridge;
            _coordinator = new AnalyzerExecutionCoordinator(
                new AnalyzerExecutionPlanner(new[] { bridge }),
                _analysisDiagnostics);
            _restartAttempts = 0;
        }

        AnalyzerEngineProbeResult? probeResult = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probeResult = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (probeResult.IsSuccess)
            {
                break;
            }

            if (IsTransientProbeFailure(probeResult) && attempt < 2)
            {
                ReportWarning(
                    "Analyzer engine probe retry",
                    $"Probe attempt {attempt + 1} for engine '{package.Id}' hit transient failure '{probeResult.FailureCode}'. Retrying after WebView navigation settles.");
                try
                {
                    await Task.Delay(600, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                continue;
            }

            break;
        }

        if (probeResult is not null && probeResult.IsSuccess)
        {
            var readyState = new AnalyzerEngineSupervisorState(
                AnalyzerEngineSupervisorStatus.Ready,
                package.Id,
                package.Version,
                $"Analyzer engine '{package.Id}' v{package.Version} is ready (upstream {package.Manifest?.Upstream?.SupportedVersions?.FirstOrDefault() ?? package.Version}). Partial results and diagnostics will be surfaced when available.",
                GetDiagnosticsSnapshot(),
                IsFallback: false,
                IsReady: true);
            UpdateState(readyState);
            ReportInfo("Analyzer engine ready", readyState.Message);
            return readyState;
        }

        return EnterFallback(
            probeResult?.FailureCode ?? "engine.probe_failed",
            probeResult?.FailureMessage ?? $"Analyzer engine '{package.Id}' did not pass its compatibility probe. Falling back to DOM adapter.",
            probeResult?.Exception,
            probeResult?.Diagnostics);
    }

    public async Task<AnalyzerEngineProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        AnalyzerEngineScriptBridge? bridge;
        AnalyzerEnginePackage? package;
        lock (_sync)
        {
            bridge = _bridge;
            package = _activePackage;
        }

        if (bridge is null || package is null)
        {
            return AnalyzerEngineProbeResult.Failure(
                "engine.not_initialized",
                "Analyzer engine probe was requested before the engine was started.",
                null,
                GetDiagnosticsSnapshot());
        }

        try
        {
            var request = CreateProbeRequest(bridge.Descriptor.Id);
            var result = await bridge.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);

            var isBeatmapParseFailureForProbe = result.Diagnostics.Any(diagnostic =>
                diagnostic.Code.Contains("ANALYSIS_FAILED", StringComparison.OrdinalIgnoreCase) &&
                diagnostic.Message.Contains("Beatmap parse failed", StringComparison.OrdinalIgnoreCase));

            foreach (var diagnostic in result.Diagnostics)
            {
                if (isBeatmapParseFailureForProbe && diagnostic.Severity == AnalysisDiagnosticSeverity.Error)
                {
                    var downgraded = new AnalysisDiagnostic(
                        AnalysisDiagnosticSeverity.Warning,
                        diagnostic.Code,
                        diagnostic.Message,
                        diagnostic.TechnicalDetails,
                        diagnostic.Properties);
                    AddAnalysisDiagnostic(downgraded);
                    ReportWarning($"Analyzer probe diagnostic: {diagnostic.Code}", diagnostic.Message + " (downgraded from error for probe beatmap)");
                }
                else
                {
                    AddAnalysisDiagnostic(diagnostic);
                    if (diagnostic.Severity == AnalysisDiagnosticSeverity.Error)
                    {
                        ReportWarning($"Analyzer probe diagnostic: {diagnostic.Code}", diagnostic.Message);
                    }
                }
            }

            if (result.Outcome is AnalysisOutcome.Success or AnalysisOutcome.Partial)
            {
                ReportInfo(
                    "Analyzer engine probe",
                    $"Probe succeeded for engine '{bridge.Descriptor.Id}' v{bridge.Descriptor.Version} with outcome {result.Outcome} and {result.Metrics.Count} metrics.");
                if (result.Outcome == AnalysisOutcome.Partial)
                {
                    ReportWarning(
                        "Analyzer engine probe partial",
                        $"Analyzer engine '{bridge.Descriptor.Id}' returned a partial probe result. Compatibility is degraded but the engine remains usable.");
                }

                return AnalyzerEngineProbeResult.Success(GetDiagnosticsSnapshot());
            }

            if (result.Outcome == AnalysisOutcome.Cancelled)
            {
                var transientCode = result.Diagnostics.FirstOrDefault()?.Code ?? "engine.runtime_reset";
                var message = $"Analyzer engine '{bridge.Descriptor.Id}' probe was cancelled (transient reset): {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))}";
                var merged = GetDiagnosticsSnapshot().Concat(result.Diagnostics.Select(diagnostic =>
                    new AnalyzerEngineDiagnostic(
                        diagnostic.Code,
                        diagnostic.Message,
                        null,
                        AnalyzerEngineDiagnosticSeverity.Warning,
                        string.IsNullOrWhiteSpace(diagnostic.TechnicalDetails) ? null : new InvalidOperationException(diagnostic.TechnicalDetails)))).ToArray();
                return AnalyzerEngineProbeResult.Failure(
                    transientCode,
                    message,
                    null,
                    merged);
            }

            if (isBeatmapParseFailureForProbe)
            {
                ReportWarning(
                    "Analyzer engine probe beatmap parse",
                    $"Probe beatmap was not parsed by engine '{bridge.Descriptor.Id}'. The runtime itself is compatible; treating probe as successful with diagnostics. Details: {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))}");
                return AnalyzerEngineProbeResult.Success(GetDiagnosticsSnapshot());
            }

            var hasRuntimeFailure = result.Diagnostics.Any(diagnostic =>
                diagnostic.Code.Contains("bootstrap", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Code.Contains("incompatible", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Code.Contains("protocol", StringComparison.OrdinalIgnoreCase) ||
                (diagnostic.Code.Contains("runtime", StringComparison.OrdinalIgnoreCase) &&
                 !diagnostic.Code.Contains("runtime_reset", StringComparison.OrdinalIgnoreCase) &&
                 !diagnostic.Code.Contains("runtime_disposed", StringComparison.OrdinalIgnoreCase)));

            if (hasRuntimeFailure)
            {
                var message = $"Analyzer engine '{bridge.Descriptor.Id}' reported an incompatible runtime during probe: {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))}";
                var merged = GetDiagnosticsSnapshot().Concat(result.Diagnostics.Select(diagnostic =>
                    new AnalyzerEngineDiagnostic(
                        diagnostic.Code,
                        diagnostic.Message,
                        null,
                        diagnostic.Severity == AnalysisDiagnosticSeverity.Error
                            ? AnalyzerEngineDiagnosticSeverity.Error
                            : diagnostic.Severity == AnalysisDiagnosticSeverity.Warning
                                ? AnalyzerEngineDiagnosticSeverity.Warning
                                : AnalyzerEngineDiagnosticSeverity.Information,
                        string.IsNullOrWhiteSpace(diagnostic.TechnicalDetails) ? null : new InvalidOperationException(diagnostic.TechnicalDetails)))).ToArray();
                return AnalyzerEngineProbeResult.Failure(
                    "engine.runtime_incompatible",
                    message,
                    null,
                    merged);
            }

            {
                var merged = GetDiagnosticsSnapshot().Concat(result.Diagnostics.Select(diagnostic =>
                    new AnalyzerEngineDiagnostic(
                        diagnostic.Code,
                        diagnostic.Message,
                        null,
                        diagnostic.Severity == AnalysisDiagnosticSeverity.Error
                            ? AnalyzerEngineDiagnosticSeverity.Error
                            : diagnostic.Severity == AnalysisDiagnosticSeverity.Warning
                                ? AnalyzerEngineDiagnosticSeverity.Warning
                                : AnalyzerEngineDiagnosticSeverity.Information,
                        string.IsNullOrWhiteSpace(diagnostic.TechnicalDetails) ? null : new InvalidOperationException(diagnostic.TechnicalDetails)))).ToArray();
                return AnalyzerEngineProbeResult.Failure(
                    "engine.probe_analysis_failed",
                    $"Analyzer engine '{bridge.Descriptor.Id}' probe returned outcome {result.Outcome}: {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))}",
                    null,
                    merged);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AnalyzerEngineBridgeException exception)
        {
            AddEngineDiagnostic(exception.Diagnostic);
            return AnalyzerEngineProbeResult.Failure(
                exception.Diagnostic.Code,
                $"Analyzer engine '{package.Id}' runtime bootstrap failed: {exception.Diagnostic.Message}",
                exception,
                GetDiagnosticsSnapshot());
        }
        catch (Exception exception)
        {
            return AnalyzerEngineProbeResult.Failure(
                "engine.probe_exception",
                $"Analyzer engine '{package.Id}' probe threw an exception: {exception.Message}",
                exception,
                GetDiagnosticsSnapshot());
        }
    }

    public async Task<AnalyzerEngineSupervisorState> RestartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        AnalyzerEnginePackage? package;
        lock (_sync)
        {
            package = _activePackage;
            _restartAttempts++;
        }

        var delay = TimeSpan.FromMilliseconds(Math.Min(2000 * Math.Pow(2, _restartAttempts - 1), 10000));
        ReportWarning("Analyzer engine restart", $"Restarting analyzer engine '{package?.Id ?? "unknown"}' in {delay.TotalMilliseconds} ms (attempt {_restartAttempts}).");

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        await StopEngineAsync().ConfigureAwait(false);
        return await StartAsync(package?.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AnalysisResult?> AnalyzeAsync(
        TosuBeatmapSnapshot snapshot,
        string requestedAlgorithm = DefaultProbeAlgorithm,
        string profileId = "headless-overlay",
        string configurationVersion = "1",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        AnalyzerExecutionCoordinator? coordinator;
        AnalyzerEngineDescriptor? descriptor;
        lock (_sync)
        {
            coordinator = _coordinator;
            descriptor = _bridge?.Descriptor;
        }

        if (coordinator is null || descriptor is null)
        {
            ReportWarning("Headless analysis requested", "Headless analysis was requested, but the engine is not ready. Using DOM fallback explicitly.");
            return null;
        }

        if (!descriptor.Capabilities.SupportedAlgorithms.IsEmpty &&
            !descriptor.Capabilities.SupportedAlgorithms.Contains(requestedAlgorithm, StringComparer.Ordinal))
        {
            var diagnostic = new AnalysisDiagnostic(
                AnalysisDiagnosticSeverity.Warning,
                "analysis.algorithm_unsupported",
                $"Engine '{descriptor.Id}' does not advertise algorithm '{requestedAlgorithm}'. Request will be validated by the planner.",
                properties: [new KeyValuePair<string, string>("engineId", descriptor.Id), new KeyValuePair<string, string>("requestedAlgorithm", requestedAlgorithm)]);
            _analysisDiagnostics.Report(diagnostic);
            ReportWarning("Analysis configuration", diagnostic.Message);
        }

        try
        {
            var request = new AnalysisRequest(
                descriptor.Id,
                snapshot.Identity,
                snapshot.RawBeatmap,
                new AnalysisConfiguration(requestedAlgorithm, configurationVersion),
                profileId,
                snapshot.Rate,
                snapshot.Mods);

            var result = await coordinator.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);

            foreach (var diagnostic in result.Diagnostics)
            {
                AddAnalysisDiagnostic(diagnostic);
            }

            if (result.Outcome == AnalysisOutcome.Partial)
            {
                ReportWarning(
                    "Headless analysis partial",
                    $"Engine '{descriptor.Id}' returned a partial result for beatmap {snapshot.Identity.StableKey} (requested {requestedAlgorithm}, actual {result.ActualAlgorithm ?? "unknown"}). Inspect diagnostics for the missing stage.");
            }
            else if (result.Outcome == AnalysisOutcome.Failed)
            {
                ReportWarning(
                    "Headless analysis failed",
                    $"Engine '{descriptor.Id}' failed for beatmap {snapshot.Identity.StableKey}: {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))}. Falling back to DOM adapter for this beatmap.");
            }
            else if (result.Outcome == AnalysisOutcome.Success)
            {
                ReportInfo(
                    "Headless analysis success",
                    $"Engine '{descriptor.Id}' analyzed beatmap {snapshot.Identity.StableKey} ({requestedAlgorithm}->{result.ActualAlgorithm}) with {result.Metrics.Count} metrics (effective rate {snapshot.Rate}, mods [{string.Join(",", snapshot.Mods)}]).");
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "analysis.headless_execution_failed",
                $"Headless analysis failed for beatmap {snapshot.Identity.StableKey}.",
                exception,
                [new KeyValuePair<string, string>("engineId", descriptor.Id)]);
            _analysisDiagnostics.Report(diagnostic);
            ReportWarning("Headless analysis exception", diagnostic.Message);
            return null;
        }
    }

    public async Task NotifyNavigationAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        AnalyzerEngineScriptBridge? bridge;
        lock (_sync)
        {
            bridge = _bridge;
        }

        if (bridge is null)
        {
            return;
        }

        try
        {
            await bridge.ResetAsync(cancellationToken).ConfigureAwait(false);
            ReportInfo("Analyzer engine navigation", "Analyzer engine runtime was reset after WebView navigation. It will re-bootstrap on the next analysis request.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportWarning("Analyzer engine navigation reset", $"The analyzer engine could not reset after navigation: {exception.Message}");
        }
    }

    public async Task NotifyTosuRestartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        AnalyzerEngineScriptBridge? bridge;
        lock (_sync)
        {
            bridge = _bridge;
        }

        if (bridge is null)
        {
            return;
        }

        try
        {
            await bridge.ResetAsync(cancellationToken).ConfigureAwait(false);
            ReportInfo("Analyzer engine tosu restart", "Analyzer engine runtime was reset after tosu restart.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportWarning("Analyzer engine tosu restart", $"The analyzer engine could not reset after tosu restart: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        AnalyzerEngineScriptBridge? bridge;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bridge = _bridge;
            _bridge = null;
            _coordinator?.Dispose();
            _coordinator = null;
        }

        if (bridge is not null)
        {
            await bridge.DisposeAsync().ConfigureAwait(false);
        }

        UpdateState(new AnalyzerEngineSupervisorState(
            AnalyzerEngineSupervisorStatus.Disposed,
            null,
            null,
            "Analyzer engine supervisor was disposed.",
            GetDiagnosticsSnapshot(),
            IsFallback: false,
            IsReady: false));
        GC.SuppressFinalize(this);
    }

    private AnalyzerEnginePackage? SelectPackage(string? preferredEngineId)
    {
        var packages = _catalog.List();
        if (!string.IsNullOrWhiteSpace(preferredEngineId))
        {
            var preferred = packages.FirstOrDefault(package =>
                string.Equals(package.Id, preferredEngineId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        var available = packages.Where(package => package.IsAvailable)
            .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return available ?? packages.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private async Task DeployAsync(AnalyzerEnginePackage package, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tosuDirectory = AppPaths.TosuDirectory;
        var legacyDirectory = AppPaths.LegacyTosuDirectory;

        string? targetDirectory = null;
        if (Directory.Exists(tosuDirectory))
        {
            targetDirectory = tosuDirectory;
        }
        else if (Directory.Exists(legacyDirectory))
        {
            targetDirectory = legacyDirectory;
        }

        if (targetDirectory is null)
        {
            var diagnostic = new AnalyzerEngineDiagnostic(
                "engine.tosu_directory_missing",
                $"Tosu directory '{tosuDirectory}' was not found. The engine package will be served directly from '{package.PackageDirectory}' without a staged copy.",
                tosuDirectory,
                AnalyzerEngineDiagnosticSeverity.Warning);
            AddDiagnostic(diagnostic);
            _diagnosticSink.Report("Deploying analyzer engine package", diagnostic);
            ReportWarning("Engine deploy", diagnostic.Message);
            return;
        }

        try
        {
            var deployment = _deployer.Deploy(package, targetDirectory);
            ReportInfo(
                "Engine deploy",
                $"Analyzer engine '{package.Id}' deployed to '{deployment.TargetDirectory}' (replacedExisting={deployment.ReplacedExisting}).");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Deploying analyzer engine '{package.Id}' failed.", exception);
        }

        await Task.CompletedTask;
    }

    private AnalysisRequest CreateProbeRequest(string engineId)
    {
        return new AnalysisRequest(
            engineId,
            new BeatmapIdentity("0", "probe-hash-0", "0"),
            ProbeBeatmapContent,
            DefaultProbeAlgorithm,
            "headless-probe",
            configurationVersion: "1");
    }

    private AnalyzerEngineSupervisorState EnterFallback(
        string code,
        string message,
        Exception? exception,
        IEnumerable<AnalyzerEngineDiagnostic>? diagnostics = null)
    {
        var fallbackDiagnostics = new List<AnalyzerEngineDiagnostic>();
        if (diagnostics is not null)
        {
            fallbackDiagnostics.AddRange(diagnostics);
        }

        var fallbackDiagnostic = new AnalyzerEngineDiagnostic(
            FallbackCode,
            message,
            null,
            AnalyzerEngineDiagnosticSeverity.Warning,
            exception);
        fallbackDiagnostics.Add(fallbackDiagnostic);
        foreach (var diagnostic in fallbackDiagnostics)
        {
            AddDiagnostic(diagnostic);
        }

        if (exception is not null)
        {
            ReportWarning("Analyzer engine fallback", message + " Exception: " + exception.Message);
        }
        else
        {
            ReportWarning("Analyzer engine fallback", message);
        }

        var state = new AnalyzerEngineSupervisorState(
            AnalyzerEngineSupervisorStatus.Fallback,
            _activePackage?.Id,
            _activePackage?.Version,
            message + " Legacy DOM adapter is now the explicit, clearly reported fallback.",
            GetDiagnosticsSnapshot(),
            IsFallback: true,
            IsReady: false);
        UpdateState(state);
        return state;
    }

    private async Task StopEngineAsync()
    {
        AnalyzerEngineScriptBridge? bridge;
        lock (_sync)
        {
            bridge = _bridge;
            _bridge = null;
            _coordinator?.Dispose();
            _coordinator = null;
        }

        if (bridge is not null)
        {
            await bridge.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void AddDiagnostic(AnalyzerEngineDiagnostic diagnostic)
    {
        lock (_sync)
        {
            _diagnostics.Add(diagnostic);
        }

        switch (diagnostic.Severity)
        {
            case AnalyzerEngineDiagnosticSeverity.Error:
                _diagnosticSink.Report("Analyzer engine supervisor", diagnostic, diagnostic.Exception);
                break;
            case AnalyzerEngineDiagnosticSeverity.Warning:
                AppLogger.Warning("Analyzer engine supervisor", diagnostic.Message, diagnostic.Exception);
                break;
            default:
                AppLogger.Info("Analyzer engine supervisor", diagnostic.Message);
                break;
        }
    }

    private void AddEngineDiagnostic(AnalysisDiagnostic diagnostic)
    {
        var engineDiagnostic = new AnalyzerEngineDiagnostic(
            diagnostic.Code,
            diagnostic.Message,
            null,
            diagnostic.Severity == AnalysisDiagnosticSeverity.Error
                ? AnalyzerEngineDiagnosticSeverity.Error
                : diagnostic.Severity == AnalysisDiagnosticSeverity.Warning
                    ? AnalyzerEngineDiagnosticSeverity.Warning
                    : AnalyzerEngineDiagnosticSeverity.Information,
            string.IsNullOrWhiteSpace(diagnostic.TechnicalDetails) ? null : new InvalidOperationException(diagnostic.TechnicalDetails));
        AddDiagnostic(engineDiagnostic);
    }

    private void AddAnalysisDiagnostic(AnalysisDiagnostic diagnostic)
    {
        _analysisDiagnostics.Report(diagnostic);
        var severity = diagnostic.Severity == AnalysisDiagnosticSeverity.Error
            ? AnalyzerEngineDiagnosticSeverity.Error
            : diagnostic.Severity == AnalysisDiagnosticSeverity.Warning
                ? AnalyzerEngineDiagnosticSeverity.Warning
                : AnalyzerEngineDiagnosticSeverity.Information;
        var engineDiagnostic = new AnalyzerEngineDiagnostic(
            diagnostic.Code,
            diagnostic.Message,
            null,
            severity,
            string.IsNullOrWhiteSpace(diagnostic.TechnicalDetails) ? null : new InvalidOperationException(diagnostic.TechnicalDetails));
        AddDiagnostic(engineDiagnostic);
    }

    private IReadOnlyList<AnalyzerEngineDiagnostic> GetDiagnosticsSnapshot()
    {
        lock (_sync)
        {
            return _diagnostics.ToArray();
        }
    }

    private void UpdateState(AnalyzerEngineSupervisorState state)
    {
        lock (_sync)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }

    private void ReportInfo(string operation, string message)
    {
        AppLogger.Info(operation, message);
    }

    private void ReportWarning(string operation, string message)
    {
        AppLogger.Warning(operation, message);
    }

    private static bool IsTransientProbeFailure(AnalyzerEngineProbeResult result)
    {
        if (result.IsSuccess)
        {
            return false;
        }

        var code = result.FailureCode ?? string.Empty;
        if (code.Contains("runtime_reset", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("runtime_disposed", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("analysis.cancelled", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Result was cancelled via bridge reset – treat as transient even if code is generic.
        if (result.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code, "engine.runtime_reset", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(diagnostic.Code, "engine.runtime_disposed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(diagnostic.Code, "analysis.cancelled", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class AppLoggerAnalysisDiagnosticsAdapter : IAnalysisDiagnostics
    {
        public void Report(AnalysisDiagnostic diagnostic)
        {
            var properties = diagnostic.Properties.Count == 0
                ? string.Empty
                : " [" + string.Join(", ", diagnostic.Properties.Select(property => property.Key + "=" + property.Value)) + "]";
            var message = diagnostic.Message + properties;
            var exception = string.IsNullOrWhiteSpace(diagnostic.TechnicalDetails)
                ? null
                : new InvalidOperationException(diagnostic.TechnicalDetails);

            switch (diagnostic.Severity)
            {
                case AnalysisDiagnosticSeverity.Error:
                    AppLogger.Error(diagnostic.Code, exception ?? new InvalidOperationException(message));
                    break;
                case AnalysisDiagnosticSeverity.Warning:
                    AppLogger.Warning(diagnostic.Code, message, exception);
                    break;
                default:
                    AppLogger.Info(diagnostic.Code, message);
                    break;
            }
        }
    }
}

public sealed record AnalyzerEngineProbeResult(
    bool IsSuccess,
    string? FailureCode,
    string? FailureMessage,
    Exception? Exception,
    IReadOnlyList<AnalyzerEngineDiagnostic> Diagnostics)
{
    public static AnalyzerEngineProbeResult Success(IReadOnlyList<AnalyzerEngineDiagnostic> diagnostics) =>
        new(true, null, null, null, diagnostics);

    public static AnalyzerEngineProbeResult Failure(
        string code,
        string message,
        Exception? exception,
        IReadOnlyList<AnalyzerEngineDiagnostic> diagnostics) =>
        new(false, code, message, exception, diagnostics);
}
