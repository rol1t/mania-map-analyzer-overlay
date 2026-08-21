using System;
using System.Collections.Generic;
using System.IO;
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
        var runtimeSource = JsonSerializer.Serialize(_package.ReadRuntimeScript(), JsonOptions);
        var protocolSource = JsonSerializer.Serialize(
            ReadProtocolScript(manifest.Runtime),
            JsonOptions);
        var normalizerSource = JsonSerializer.Serialize(
            ReadSiblingScript(manifest.Worker, "normalizer.mjs"),
            JsonOptions);
        var workerSource = JsonSerializer.Serialize(_package.ReadWorkerScript(), JsonOptions);
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
         const runtimeSource = __RUNTIME_SOURCE__;
         const protocolSource = __PROTOCOL_SOURCE__;
         const normalizerSource = __NORMALIZER_SOURCE__;
         const workerSource = __WORKER_SOURCE__;
         const documentUrl = /^https?:/i.test(globalThis.location.href)
             ? globalThis.location.href
             : "http://127.0.0.1:24050/";
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
    const moduleUrls = [];
    const createModuleUrl = (source) => {
        const url = URL.createObjectURL(new Blob([source], { type: "text/javascript" }));
        moduleUrls.push(url);
        return url;
    };
    const revokeModuleUrls = () => {
        while (moduleUrls.length > 0) {
            URL.revokeObjectURL(moduleUrls.pop());
        }
    };
     try {
         const runtimeUrl = new URL(__RUNTIME__, documentUrl).href;
         const loadModuleScript = (sourceUrl) => new Promise((resolve, reject) => {
             const script = document.createElement("script");
             script.type = "module";
             script.src = sourceUrl;
             script.onload = () => resolve(true);
             script.onerror = () => reject(new Error(`Failed to load analyzer runtime module: ${sourceUrl}`));
             (document.head || document.documentElement).appendChild(script);
         });
         const runtimeModule = await (async () => {
             const existing = globalThis.__maniaMapAnalyzerOverlayHeadlessRuntimeModule;
             if (existing?.HeadlessAnalyzerRuntime) {
                 return existing;
             }

             if (!runtimeSource || !protocolSource) {
                 await loadModuleScript(runtimeUrl);
             } else {
                 const protocolBlobUrl = createModuleUrl(protocolSource);
                 const normalizerBlobUrl = normalizerSource
                     ? createModuleUrl(normalizerSource.replace(
                         'from "./protocol.mjs"',
                         `from ${JSON.stringify(protocolBlobUrl)}`))
                     : "";
                 const workerBlobUrl = workerSource
                     ? createModuleUrl(workerSource
                         .replace('from "./protocol.mjs"', `from ${JSON.stringify(protocolBlobUrl)}`)
                         .replace('from "./normalizer.mjs"', `from ${JSON.stringify(normalizerBlobUrl)}`))
                     : __WORKER__;
                 const patchedSource = runtimeSource.replace(
                     'from "./protocol.mjs"',
                     `from ${JSON.stringify(protocolBlobUrl)}`);
                 const runtimeBlobUrl = createModuleUrl(patchedSource);
                 try {
                     await loadModuleScript(runtimeBlobUrl);
                 } finally {
                     URL.revokeObjectURL(runtimeBlobUrl);
                     const runtimeIndex = moduleUrls.indexOf(runtimeBlobUrl);
                     if (runtimeIndex >= 0) {
                         moduleUrls.splice(runtimeIndex, 1);
                     }
                 }
                 state.workerUrl = workerBlobUrl;
             }

             const loaded = globalThis.__maniaMapAnalyzerOverlayHeadlessRuntimeModule;
             if (!loaded?.HeadlessAnalyzerRuntime) {
                 throw new Error("The headless analyzer module loaded without exporting HeadlessAnalyzerRuntime.");
             }

             return loaded;
         })();
         const runtime = new runtimeModule.HeadlessAnalyzerRuntime({
             workerUrl: state.workerUrl || new URL(__WORKER__, documentUrl),
             configuration: { ...configuration, baseUrl: new URL(configuration.baseUrl, documentUrl).href },
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
         state.dispose = () => {
             try {
                 runtime.dispose();
             } finally {
                 revokeModuleUrls();
             }
         };
        const ready = await runtime.initialize();
        post(ready);
    } catch (exception) {
        try {
            state.dispose?.();
        } catch (disposeException) {
            console.error("Disposing failed analyzer runtime failed", disposeException);
        }
        revokeModuleUrls();
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
            .Replace("__RUNTIME_SOURCE__", runtimeSource, StringComparison.Ordinal)
            .Replace("__PROTOCOL_SOURCE__", protocolSource, StringComparison.Ordinal)
            .Replace("__NORMALIZER_SOURCE__", normalizerSource, StringComparison.Ordinal)
            .Replace("__WORKER_SOURCE__", workerSource, StringComparison.Ordinal)
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

    private string ReadProtocolScript(string runtimePath)
    {
        var directory = Path.GetDirectoryName(runtimePath)?.Replace('\\', '/') ?? string.Empty;
        var protocolPath = string.IsNullOrWhiteSpace(directory)
            ? "protocol.mjs"
            : directory + "/protocol.mjs";
        var candidatePath = Path.Combine(_package.PackageDirectory, protocolPath);
        return File.Exists(candidatePath)
            ? File.ReadAllText(_package.ResolveContainedFile(protocolPath)!)
            : string.Empty;
    }

    private string ReadSiblingScript(string modulePath, string siblingName)
    {
        var directory = Path.GetDirectoryName(modulePath)?.Replace('\\', '/') ?? string.Empty;
        var siblingPath = string.IsNullOrWhiteSpace(directory)
            ? siblingName
            : directory + "/" + siblingName;
        var candidatePath = Path.Combine(_package.PackageDirectory, siblingPath);
        return File.Exists(candidatePath)
            ? File.ReadAllText(_package.ResolveContainedFile(siblingPath)!)
            : string.Empty;
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
