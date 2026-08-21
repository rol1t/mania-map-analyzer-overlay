/**
 * Versioned, DOM-free protocol shared by the host runtime and the worker.
 *
 * Request lifecycle:
 *   host -> { type: "runtime.configure" }
 *   host -> { type: "analysis.request", correlationId, ... }
 *   host -> { type: "analysis.cancel", correlationId }
 *   worker -> { type: "runtime.ready" | "analysis.result" |
 *               "analysis.error" | "analysis.cancelled" }
 *
 * A correlationId is mandatory on every analysis message. The worker may
 * finish stale work internally, but it must never publish a result for a
 * request that has been cancelled or superseded.
 */

export const PROTOCOL = "mania-map-analyser.headless";
export const PROTOCOL_VERSION = 1;

export const MESSAGE_TYPES = Object.freeze({
    Configure: "runtime.configure",
    Ping: "runtime.ping",
    Ready: "runtime.ready",
    Pong: "runtime.pong",
    Analyze: "analysis.request",
    Result: "analysis.result",
    Error: "analysis.error",
    Cancel: "analysis.cancel",
    Cancelled: "analysis.cancelled",
});

export const ANALYSIS_STATUSES = Object.freeze({
    Ok: "ok",
    Partial: "partial",
    Error: "error",
    Cancelled: "cancelled",
});

export function createCorrelationId(prefix = "analysis") {
    const timestamp = Date.now().toString(36);
    const random = Math.random().toString(36).slice(2, 10);
    return `${prefix}-${timestamp}-${random}`;
}

export function createAnalyzeRequest({
    correlationId = createCorrelationId(),
    rawText,
    requestedAlgorithm = "Mixed",
    options = {},
    rate = 1,
    mods = [],
    includeRawResult = false,
    scopeId = null,
    generation = null,
}) {
    const speedRate = Number.isFinite(Number(rate)) && Number(rate) > 0 ? Number(rate) : 1;
    return {
        protocol: PROTOCOL,
        protocolVersion: PROTOCOL_VERSION,
        type: MESSAGE_TYPES.Analyze,
        correlationId,
        rawText: String(rawText ?? ""),
        requestedAlgorithm: String(requestedAlgorithm || "Mixed"),
        options: isRecord(options) ? { ...options } : {},
        rate: speedRate,
        speedRate,
        mods: Array.isArray(mods)
            ? mods.map((mod) => String(mod || "").trim().toUpperCase()).filter(Boolean)
            : [],
        includeRawResult: includeRawResult === true,
        ...(String(scopeId || "").trim() ? { scopeId: String(scopeId).trim() } : {}),
        ...(Number.isSafeInteger(generation) && generation >= 0 ? { generation } : {}),
    };
}

export function createCancelRequest(correlationId, reason = "cancelled") {
    return {
        protocol: PROTOCOL,
        protocolVersion: PROTOCOL_VERSION,
        type: MESSAGE_TYPES.Cancel,
        correlationId: String(correlationId || ""),
        reason: String(reason || "cancelled"),
    };
}

export function createConfigureRequest(config = {}) {
    return {
        protocol: PROTOCOL,
        protocolVersion: PROTOCOL_VERSION,
        type: MESSAGE_TYPES.Configure,
        config: isRecord(config) ? { ...config } : {},
    };
}

export function createResponse({
    type,
    correlationId = "",
    status,
    requestedAlgorithm = null,
    actualAlgorithm = null,
    analysis = null,
    diagnostics = [],
    error = null,
    config = null,
    capabilities = null,
    compatibility = null,
}) {
    return {
        protocol: PROTOCOL,
        protocolVersion: PROTOCOL_VERSION,
        type,
        correlationId: String(correlationId || ""),
        status: status || null,
        requestedAlgorithm,
        actualAlgorithm,
        analysis,
        diagnostics: Array.isArray(diagnostics) ? diagnostics : [],
        error,
        ...(config ? { config } : {}),
        ...(capabilities ? { capabilities } : {}),
        ...(compatibility ? { compatibility } : {}),
    };
}

export function createStructuredError({
    code = "ANALYSIS_FAILED",
    message = "Analysis failed.",
    stage = "runtime",
    exception = null,
    retryable = false,
    details = null,
}) {
    const result = {
        code: String(code),
        message: String(message),
        stage: String(stage),
        retryable: retryable === true,
    };

    if (details !== null && details !== undefined) {
        result.details = details;
    }

    if (exception) {
        result.exception = serializeException(exception);
    }

    return result;
}

export function createDiagnostic({
    code,
    message,
    stage,
    severity = "warning",
    details = null,
}) {
    const diagnostic = {
        code: String(code || "ANALYSIS_DIAGNOSTIC"),
        message: String(message || "Analysis diagnostic."),
        stage: String(stage || "runtime"),
        severity: String(severity || "warning"),
    };

    if (details !== null && details !== undefined) {
        diagnostic.details = details;
    }

    return diagnostic;
}

export function assertProtocolMessage(message, expectedType = null) {
    if (!isRecord(message)) {
        throw new TypeError("Protocol message must be an object.");
    }

    if (message.protocol !== PROTOCOL) {
        throw new Error(`Unsupported protocol: ${String(message.protocol || "missing")}`);
    }

    if (message.protocolVersion !== PROTOCOL_VERSION) {
        throw new Error(`Unsupported protocol version: ${String(message.protocolVersion)}`);
    }

    if (expectedType && message.type !== expectedType) {
        throw new Error(`Unexpected protocol message type: ${String(message.type || "missing")}`);
    }

    if ((message.type === MESSAGE_TYPES.Analyze || message.type === MESSAGE_TYPES.Cancel)
        && !String(message.correlationId || "").trim()) {
        throw new Error("Analysis protocol messages require a correlationId.");
    }

    if (message.type === MESSAGE_TYPES.Analyze && !String(message.rawText || "").trim()) {
        throw new Error("Analysis requests require rawText containing an .osu file.");
    }

    if (message.type === MESSAGE_TYPES.Analyze &&
        (!Number.isFinite(Number(message.speedRate ?? message.rate)) || Number(message.speedRate ?? message.rate) <= 0)) {
        throw new Error("Analysis requests require a finite positive rate.");
    }

    return message;
}

export function isProtocolResponse(message) {
    return isRecord(message)
        && message.protocol === PROTOCOL
        && message.protocolVersion === PROTOCOL_VERSION
        && Object.values(MESSAGE_TYPES).includes(message.type);
}

export function serializeException(exception) {
    if (exception instanceof Error) {
        return {
            name: exception.name,
            message: exception.message,
            stack: exception.stack || "",
        };
    }

    if (isRecord(exception)) {
        return {
            name: String(exception.name || "Error"),
            message: String(exception.message || exception.toString?.() || "Unknown error"),
            stack: String(exception.stack || ""),
        };
    }

    return {
        name: "Error",
        message: String(exception || "Unknown error"),
        stack: "",
    };
}

export function isRecord(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}
