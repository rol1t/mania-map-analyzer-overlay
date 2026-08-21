import {
    ANALYSIS_STATUSES,
    MESSAGE_TYPES,
    PROTOCOL,
    PROTOCOL_VERSION,
    assertProtocolMessage,
    createDiagnostic,
    createResponse,
    createStructuredError,
    isRecord,
} from "./protocol.mjs";
import { normalizePipelineResult } from "./normalizer.mjs";

const DEFAULT_PIPELINE_PATH = "js/pipeline/runAnalysisPipeline.js";
const DEFAULT_COMPANELLA_PATH = "js/estimator/companellaEstimator.js";
const DEFAULT_MIXED_ESTIMATOR_PATH = "js/estimator/mixedEstimator.js";
const DEFAULT_PIPELINE_EXPORT = "runAnalysisPipeline";
const DEFAULT_COMPANELLA_EXPORT = "classifyCompanellaDifficulty";
const DEFAULT_MIXED_COMPANELLA_EXPORT = "applyCompanellaToMixedResult";

let configuration = null;
let pipelinePromise = null;
let companellaPromise = null;
let mixedCompanellaPromise = null;
const jobs = new Map();
const latestGenerationByScope = new Map();

globalThis.addEventListener("message", (event) => {
    void handleMessage(event?.data);
});

async function handleMessage(message) {
    try {
        if (!isRecord(message) || message.protocol !== PROTOCOL) {
            throw new Error("Invalid headless analyzer protocol message.");
        }

        if (message.protocolVersion !== PROTOCOL_VERSION) {
            throw new Error(`Unsupported protocol version: ${String(message.protocolVersion)}`);
        }

        switch (message.type) {
            case MESSAGE_TYPES.Configure:
                await handleConfigure(message.config);
                return;
            case MESSAGE_TYPES.Ping:
                post(createResponse({
                    type: MESSAGE_TYPES.Pong,
                    status: ANALYSIS_STATUSES.Ok,
                    config: publicConfiguration(),
                }));
                return;
            case MESSAGE_TYPES.Cancel:
                handleCancel(assertProtocolMessage(message, MESSAGE_TYPES.Cancel));
                return;
            case MESSAGE_TYPES.Analyze:
                await handleAnalyze(message);
                return;
            default:
                throw new Error(`Unsupported headless analyzer message type: ${String(message.type || "missing")}`);
        }
    } catch (exception) {
        reportException("Processing headless analyzer message", exception);
        post(createResponse({
            type: MESSAGE_TYPES.Error,
            status: ANALYSIS_STATUSES.Error,
            error: createStructuredError({
                code: "PROTOCOL_MESSAGE_FAILED",
                message: exception?.message || "Protocol message failed.",
                stage: "protocol",
                exception,
            }),
        }));
    }
}

async function handleConfigure(config) {
    if (!isRecord(config)) {
        throw new TypeError("runtime.configure requires a configuration object.");
    }

    const next = normalizeConfiguration(config);
    if (!configuration || JSON.stringify(configuration) !== JSON.stringify(next)) {
        cancelAllJobs("runtime reconfigured");
        pipelinePromise = null;
        companellaPromise = null;
        mixedCompanellaPromise = null;
    }

    configuration = next;

    try {
        await loadPipeline();
    } catch (exception) {
        reportException("Probing ManiaMapAnalyser pipeline", exception);
        const error = createStructuredError({
            code: exception?.code || "PIPELINE_COMPATIBILITY_FAILED",
            message: exception?.message || "The installed MMA pipeline is incompatible.",
            stage: exception?.stage || "pipeline-import",
            exception,
            retryable: exception?.retryable === true,
        });
        const diagnostic = createDiagnostic({
            code: error.code,
            message: error.message,
            stage: error.stage,
            severity: "error",
        });
        post(createResponse({
            type: MESSAGE_TYPES.Ready,
            status: ANALYSIS_STATUSES.Error,
            config: publicConfiguration(),
            capabilities: createEffectiveCapabilities(false, false, false),
            compatibility: {
                status: "incompatible",
                diagnostics: [diagnostic],
            },
            diagnostics: [diagnostic],
            error,
        }));
        return;
    }

    const compatibilityDiagnostics = [];
    let companellaAvailable = false;
    let mixedCompanellaAvailable = false;
    try {
        await loadCompanellaClassifier();
        companellaAvailable = true;
    } catch (exception) {
        reportException("Probing Companella classifier", exception);
        compatibilityDiagnostics.push(createDiagnostic({
            code: exception?.code || "COMPANELLA_API_UNAVAILABLE",
            message: exception?.message || "Companella classifier is unavailable.",
            stage: exception?.stage || "companella-import",
            severity: "warning",
        }));
    }

    try {
        await loadMixedCompanellaFinalizer();
        mixedCompanellaAvailable = true;
    } catch (exception) {
        reportException("Probing Mixed Companella finalizer", exception);
        compatibilityDiagnostics.push(createDiagnostic({
            code: exception?.code || "MIXED_COMPANELLA_API_UNAVAILABLE",
            message: exception?.message || "Mixed Companella finalizer is unavailable.",
            stage: exception?.stage || "mixed-companella-import",
            severity: "warning",
        }));
    }

    const fullyCompanellaCompatible = companellaAvailable && mixedCompanellaAvailable;
    post(createResponse({
        type: MESSAGE_TYPES.Ready,
        status: compatibilityDiagnostics.length > 0
            ? ANALYSIS_STATUSES.Partial
            : ANALYSIS_STATUSES.Ok,
        config: publicConfiguration(),
        capabilities: createEffectiveCapabilities(
            true,
            companellaAvailable,
            fullyCompanellaCompatible,
        ),
        compatibility: {
            status: compatibilityDiagnostics.length > 0 ? "degraded" : "compatible",
            diagnostics: compatibilityDiagnostics,
        },
        diagnostics: compatibilityDiagnostics,
    }));
}

