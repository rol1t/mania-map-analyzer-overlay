import { createDiagnostic, isRecord } from "./protocol.mjs";

const SKILL_FIELDS = Object.freeze({
    Overall: "skills.overall",
    Stream: "skills.stream",
    Jumpstream: "skills.jumpstream",
    Handstream: "skills.handstream",
    Stamina: "skills.stamina",
    JackSpeed: "skills.jackspeed",
    Chordjack: "skills.chordjack",
    Technical: "skills.technical",
});

/**
 * Convert the official pipeline result into analyzer-neutral semantic data.
 * The keys in `metrics` are the public contract consumed by widgets; they do
 * not mirror MMA DOM IDs or CSS classes.
 */
export function normalizePipelineResult(result, {
    requestedAlgorithm,
    includeRawResult = false,
    companella = null,
} = {}) {
    if (!isRecord(result)) {
        throw new TypeError("The analyzer pipeline returned a non-object result.");
    }

    const rework = isRecord(result.rework) ? result.rework : {};
    const parsedSummary = isRecord(result.parsedSummary) ? result.parsedSummary : {};
    const requested = String(requestedAlgorithm || "Mixed");
    const actual = String(result.actualEstimatorAlgorithm || result.actualAlgorithm || requested);
    const metrics = {};

    setMetric(metrics, "algorithm.requested", requested, "algorithm");
    setMetric(metrics, "algorithm.actual", actual, "algorithm");
    setMetric(metrics, "difficulty.star", firstFinite(rework.star, result.sunnyStar), "SR");
    setMetric(metrics, "difficulty.numeric", rework.numericDifficulty, "difficulty");
    setMetric(metrics, "difficulty.label", rework.estDiff, "label");
    setMetric(metrics, "difficulty.lnPercent", toPercent(firstFinite(rework.lnRatio, parsedSummary.lnRatio)), "%");
    setMetric(metrics, "difficulty.keys", firstFinite(rework.columnCount, parsedSummary.columnCount), "keys");
    setMetric(metrics, "difficulty.sixKConst", result.sixKConst, "LV");

    const danLabels = splitDifficultyLabel(rework.estDiff);
    setMetric(metrics, "dan.rc.label", companella?.estDiff || danLabels.rc, "label");
    setMetric(metrics, "dan.rc.numeric", companella?.numericDifficulty ?? rework.numericDifficulty, "difficulty");
    setMetric(metrics, "dan.rc.variant", companella?.variant, "variant");
    setMetric(metrics, "dan.rc.confidence", companella?.confidence, "confidence");
    setMetric(metrics, "dan.ln.label", danLabels.ln, "label");

    const values = selectEtternaValues(result);
    for (const [field, metricId] of Object.entries(SKILL_FIELDS)) {
        setMetric(metrics, metricId, values[field], "MSD");
    }

    setMetric(metrics, "interlude.star", result.interludeStar, "SR");

    const pattern = isRecord(result.patternReport) ? result.patternReport : null;
    if (pattern) {
        setMetric(metrics, "pattern.category", pattern.Category, "category");
        setMetric(metrics, "pattern.lnPercent", toPercent(pattern.LNPercent), "%");
        setMetric(metrics, "pattern.svAmount", pattern.SVAmount, "amount");
        setMetric(metrics, "pattern.modeTag", pattern.ModeTag, "tag");
        setMetric(metrics, "pattern.clusters", pattern.Clusters, "clusters");
    }

    const diagnostics = collectPipelineDiagnostics(result);
    const analysis = {
        requestedAlgorithm: requested,
        actualAlgorithm: actual,
        metrics,
        availableMetricIds: Object.keys(metrics),
        summary: {
            metadata: parsedSummary.metadata || {},
            lnRatio: firstFinite(parsedSummary.lnRatio, rework.lnRatio),
            columnCount: firstFinite(parsedSummary.columnCount, rework.columnCount),
        },
        diagnostics,
    };

    if (includeRawResult === true) {
        analysis.rawResult = result;
    }

    return { analysis, diagnostics };
}

function collectPipelineDiagnostics(result) {
    const diagnostics = [];
    const optionalStages = [
        ["patternError", "pattern", "PATTERN_STAGE_FAILED"],
        ["ettError", "etterna", "ETTERNA_STAGE_FAILED"],
        ["interludeError", "interlude", "INTERLUDE_STAGE_FAILED"],
        ["companellaEttError", "companella-etterna", "COMPANELLA_ETTERNA_STAGE_FAILED"],
    ];

    for (const [field, stage, code] of optionalStages) {
        const message = String(result[field] || "").trim();
        if (message) {
            diagnostics.push(createDiagnostic({
                code,
                message,
                stage,
                severity: "warning",
            }));
        }
    }

    if (Array.isArray(result.errors)) {
        for (const message of result.errors) {
            if (String(message || "").trim()) {
                diagnostics.push(createDiagnostic({
                    code: "PIPELINE_DIAGNOSTIC",
                    message,
                    stage: "pipeline",
                    severity: "warning",
                }));
            }
        }
    }

    return diagnostics;
}

function selectEtternaValues(result) {
    if (isRecord(result.ettResult) && isRecord(result.ettResult.values)) {
        return result.ettResult.values;
    }

    return {};
}

function splitDifficultyLabel(value) {
    const parts = String(value || "")
        .split(/\s*\|\|\s*/)
        .map((part) => part.trim())
        .filter(Boolean);

    return {
        rc: parts[0] || null,
        ln: parts.length > 1 ? parts.slice(1).join(" || ") : null,
    };
}

function setMetric(target, id, value, unit) {
    const normalized = normalizeMetricValue(value);
    if (normalized === null) {
        return;
    }

    target[id] = {
        id,
        value: normalized,
        ...(unit ? { unit } : {}),
    };
}

function normalizeMetricValue(value) {
    if (value === null || value === undefined || value === "") {
        return null;
    }

    if (typeof value === "number") {
        return Number.isFinite(value) ? value : null;
    }

    if (typeof value === "string" || typeof value === "boolean") {
        return value;
    }

    if (Array.isArray(value) || isRecord(value)) {
        return value;
    }

    return null;
}

function firstFinite(...values) {
    for (const value of values) {
        if (value === null || value === undefined || value === "") {
            continue;
        }

        const number = Number(value);
        if (Number.isFinite(number)) {
            return number;
        }
    }

    return null;
}

function toPercent(value) {
    if (value === null || value === undefined || value === "") {
        return null;
    }

    const number = Number(value);
    if (!Number.isFinite(number)) {
        return null;
    }

    return Math.abs(number) <= 1 ? number * 100 : number;
}
