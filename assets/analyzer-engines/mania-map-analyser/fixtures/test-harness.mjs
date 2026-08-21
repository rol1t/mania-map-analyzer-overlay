import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
    MESSAGE_TYPES,
    assertProtocolMessage,
    createAnalyzeRequest,
    createConfigureRequest,
    createResponse,
    isProtocolResponse,
} from "../protocol.mjs";
import { normalizePipelineResult } from "../normalizer.mjs";
import {
    HeadlessAnalysisError,
    HeadlessAnalyzerRuntime,
} from "../runtime.mjs";

const directory = path.dirname(fileURLToPath(import.meta.url));
const fixtureRequest = JSON.parse(fs.readFileSync(path.join(directory, "request.json"), "utf8"));
const fixtureResponse = JSON.parse(fs.readFileSync(path.join(directory, "response.json"), "utf8"));

class RuntimeFixtureWorker {
    constructor(name) {
        this.name = name;
        this.messages = [];
        this._listeners = new Map();
        this.terminated = false;
    }

    addEventListener(type, handler) {
        const handlers = this._listeners.get(type) || [];
        handlers.push(handler);
        this._listeners.set(type, handlers);
    }

    postMessage(message) {
        if (this.terminated) {
            throw new Error(`Fixture worker '${this.name}' is terminated.`);
        }
        this.messages.push(message);
    }

    emit(message) {
        for (const handler of this._listeners.get("message") || []) {
            handler({ data: message });
        }
    }

    terminate() {
        this.terminated = true;
    }
}

assertProtocolMessage(fixtureRequest, MESSAGE_TYPES.Analyze);
assert.equal(isProtocolResponse(fixtureResponse), true);

const request = createAnalyzeRequest({
    correlationId: fixtureRequest.correlationId,
    rawText: fixtureRequest.rawText,
    requestedAlgorithm: fixtureRequest.requestedAlgorithm,
    options: fixtureRequest.options,
});
assert.equal(request.protocolVersion, fixtureRequest.protocolVersion);
assert.equal(request.correlationId, fixtureRequest.correlationId);

const normalized = normalizePipelineResult({
    rework: {
        star: 5.17,
        lnRatio: 0.514,
        columnCount: 4,
        estDiff: "Reform 4 mid/high || LN 6 mid/high",
        numericDifficulty: null,
    },
    actualEstimatorAlgorithm: "Roxy",
    parsedSummary: {
        metadata: {},
        lnRatio: 0.514,
        columnCount: 4,
    },
    ettResult: {
        values: {
            Stream: 19,
        },
    },
    companellaEttResult: {
        values: {
            Stream: 99,
        },
    },
    patternReport: {
        LNPercent: 0.514,
    },
    interludeStar: Number.NaN,
    errors: [],
}, {
    requestedAlgorithm: "Mixed",
});

assert.equal(normalized.analysis.metrics["algorithm.requested"].value, "Mixed");
assert.equal(normalized.analysis.metrics["algorithm.actual"].value, "Roxy");
assert.equal(normalized.analysis.metrics["difficulty.lnPercent"].value, 51.4);
assert.equal(normalized.analysis.metrics["pattern.lnPercent"].value, 51.4);
assert.equal(normalized.analysis.metrics["skills.stream"].value, 19);
assert.equal(normalized.analysis.metrics["dan.ln.label"].value, "LN 6 mid/high");

const missingNumbers = normalizePipelineResult({
    rework: {
        star: null,
        lnRatio: "",
        columnCount: null,
    },
    parsedSummary: {
        lnRatio: null,
        columnCount: "",
    },
    patternReport: {
        LNPercent: null,
    },
    errors: [],
}, {
    requestedAlgorithm: "Sunny",
});
assert.equal("difficulty.star" in missingNumbers.analysis.metrics, false);
assert.equal("difficulty.lnPercent" in missingNumbers.analysis.metrics, false);
assert.equal("difficulty.keys" in missingNumbers.analysis.metrics, false);
assert.equal("pattern.lnPercent" in missingNumbers.analysis.metrics, false);