function handleCancel(message) {
    const correlationId = String(message.correlationId || "");
    const job = jobs.get(correlationId);
    if (!job) {
        return;
    }

    job.cancelled = true;
    jobs.delete(correlationId);
    post(createResponse({
        type: MESSAGE_TYPES.Cancelled,
        correlationId,
        status: ANALYSIS_STATUSES.Cancelled,
        requestedAlgorithm: job.requestedAlgorithm,
        actualAlgorithm: null,
        diagnostics: [createDiagnostic({
            code: "ANALYSIS_CANCELLED",
            message: String(message.reason || "Analysis cancelled."),
            stage: "runtime",
            severity: "info",
        })],
    }));
}

async function handleAnalyze(message) {
    const request = assertProtocolMessage(message, MESSAGE_TYPES.Analyze);
    const correlationId = request.correlationId;
    const scopeId = String(request.scopeId || "").trim() || null;
    const generation = Number.isSafeInteger(request.generation) && request.generation >= 0
        ? request.generation
        : null;

    if (scopeId && generation !== null) {
        const latest = latestGenerationByScope.get(scopeId);
        if (Number.isSafeInteger(latest) && generation < latest) {
            post(createResponse({
                type: MESSAGE_TYPES.Cancelled,
                correlationId,
                status: ANALYSIS_STATUSES.Cancelled,
                requestedAlgorithm: request.requestedAlgorithm,
                diagnostics: [createDiagnostic({
                    code: "ANALYSIS_STALE_GENERATION",
                    message: `Generation ${generation} is older than active generation ${latest} for scope ${scopeId}.`,
                    stage: "runtime",
                    severity: "info",
                })],
            }));
            return;
        }

        if (!Number.isSafeInteger(latest) || generation > latest) {
            latestGenerationByScope.set(scopeId, generation);
            cancelOlderScopeJobs(scopeId, generation);
        }
    }

    const previous = jobs.get(correlationId);
    if (previous) {
        previous.cancelled = true;
        jobs.delete(correlationId);
        post(createResponse({
            type: MESSAGE_TYPES.Cancelled,
            correlationId,
            status: ANALYSIS_STATUSES.Cancelled,
            requestedAlgorithm: previous.requestedAlgorithm,
            diagnostics: [createDiagnostic({
                code: "ANALYSIS_CORRELATION_REUSED",
                message: "A newer request reused the same correlation ID.",
                stage: "runtime",
                severity: "info",
            })],
        }));
    }

    const job = {
        generation,
        scopeId,
        requestedAlgorithm: request.requestedAlgorithm,
        cancelled: false,
    };
    jobs.set(correlationId, job);

    try {
        const pipeline = await loadPipeline();
        throwIfStale(correlationId, job);

        const pipelineOptions = preparePipelineOptions(request.requestedAlgorithm, request.options);
        let pipelineResult = await pipeline({
            rawText: request.rawText,
            estimatorAlgorithm: request.requestedAlgorithm,
            options: pipelineOptions,
            rate: request.rate,
            speedRate: request.speedRate ?? request.rate,
            mods: request.mods,
        });
        throwIfStale(correlationId, job);

        let companella = null;
        const diagnostics = [];
        const actualAlgorithm = String(
            pipelineResult?.actualEstimatorAlgorithm
                || pipelineResult?.actualAlgorithm
                || request.requestedAlgorithm,
        );
        if (request.requestedAlgorithm === "Companella" || actualAlgorithm === "Companella") {
            const postProcessing = await runCompanellaPostProcessing(pipelineResult, request.requestedAlgorithm);
            throwIfStale(correlationId, job);
            companella = postProcessing.companella;
            pipelineResult = postProcessing.pipelineResult;
            diagnostics.push(...postProcessing.diagnostics);
        }

        const normalized = normalizePipelineResult(pipelineResult, {
            requestedAlgorithm: request.requestedAlgorithm,
            includeRawResult: request.includeRawResult === true,
            companella,
        });
        diagnostics.push(...normalized.diagnostics);
        normalized.analysis.diagnostics = diagnostics;
        for (const diagnostic of diagnostics) {
            if (diagnostic.severity === "error") {
                console.error("Headless analyzer diagnostic", diagnostic);
            }
        }

        jobs.delete(correlationId);
        const status = diagnostics.some((entry) => entry.severity === "error")
            ? ANALYSIS_STATUSES.Partial
            : diagnostics.length > 0
                ? ANALYSIS_STATUSES.Partial
                : ANALYSIS_STATUSES.Ok;
        post(createResponse({
            type: MESSAGE_TYPES.Result,
            correlationId,
            status,
            requestedAlgorithm: normalized.analysis.requestedAlgorithm,
            actualAlgorithm: normalized.analysis.actualAlgorithm,
            analysis: normalized.analysis,
            diagnostics,
        }));
    } catch (exception) {
        const stale = exception?.code === "STALE_ANALYSIS" || isStaleJob(correlationId, job);
        if (stale) {
            jobs.delete(correlationId);
            return;
        }

        jobs.delete(correlationId);
        reportException(`Headless analysis ${correlationId}`, exception);
        post(createResponse({
            type: MESSAGE_TYPES.Error,
            correlationId,
            status: ANALYSIS_STATUSES.Error,
            requestedAlgorithm: request.requestedAlgorithm,
            error: createStructuredError({
                code: exception?.code || "ANALYSIS_FAILED",
                message: exception?.message || "Headless analysis failed.",
                stage: exception?.stage || "pipeline",
                exception,
                retryable: exception?.retryable === true,
            }),
        }));
    }
}

