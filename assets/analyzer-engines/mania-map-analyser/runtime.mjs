import {
    ANALYSIS_STATUSES,
    MESSAGE_TYPES,
    PROTOCOL,
    PROTOCOL_VERSION,
    createAnalyzeRequest,
    createCancelRequest,
    createConfigureRequest,
    createCorrelationId,
    isProtocolResponse,
} from "./protocol.mjs";

/**
 * Host-side API for one or more widgets.
 *
 * This class owns transport and request lifecycle only. It does not know how
 * a widget is rendered and does not query a document. Supply `workerFactory`
 * in tests or in a host that provides a worker-like postMessage implementation.
 */
export class HeadlessAnalyzerRuntime {
    constructor({
        workerFactory = defaultWorkerFactory,
        workerUrl = new URL("./worker.mjs", import.meta.url),
        configuration = {},
        supersedePending = false,
    } = {}) {
        this._workerFactory = workerFactory;
        this._workerUrl = workerUrl;
        this._configuration = { ...configuration };
        this._supersedePending = supersedePending !== false;
        this._worker = null;
        this._pending = new Map();
        this._disposed = false;
        this._ready = null;
        this._pendingReady = null;
    }

    async initialize() {
        this._ensureUsable();
        if (!this._ready) {
            try {
                const worker = this._workerFactory(this._workerUrl);
                this._worker = worker;
                attachMessageHandler(worker, (message) => this._handleMessage(message, worker));
            } catch (exception) {
                console.error("Creating headless analyzer worker failed", exception);
                throw this._toError("WORKER_CREATE_FAILED", exception);
            }
            this._ready = new Promise((resolve, reject) => {
                this._pendingReady = { resolve, reject };
                try {
                    this._worker.postMessage(createConfigureRequest(this._configuration));
                } catch (exception) {
                    this._pendingReady = null;
                    this._ready = null;
                    this._terminateWorker(this._worker);
                    this._worker = null;
                    reject(this._toError("RUNTIME_CONFIGURE_FAILED", exception));
                }
            });
        }

        return this._ready;
    }

    async analyze({
        rawText,
        requestedAlgorithm = "Mixed",
        options = {},
        rate = 1,
        speedRate = rate,
        mods = [],
        includeRawResult = false,
        correlationId = createCorrelationId(),
        scopeId = null,
        generation = null,
    }) {
        this._ensureUsable();
        await this.initialize();

        const request = createAnalyzeRequest({
            correlationId,
            rawText,
            requestedAlgorithm,
            options,
            rate: speedRate,
            speedRate,
            mods,
            includeRawResult,
            scopeId,
            generation,
        });

        if (this._pending.has(request.correlationId)) {
            throw new HeadlessAnalysisError(
                "DUPLICATE_CORRELATION_ID",
                `An analysis request with correlation ID '${request.correlationId}' is already pending.`,
                { correlationId: request.correlationId },
            );
        }

        if (this._supersedePending) {
            for (const id of this._pending.keys()) {
                this.cancel(id, "superseded by a newer analysis request");
            }
        }

        return new Promise((resolve, reject) => {
            const entry = { resolve, reject };
            this._pending.set(request.correlationId, entry);
            try {
                this._worker.postMessage(request);
            } catch (exception) {
                this._pending.delete(request.correlationId);
                const error = this._toError("ANALYSIS_POST_FAILED", exception);
                console.error("Posting headless analyzer request failed", exception);
                reject(error);
            }
        });
    }

    cancel(correlationId, reason = "cancelled") {
        const id = String(correlationId || "");
        const entry = this._pending.get(id);
        if (!entry || !this._worker) {
            return false;
        }

        this._pending.delete(id);
        entry.reject(new HeadlessAnalysisCancelledError(id, reason));
        try {
            this._worker.postMessage(createCancelRequest(id, reason));
        } catch (exception) {
            console.error("Posting headless analyzer cancellation failed", exception);
        }

        return true;
    }

    dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        for (const [id, entry] of this._pending) {
            entry.reject(new HeadlessAnalysisCancelledError(id, "runtime disposed"));
        }
        this._pending.clear();
        this._pendingReady?.reject(new HeadlessAnalysisCancelledError("", "runtime disposed"));
        this._pendingReady = null;
        this._ready = null;

        try {
            this._worker?.terminate?.();
        } catch (exception) {
            console.error("Terminating headless analyzer worker failed", exception);
        }

        this._worker = null;
    }

    _handleMessage(message, sourceWorker = this._worker) {
        if (sourceWorker !== this._worker) {
            // A terminated worker may still have an already queued message.
            // It must not affect a worker created during reinitialization.
            return;
        }

        if (!isWellFormedWorkerResponse(message)) {
            const error = this._toError(
                "INVALID_WORKER_RESPONSE",
                new Error("Worker sent an invalid protocol response."),
            );
            console.error("Invalid headless analyzer worker response", message);
            this._failRuntime(error, message);
            return;
        }

        if (message.type === MESSAGE_TYPES.Ready) {
            if (message.status === ANALYSIS_STATUSES.Error) {
                const error = new HeadlessAnalysisError(
                    message.error?.code || "RUNTIME_INCOMPATIBLE",
                    message.error?.message || "Headless analyzer runtime is incompatible.",
                    {
                        error: message.error,
                        compatibility: message.compatibility,
                        capabilities: message.capabilities,
                    },
                );
                console.error("Headless analyzer compatibility check failed", message);
                this._pendingReady?.reject(error);
                this._pendingReady = null;
                this._ready = null;
                return;
            }

            this._pendingReady?.resolve(message);
            this._pendingReady = null;
            return;
        }

        if (message.type === MESSAGE_TYPES.Pong) {
            return;
        }

        if (message.type === MESSAGE_TYPES.Error && !message.correlationId) {
            const error = new HeadlessAnalysisError(
                message.error?.code || "RUNTIME_FAILED",
                message.error?.message || "Headless analyzer runtime failed.",
                message.error,
            );
            console.error("Headless analyzer runtime returned an error", message.error);
            this._failRuntime(error, message);
            return;
        }

        const entry = this._pending.get(message.correlationId);
        if (!entry) {
            // A cancelled or superseded response is expected to arrive after
            // the host already rejected its promise; ignore it deliberately.
            return;
        }

        this._pending.delete(message.correlationId);
        if (message.type === MESSAGE_TYPES.Result
            && (message.status === ANALYSIS_STATUSES.Ok || message.status === ANALYSIS_STATUSES.Partial)) {
            entry.resolve(message);
            return;
        }

        if (message.type === MESSAGE_TYPES.Cancelled || message.status === ANALYSIS_STATUSES.Cancelled) {
            entry.reject(new HeadlessAnalysisCancelledError(message.correlationId, message.diagnostics?.[0]?.message));
            return;
        }

        const error = new HeadlessAnalysisError(
            message.error?.code || "ANALYSIS_FAILED",
            message.error?.message || "Headless analysis failed.",
            message.error,
        );
        console.error("Headless analyzer returned an error", message.error);
        entry.reject(error);
    }

    _ensureUsable() {
        if (this._disposed) {
            throw new Error("Headless analyzer runtime has been disposed.");
        }
    }

    _toError(code, exception) {
        return new HeadlessAnalysisError(code, exception?.message || String(exception), {
            code,
            exception,
        });
    }

    _failRuntime(error, message) {
        this._pendingReady?.reject(error);
        this._pendingReady = null;
        this._ready = null;

        for (const [id, entry] of this._pending) {
            this._pending.delete(id);
            entry.reject(error);
        }

        const worker = this._worker;
        this._worker = null;
        this._terminateWorker(worker);

        if (message !== undefined) {
            console.error("Headless analyzer runtime became unusable after a worker protocol failure", message);
        }
    }

    _terminateWorker(worker) {
        try {
            worker?.terminate?.();
        } catch (exception) {
            console.error("Terminating headless analyzer worker failed", exception);
        }
    }
}