const postedMessages = [];
const waiters = [];
let workerMessageHandler = null;

globalThis.addEventListener = (type, handler) => {
    if (type === "message") {
        workerMessageHandler = handler;
    }
};
globalThis.postMessage = (message) => {
    postedMessages.push(message);
    for (const waiter of [...waiters]) {
        if (waiter.predicate(message)) {
            waiter.resolve(message);
            waiters.splice(waiters.indexOf(waiter), 1);
        }
    }
};

await import(`../worker.mjs?fixture=${Date.now()}`);
assert.equal(typeof workerMessageHandler, "function");

const fixtureBaseUrl = new URL("./", import.meta.url).toString();
workerMessageHandler({
    data: createConfigureRequest({
        baseUrl: fixtureBaseUrl,
        pipelinePath: "fake-pipeline.mjs",
        companellaPath: "fake-companella.mjs",
        mixedEstimatorPath: "fake-mixed.mjs",
    }),
});
const ready = await waitFor((message) => message.type === MESSAGE_TYPES.Ready);
assert.equal(ready.status, "ok");
assert.equal(ready.capabilities.pipeline, true);
assert.equal(ready.capabilities.companella, true);
assert.equal(ready.capabilities.companellaDirect, true);
assert.equal(ready.capabilities.mixedCompanella, true);

const firstCorrelationId = "concurrent-slow";
const secondCorrelationId = "concurrent-fast";
workerMessageHandler({
    data: createAnalyzeRequest({
        correlationId: firstCorrelationId,
        rawText: "slow map",
        requestedAlgorithm: "Sunny",
        options: { delayMs: 60 },
    }),
});
workerMessageHandler({
    data: createAnalyzeRequest({
        correlationId: secondCorrelationId,
        rawText: "fast map",
        requestedAlgorithm: "Daniel",
        options: { delayMs: 5 },
    }),
});

const [firstResult, secondResult] = await Promise.all([
    waitFor((message) => message.type === MESSAGE_TYPES.Result
        && message.correlationId === firstCorrelationId),
    waitFor((message) => message.type === MESSAGE_TYPES.Result
        && message.correlationId === secondCorrelationId),
]);
assert.equal(firstResult.actualAlgorithm, "Sunny");
assert.equal(secondResult.actualAlgorithm, "Daniel");
assert.equal(
    postedMessages.some((message) => message.type === MESSAGE_TYPES.Cancelled
        && (message.correlationId === firstCorrelationId || message.correlationId === secondCorrelationId)),
    false,
);

const typedOptions = {
    speedRate: 1.25,
    withEtterna: true,
    label: "typed-options",
    tags: ["DT", "Mirror"],
    nested: {
        enabled: false,
        threshold: 3,
    },
    nullable: null,
};
const directCorrelationId = "companella-direct";
workerMessageHandler({
    data: createAnalyzeRequest({
        correlationId: directCorrelationId,
        rawText: "direct Companella map",
        requestedAlgorithm: "Companella",
        options: typedOptions,
        includeRawResult: true,
    }),
});
const directResult = await waitFor((message) => message.type === MESSAGE_TYPES.Result
    && message.correlationId === directCorrelationId);
assert.equal(directResult.status, "ok");
assert.equal(directResult.analysis.metrics["difficulty.label"].value, "Reform 7 mid/high");
assert.equal(directResult.analysis.metrics["difficulty.numeric"].value, 7.25);
assert.deepEqual(directResult.analysis.rawResult.receivedOptions, {
    ...typedOptions,
    withEtterna: true,
    withInterlude: true,
});

const mixedCorrelationId = "companella-mixed";
workerMessageHandler({
    data: createAnalyzeRequest({
        correlationId: mixedCorrelationId,
        rawText: "Mixed Companella map",
        requestedAlgorithm: "Mixed",
        options: {
            actualAlgorithm: "Companella",
        },
        includeRawResult: true,
    }),
});
const mixedResult = await waitFor((message) => message.type === MESSAGE_TYPES.Result
    && message.correlationId === mixedCorrelationId);