async function loadPipeline() {
    if (!configuration) {
        throw createRuntimeError(
            "RUNTIME_NOT_CONFIGURED",
            "Headless analyzer runtime must be configured with baseUrl before analysis.",
            "runtime",
        );
    }

    if (!pipelinePromise) {
        const url = resolveModuleUrl(configuration.baseUrl, configuration.pipelinePath);
        pipelinePromise = import(url.toString())
            .then((module) => resolveFunction(module, configuration.pipelineExport, "runAnalysisPipeline"))
            .catch((exception) => {
                pipelinePromise = null;
                throw createRuntimeError(
                    "PIPELINE_IMPORT_FAILED",
                    `Unable to load ManiaMapAnalyser pipeline from ${url.toString()}.`,
                    "pipeline-import",
                    exception,
                    true,
                );
            });
    }

    return pipelinePromise;
}

async function runCompanellaPostProcessing(pipelineResult, requestedAlgorithm) {
    const diagnostics = [];
    let classifier;
    try {
        classifier = await loadCompanellaClassifier();
    } catch (exception) {
        reportException("Loading Companella post-processing", exception);
        return {
            companella: null,
            pipelineResult,
            diagnostics: [createDiagnostic({
                code: "COMPANELLA_POST_PROCESSING_UNAVAILABLE",
                message: exception?.message || "Companella classifier is unavailable in this MMA version.",
                stage: "companella-post-processing",
                severity: "error",
                details: {
                    classifierPath: configuration?.companellaPath || DEFAULT_COMPANELLA_PATH,
                    requestedAlgorithm,
                },
            })],
        };
    }

    try {
        const values = selectEtternaValues(pipelineResult);
        const companella = await classifier({
            msdValues: values,
            interludeStar: pipelineResult?.interludeStar,
            sunnyStar: pipelineResult?.sunnyStar ?? pipelineResult?.rework?.star,
        });

        if (requestedAlgorithm === "Mixed" && pipelineResult?.rework?.mixedCompanellaPlan) {
            try {
                const applyCompanellaToMixedResult = await loadMixedCompanellaFinalizer();
                const rework = applyCompanellaToMixedResult(pipelineResult.rework, companella);
                return {
                    companella,
                    pipelineResult: { ...pipelineResult, rework },
                    diagnostics,
                };
            } catch (exception) {
                reportException("Finalizing Mixed Companella result", exception);
                diagnostics.push(createDiagnostic({
                    code: "MIXED_COMPANELLA_FINALIZER_UNAVAILABLE",
                    message: exception?.message || "Mixed Companella finalizer is unavailable in this MMA version.",
                    stage: "mixed-companella-post-processing",
                    severity: "error",
                    details: {
                        finalizerPath: configuration?.mixedEstimatorPath || DEFAULT_MIXED_ESTIMATOR_PATH,
                        finalizerExport: configuration?.mixedCompanellaExport || DEFAULT_MIXED_COMPANELLA_EXPORT,
                    },
                }));
                return { companella, pipelineResult, diagnostics };
            }
        }

        const rework = {
            ...pipelineResult.rework,
            estDiff: companella.estDiff,
            numericDifficulty: companella.numericDifficulty,
            numericDifficultyHint: companella.numericDifficultyHint,
        };
        return {
            companella,
            pipelineResult: { ...pipelineResult, rework },
            diagnostics,
        };
    } catch (exception) {
        reportException("Running Companella post-processing", exception);
        return {
            companella: null,
            pipelineResult,
            diagnostics: [createDiagnostic({
                code: "COMPANELLA_POST_PROCESSING_FAILED",
                message: exception?.message || "Companella post-processing failed.",
                stage: "companella-post-processing",
                severity: "error",
            })],
        };
    }
}

