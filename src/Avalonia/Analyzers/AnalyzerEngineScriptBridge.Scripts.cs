using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

public sealed partial class AnalyzerEngineScriptBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private string BuildBootstrapScript(string sessionId)
    {
        var manifest = _package.Manifest!;
        var runtimeUrl = BuildResourceUrl(manifest.Runtime);
        var workerUrl = BuildResourceUrl(manifest.Worker);
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["baseUrl"] = BuildAbsoluteRoot(_upstreamRoot),
            ["pipelinePath"] = manifest.DefaultPipelinePath ?? string.Empty,
            ["pipelineExport"] = manifest.PipelineExport ?? string.Empty,
            ["companellaPath"] = manifest.DefaultCompanellaPath ?? string.Empty,
            ["companellaExport"] = manifest.CompanellaExport ?? string.Empty,
            ["mixedEstimatorPath"] = manifest.DefaultMixedEstimatorPath ?? string.Empty,
            ["mixedCompanellaExport"] = manifest.MixedCompanellaExport ?? string.Empty
        };

        var key = JsonSerializer.Serialize(_registryKey, JsonOptions);
        var runtime = JsonSerializer.Serialize(runtimeUrl, JsonOptions);
        var worker = JsonSerializer.Serialize(workerUrl, JsonOptions);
        var config = JsonSerializer.Serialize(configuration, JsonOptions);
        var prefix = JsonSerializer.Serialize(NativeMessagePrefix, JsonOptions);
        var protocol = JsonSerializer.Serialize(_protocol, JsonOptions);
        var protocolVersion = _protocolVersion.ToString();
        var serializedSessionId = JsonSerializer.Serialize(sessionId, JsonOptions);
        var script = """
(async function() {
    const registry = globalThis.__maniaMapAnalyzerOverlayEngines || (globalThis.__maniaMapAnalyzerOverlayEngines = Object.create(null));
    const key = __KEY__;
    const sessionId = __SESSION_ID__;
    const configuration = __CONFIG__;
    const previous = registry[key];
    if (previous?.runtime) {
        try { previous.runtime.dispose(); } catch (exception) { console.error("Disposing previous analyzer runtime failed", exception); }
    }
    const state = { runtime: null, analyze: null, cancel: null, dispose: null };
    registry[key] = state;
    const prefix = __PREFIX__;
    const post = (message) => {
        const body = prefix + JSON.stringify({ ...message, engineId: key, sessionId });
        if (globalThis.chrome?.webview && typeof globalThis.chrome.webview.postMessage === "function") {
            globalThis.chrome.webview.postMessage(body);
            return;
        }
        if (globalThis.external && typeof globalThis.external.notify === "function") {
            globalThis.external.notify(body);
            return;
        }
        throw new Error("The native WebView message bridge is unavailable.");
    };
    try {
        const module = await import(__RUNTIME__);
        const runtime = new module.HeadlessAnalyzerRuntime({
            workerUrl: new URL(__WORKER__, globalThis.location.href),
            configuration: { ...configuration, baseUrl: new URL(configuration.baseUrl, globalThis.location.href).href },
            supersedePending: false,
        });
        state.runtime = runtime;
        state.analyze = (request) => runtime.analyze(request).then(
            (response) => post(response),
            (exception) => post({
                protocol: __PROTOCOL__,
                protocolVersion: __PROTOCOL_VERSION__,
                type: exception?.code === "ANALYSIS_CANCELLED" ? "analysis.cancelled" : "analysis.error",
                correlationId: request?.correlationId || "",
                status: exception?.code === "ANALYSIS_CANCELLED" ? "cancelled" : "error",
                error: { code: exception?.code || "ANALYSIS_FAILED", message: exception?.message || String(exception), details: exception?.details || null },
            }),
        );
        state.cancel = (correlationId, reason) => runtime.cancel(correlationId, reason);
        state.dispose = () => runtime.dispose();
        const ready = await runtime.initialize();
        post(ready);
    } catch (exception) {
        post({
            protocol: __PROTOCOL__,
            protocolVersion: __PROTOCOL_VERSION__,
            type: "runtime.ready",
            status: "error",
            error: { code: exception?.code || "RUNTIME_BOOTSTRAP_FAILED", message: exception?.message || String(exception), details: exception?.details || null },
        });
    }
})();
""";
        return script
            .Replace("__KEY__", key, StringComparison.Ordinal)
            .Replace("__SESSION_ID__", serializedSessionId, StringComparison.Ordinal)
            .Replace("__PREFIX__", prefix, StringComparison.Ordinal)
            .Replace("__RUNTIME__", runtime, StringComparison.Ordinal)
            .Replace("__WORKER__", worker, StringComparison.Ordinal)
            .Replace("__CONFIG__", config, StringComparison.Ordinal)
            .Replace("__PROTOCOL__", protocol, StringComparison.Ordinal)
            .Replace("__PROTOCOL_VERSION__", protocolVersion, StringComparison.Ordinal);
    }

    private string BuildAnalyzeScript(AnalysisRequest request, string correlationId)
    {
        var options = request.Options.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var payload = new
        {
            rawText = request.BeatmapContent,
            requestedAlgorithm = request.RequestedAlgorithm,
            options,
            includeRawResult = false,
            correlationId,
            rate = request.Rate,
            speedRate = request.Rate,
            mods = request.Mods
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var key = JsonSerializer.Serialize(_registryKey, JsonOptions);
        var script = """
(function() {
    const state = globalThis.__maniaMapAnalyzerOverlayEngines?.[__KEY__];
    const request = __REQUEST__;
    if (!state?.analyze) {
        throw new Error("The analyzer runtime is not ready.");
    }
    void state.analyze(request);
})();
""";
        return script
            .Replace("__KEY__", key, StringComparison.Ordinal)
            .Replace("__REQUEST__", json, StringComparison.Ordinal);
    }

    private string BuildCancelScript(string correlationId, string reason)
    {
        var key = JsonSerializer.Serialize(_registryKey, JsonOptions);
        var id = JsonSerializer.Serialize(correlationId, JsonOptions);
        var serializedReason = JsonSerializer.Serialize(reason, JsonOptions);
        var script = """
(function() {
    const state = globalThis.__maniaMapAnalyzerOverlayEngines?.[__KEY__];
    state?.cancel?.(__ID__, __REASON__);
})();
""";
        return script
            .Replace("__KEY__", key, StringComparison.Ordinal)
            .Replace("__ID__", id, StringComparison.Ordinal)
            .Replace("__REASON__", serializedReason, StringComparison.Ordinal);
    }

    private string BuildResetScript()
    {
        var key = JsonSerializer.Serialize(_registryKey, JsonOptions);
        var script = """
(function() {
    const registry = globalThis.__maniaMapAnalyzerOverlayEngines;
    const state = registry?.[__KEY__];
    try { state?.dispose?.(); } finally {
        if (registry) delete registry[__KEY__];
    }
})();
""";
        return script.Replace("__KEY__", key, StringComparison.Ordinal);
    }

    private string BuildResourceUrl(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return _staticRoot + Uri.EscapeDataString(_registryKey) + "/" + string.Join('/', segments);
    }

    private static string BuildAbsoluteRoot(string path) => path;

    private static string NormalizeRoot(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
    }
}