assert.equal(mixedResult.status, "ok");
assert.equal(mixedResult.actualAlgorithm, "Companella");
assert.equal(
    mixedResult.analysis.metrics["difficulty.label"].value,
    "Reform 7 mid/high || LN 6 mid/high",
);
assert.equal(mixedResult.analysis.metrics["difficulty.numeric"].value, 7.25);
assert.equal(mixedResult.analysis.rawResult.rework.mixedMergeApplied, true);
assert.equal(mixedResult.analysis.rawResult.rework.mixedCompanellaPlan, null);

workerMessageHandler({
    data: createConfigureRequest({
        baseUrl: fixtureBaseUrl,
        pipelinePath: "fake-pipeline.mjs",
        companellaPath: "fake-missing-exports.mjs",
        mixedEstimatorPath: "fake-missing-exports.mjs",
    }),
});
const degradedReady = await waitFor((message) => message.type === MESSAGE_TYPES.Ready
    && message.config?.companellaPath === "fake-missing-exports.mjs");
assert.equal(degradedReady.status, "partial");
assert.equal(degradedReady.compatibility.status, "degraded");
assert.equal(degradedReady.capabilities.pipeline, true);
assert.equal(degradedReady.capabilities.companella, false);
assert.equal(degradedReady.capabilities.companellaDirect, false);
assert.equal(degradedReady.capabilities.mixedCompanella, false);
assert.equal(degradedReady.capabilities.algorithms.includes("Companella"), false);

const duplicateWorker = new RuntimeFixtureWorker("duplicate");
const duplicateRuntime = new HeadlessAnalyzerRuntime({
    workerFactory: () => duplicateWorker,
    supersedePending: false,
});
const duplicateReadyPromise = duplicateRuntime.initialize();
await waitForRuntimePost(duplicateWorker, (message) => message.type === MESSAGE_TYPES.Configure);
duplicateWorker.emit(createResponse({
    type: MESSAGE_TYPES.Ready,
    status: "ok",
    config: {},
    capabilities: {},
    compatibility: { status: "compatible" },
}));
await duplicateReadyPromise;

const originalPending = duplicateRuntime.analyze({
    correlationId: "duplicate-correlation",
    rawText: "original map",
    requestedAlgorithm: "Sunny",
});
await waitForRuntimePost(duplicateWorker, (message) => message.type === MESSAGE_TYPES.Analyze
    && message.correlationId === "duplicate-correlation");
const duplicateAttempt = duplicateRuntime.analyze({
    correlationId: "duplicate-correlation",
    rawText: "replacement map",
    requestedAlgorithm: "Daniel",
});
await assert.rejects(duplicateAttempt, (exception) => exception instanceof HeadlessAnalysisError
    && exception.code === "DUPLICATE_CORRELATION_ID");
assert.equal(
    duplicateWorker.messages.filter((message) => message.type === MESSAGE_TYPES.Analyze).length,
    1,
);
duplicateWorker.emit(createResponse({
    type: MESSAGE_TYPES.Result,
    correlationId: "duplicate-correlation",
    status: "ok",
    requestedAlgorithm: "Sunny",
    actualAlgorithm: "Sunny",
    analysis: { metrics: {} },
}));
await originalPending;
duplicateRuntime.dispose();

const malformedOriginalWorker = new RuntimeFixtureWorker("malformed-original");
const reinitializedWorker = new RuntimeFixtureWorker("malformed-reinitialized");
const malformedWorkers = [malformedOriginalWorker, reinitializedWorker];
const malformedRuntime = new HeadlessAnalyzerRuntime({
    workerFactory: () => malformedWorkers.shift(),
    supersedePending: false,
});
const malformedReadyPromise = malformedRuntime.initialize();
await waitForRuntimePost(malformedOriginalWorker, (message) => message.type === MESSAGE_TYPES.Configure);
malformedOriginalWorker.emit(createResponse({
    type: MESSAGE_TYPES.Ready,
    status: "ok",
    config: {},
    capabilities: {},
    compatibility: { status: "compatible" },
}));
await malformedReadyPromise;