export class HeadlessAnalysisError extends Error {
    constructor(code, message, details = null) {
        super(message);
        this.name = "HeadlessAnalysisError";
        this.code = code;
        this.details = details;
    }
}

export class HeadlessAnalysisCancelledError extends HeadlessAnalysisError {
    constructor(correlationId, reason = "cancelled") {
        super("ANALYSIS_CANCELLED", reason || "Analysis cancelled.", { correlationId });
        this.name = "HeadlessAnalysisCancelledError";
    }
}

function defaultWorkerFactory(workerUrl) {
    if (typeof Worker !== "function") {
        throw new HeadlessAnalysisError(
            "WORKER_UNAVAILABLE",
            "This host does not provide Web Worker support; inject a workerFactory.",
        );
    }

    return new Worker(workerUrl, { type: "module" });
}

function attachMessageHandler(worker, handler) {
    if (typeof worker?.addEventListener === "function") {
        worker.addEventListener("message", (event) => handler(event?.data));
        worker.addEventListener("error", (event) => {
            console.error("Headless analyzer worker crashed", event);
            handler(createWorkerCrashResponse(event));
        });
        return;
    }

    if (worker) {
        worker.onmessage = (event) => handler(event?.data);
        worker.onerror = (event) => {
            console.error("Headless analyzer worker crashed", event);
            handler(createWorkerCrashResponse(event));
        };
        return;
    }

    throw new Error("workerFactory returned no worker-like object.");
}

function isWellFormedWorkerResponse(message) {
    if (!isProtocolResponse(message)) {
        return false;
    }

    switch (message.type) {
        case MESSAGE_TYPES.Ready:
            return message.status === ANALYSIS_STATUSES.Ok
                || message.status === ANALYSIS_STATUSES.Partial
                || message.status === ANALYSIS_STATUSES.Error;
        case MESSAGE_TYPES.Pong:
            return message.status === ANALYSIS_STATUSES.Ok;
        case MESSAGE_TYPES.Result:
            return hasCorrelationId(message)
                && (message.status === ANALYSIS_STATUSES.Ok
                    || message.status === ANALYSIS_STATUSES.Partial);
        case MESSAGE_TYPES.Cancelled:
            return hasCorrelationId(message) && message.status === ANALYSIS_STATUSES.Cancelled;
        case MESSAGE_TYPES.Error:
            return message.status === ANALYSIS_STATUSES.Error
                && (message.correlationId === undefined
                    || message.correlationId === null
                    || message.correlationId === ""
                    || hasCorrelationId(message));
        default:
            return false;
    }
}

function hasCorrelationId(message) {
    return typeof message?.correlationId === "string"
        && message.correlationId.trim().length > 0;
}

function createWorkerCrashResponse(event) {
    const filename = String(event?.filename || "").trim();
    const line = Number.isFinite(Number(event?.lineno)) ? Number(event.lineno) : null;
    const column = Number.isFinite(Number(event?.colno)) ? Number(event.colno) : null;
    const cause = String(event?.error?.message || event?.message || "").trim();
    const location = filename
        ? `${filename}${line === null ? "" : `:${line}`}${column === null ? "" : `:${column}`}`
        : "";
    const message = cause || "Headless analyzer worker crashed.";

    return {
        protocol: PROTOCOL,
        protocolVersion: PROTOCOL_VERSION,
        type: MESSAGE_TYPES.Error,
        status: ANALYSIS_STATUSES.Error,
        error: {
            code: "WORKER_CRASHED",
            message: location ? `${message} (${location})` : message,
            stage: "worker",
            details: {
                filename,
                line,
                column,
                stack: String(event?.error?.stack || ""),
            },
        },
    };
}

// EvaluateScriptAsync runs in a classic/eval context on some WebView engines,
// where dynamic import specifiers have no document base URL. The host loads
// this file as a module script and resolves the public constructor through this
// explicit bridge, while the module's internal relative imports remain valid.
globalThis.__maniaMapAnalyzerOverlayHeadlessRuntimeModule = Object.freeze({
    HeadlessAnalyzerRuntime,
});