async function loadMixedCompanellaFinalizer() {
    if (!configuration) {
        throw createRuntimeError("RUNTIME_NOT_CONFIGURED", "Runtime is not configured.", "runtime");
    }

    if (!mixedCompanellaPromise) {
        const url = resolveModuleUrl(configuration.baseUrl, configuration.mixedEstimatorPath);
        mixedCompanellaPromise = import(url.toString())
            .then((module) => resolveFunction(
                module,
                configuration.mixedCompanellaExport,
                DEFAULT_MIXED_COMPANELLA_EXPORT,
            ))
            .catch((exception) => {
                mixedCompanellaPromise = null;
                throw createRuntimeError(
                    "MIXED_COMPANELLA_API_UNAVAILABLE",
                    `Installed MMA version does not expose ${configuration.mixedCompanellaExport} at ${url.toString()}.`,
                    "mixed-companella-import",
                    exception,
                );
            });
    }

    return mixedCompanellaPromise;
}

async function loadCompanellaClassifier() {
    if (!configuration) {
        throw createRuntimeError("RUNTIME_NOT_CONFIGURED", "Runtime is not configured.", "runtime");
    }

    if (!companellaPromise) {
        const url = resolveModuleUrl(configuration.baseUrl, configuration.companellaPath);
        companellaPromise = import(url.toString())
            .then((module) => resolveFunction(module, configuration.companellaExport, "classifyCompanellaDifficulty"))
            .catch((exception) => {
                companellaPromise = null;
                throw createRuntimeError(
                    "COMPANELLA_API_UNAVAILABLE",
                    `Installed MMA version does not expose ${configuration.companellaExport} at ${url.toString()}.`,
                    "companella-import",
                    exception,
                );
            });
    }

    return companellaPromise;
}

function selectEtternaValues(result) {
    if (isRecord(result?.companellaEttResult) && isRecord(result.companellaEttResult.values)) {
        return result.companellaEttResult.values;
    }

    if (isRecord(result?.ettResult) && isRecord(result.ettResult.values)) {
        return result.ettResult.values;
    }

    return null;
}

function preparePipelineOptions(requestedAlgorithm, options) {
    const preserved = isRecord(options) ? { ...options } : {};
    if (requestedAlgorithm === "Companella" || requestedAlgorithm === "Mixed") {
        // These stages provide the ten features required by the Companella
        // classifier. MMA's UI enables them implicitly for the same profiles.
        preserved.withEtterna = true;
        preserved.withInterlude = true;
    }

    return preserved;
}