const malformedFirstPending = malformedRuntime.analyze({
    correlationId: "malformed-first",
    rawText: "first map",
});
const malformedSecondPending = malformedRuntime.analyze({
    correlationId: "malformed-second",
    rawText: "second map",
});
await waitForRuntimePost(malformedOriginalWorker, (message) => message.type === MESSAGE_TYPES.Analyze
    && message.correlationId === "malformed-first");
await waitForRuntimePost(malformedOriginalWorker, (message) => message.type === MESSAGE_TYPES.Analyze
    && message.correlationId === "malformed-second");
malformedOriginalWorker.emit({
    protocol: fixtureRequest.protocol,
    protocolVersion: fixtureRequest.protocolVersion,
    type: MESSAGE_TYPES.Result,
    status: "ok",
});
await assert.rejects(malformedFirstPending, (exception) => exception instanceof HeadlessAnalysisError
    && exception.code === "INVALID_WORKER_RESPONSE");
await assert.rejects(malformedSecondPending, (exception) => exception instanceof HeadlessAnalysisError
    && exception.code === "INVALID_WORKER_RESPONSE");
assert.equal(malformedOriginalWorker.terminated, true);

const reinitializedReadyPromise = malformedRuntime.initialize();
await waitForRuntimePost(reinitializedWorker, (message) => message.type === MESSAGE_TYPES.Configure);
reinitializedWorker.emit(createResponse({
    type: MESSAGE_TYPES.Ready,
    status: "ok",
    config: {},
    capabilities: {},
    compatibility: { status: "compatible" },
}));
await reinitializedReadyPromise;
const recoveredPending = malformedRuntime.analyze({
    correlationId: "reinitialized-analysis",
    rawText: "reinitialized map",
});
await waitForRuntimePost(reinitializedWorker, (message) => message.type === MESSAGE_TYPES.Analyze
    && message.correlationId === "reinitialized-analysis");
reinitializedWorker.emit(createResponse({
    type: MESSAGE_TYPES.Result,
    correlationId: "reinitialized-analysis",
    status: "ok",
    requestedAlgorithm: "Mixed",
    actualAlgorithm: "Mixed",
    analysis: { metrics: {} },
}));
await recoveredPending;
malformedRuntime.dispose();

console.log("Headless analyzer protocol and worker-like fixtures passed.");

function waitFor(predicate, timeoutMs = 2000) {
    const existing = postedMessages.find(predicate);
    if (existing) {
        return Promise.resolve(existing);
    }

    return new Promise((resolve, reject) => {
        const waiter = {
            predicate,
            resolve: (message) => {
                clearTimeout(timeoutId);
                resolve(message);
            },
        };
        const timeoutId = setTimeout(() => {
            const index = waiters.indexOf(waiter);
            if (index >= 0) {
                waiters.splice(index, 1);
            }
            reject(new Error("Timed out waiting for a worker fixture response."));
        }, timeoutMs);
        waiters.push(waiter);
    });
}

function waitForRuntimePost(worker, predicate, timeoutMs = 2000) {
    const existing = worker.messages.find(predicate);
    if (existing) {
        return Promise.resolve(existing);
    }

    return new Promise((resolve, reject) => {
        const startedAt = Date.now();
        const poll = () => {
            const message = worker.messages.find(predicate);
            if (message) {
                resolve(message);
                return;
            }

            if (Date.now() - startedAt >= timeoutMs) {
                reject(new Error("Timed out waiting for a runtime fixture post."));
                return;
            }

            setTimeout(poll, 0);
        };
        poll();
    });
}