function resolveFunction(module, configuredName, fallbackName) {
    const name = String(configuredName || fallbackName);
    const candidate = module?.[name] || module?.default?.[name] || (name === fallbackName ? module?.default : null);
    if (typeof candidate !== "function") {
        throw createRuntimeError(
            "MODULE_EXPORT_MISSING",
            `Module does not export the required function ${name}.`,
            "module-contract",
        );
    }

    return candidate;
}

function normalizeConfiguration(config) {
    const baseUrl = String(config.baseUrl || "").trim();
    if (!baseUrl) {
        throw createRuntimeError("BASE_URL_REQUIRED", "baseUrl is required for dynamic MMA imports.", "runtime");
    }

    return {
        baseUrl,
        pipelinePath: String(config.pipelinePath || DEFAULT_PIPELINE_PATH),
        pipelineExport: String(config.pipelineExport || DEFAULT_PIPELINE_EXPORT),
        companellaPath: String(config.companellaPath || DEFAULT_COMPANELLA_PATH),
        companellaExport: String(config.companellaExport || DEFAULT_COMPANELLA_EXPORT),
        mixedEstimatorPath: String(config.mixedEstimatorPath || DEFAULT_MIXED_ESTIMATOR_PATH),
        mixedCompanellaExport: String(config.mixedCompanellaExport || DEFAULT_MIXED_COMPANELLA_EXPORT),
    };
}

function resolveModuleUrl(baseUrl, path) {
    try {
        return new URL(path, ensureTrailingSlash(baseUrl));
    } catch (exception) {
        throw createRuntimeError(
            "MODULE_URL_INVALID",
            `Cannot resolve analyzer module URL from baseUrl ${baseUrl}.`,
            "module-import",
            exception,
        );
    }
}

function ensureTrailingSlash(value) {
    return value.endsWith("/") ? value : `${value}/`;
}

function throwIfStale(correlationId, job) {
    if (isStaleJob(correlationId, job)) {
        const exception = new Error("Analysis request is stale or cancelled.");
        exception.code = "STALE_ANALYSIS";
        throw exception;
    }
}

function isStaleJob(correlationId, job) {
    if (!job || jobs.get(correlationId) !== job || job.cancelled) {
        return true;
    }

    if (job.scopeId && job.generation !== null) {
        const latest = latestGenerationByScope.get(job.scopeId);
        return Number.isSafeInteger(latest) && job.generation < latest;
    }

    return false;
}

function cancelOlderScopeJobs(scopeId, generation) {
    for (const [correlationId, job] of jobs) {
        if (job.scopeId !== scopeId || job.generation === null || job.generation >= generation) {
            continue;
        }

        job.cancelled = true;
        jobs.delete(correlationId);
        post(createResponse({
            type: MESSAGE_TYPES.Cancelled,
            correlationId,
            status: ANALYSIS_STATUSES.Cancelled,
            requestedAlgorithm: job.requestedAlgorithm,
            diagnostics: [createDiagnostic({
                code: "ANALYSIS_SUPERSEDED",
                message: `Generation ${generation} superseded generation ${job.generation} for scope ${scopeId}.`,
                stage: "runtime",
                severity: "info",
            })],
        }));
    }
}

function cancelAllJobs(reason) {
    for (const [correlationId, job] of jobs) {
        job.cancelled = true;
        post(createResponse({
            type: MESSAGE_TYPES.Cancelled,
            correlationId,
            status: ANALYSIS_STATUSES.Cancelled,
            requestedAlgorithm: job.requestedAlgorithm,
            diagnostics: [createDiagnostic({
                code: "ANALYSIS_CANCELLED",
                message: reason,
                stage: "runtime",
                severity: "info",
            })],
        }));
    }
    jobs.clear();
    latestGenerationByScope.clear();
}

function publicConfiguration() {
    return configuration ? { ...configuration } : null;
}

function createEffectiveCapabilities(pipeline, companellaDirect, companella) {
    const algorithms = ["Sunny", "Daniel", "Azusa", "Roxy", "Mixed"];
    if (companella) {
        algorithms.push("Companella");
    }

    return {
        pipeline,
        algorithms,
        companella,
        companellaDirect,
        mixedCompanella: companella,
    };
}

function createRuntimeError(code, message, stage, cause = null, retryable = false) {
    const exception = new Error(message);
    exception.code = code;
    exception.stage = stage;
    exception.retryable = retryable;
    if (cause) {
        exception.cause = cause;
    }
    return exception;
}

function reportException(operation, exception) {
    console.error(operation, exception);
}

function post(message) {
    try {
        globalThis.postMessage(message);
    } catch (exception) {
        reportException("Posting headless analyzer response", exception);
    }
}
