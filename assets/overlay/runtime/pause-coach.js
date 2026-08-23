(function () {
  "use strict";

  if (typeof window.__createRealtimePauseCoachRuntime === "function") return;

  const QUALITY = Object.freeze({
    EXACT: "Exact",
    OBSERVED: "Observed",
    RECONSTRUCTED: "Reconstructed",
    ESTIMATED: "Estimated",
    UNAVAILABLE: "Unavailable",
  });

  const STATES = Object.freeze({
    UNAVAILABLE: "Unavailable",
    WAITING: "WaitingForGame",
    PLAYING: "Playing",
    PAUSED: "Paused",
    INSUFFICIENT: "InsufficientData",
    READY: "Ready",
    ERROR: "Error",
  });

  const DEFAULTS = Object.freeze({
    recentWindowSeconds: 20,
    baselineWindowSeconds: 20,
    minimumTimingSamples: 12,
    timingBiasThresholdMs: 8,
    timingInstabilityUrThreshold: 45,
    timingInstabilityMultiplier: 1.35,
    accuracyDropThreshold: 0.05,
    missSpikeMultiplier: 2,
    minimumMissesForSpike: 2,
    sectionAccuracyDropThreshold: 0.06,
    maxTimelineEvents: 512,
    maxTimingSamples: 512,
    maxInsights: 4,
  });

  function finite(value) {
    if (value == null || String(value).trim() === "") return null;
    const number = Number(String(value).replace(",", "."));
    return Number.isFinite(number) ? number : null;
  }

  function now() { return Date.now(); }

  function clean(value) { return String(value == null ? "" : value).trim(); }

  function normalizeState(input) {
    const source = input && typeof input === "object" ? input : {};
    const name = clean(source.state || source.name).toLowerCase().replace(/[^a-z]/g, "");
    if (source.isSpectating === true || name === "spectating") return "spectating";
    if (source.isReplay === true || ["replay", "watchingreplay"].includes(name)) return "replay";
    if (source.isPaused === true && source.isPlaying !== false) return "paused";
    if (source.isPlaying === true || ["play", "gameplay", "playing"].includes(name)) return "playing";
    if (["result", "results", "resultscreen"].includes(name)) return "results";
    if (["menu", "songselect", "selectplay", "edit", "options"].includes(name)) return "menu";
    return "unknown";
  }

  function normalizeHits(play) {
    const source = play && play.hits && typeof play.hits === "object" ? play.hits : {};
    function pick(names) {
      for (const name of names) {
        const value = finite(source[name]);
        if (value !== null) return Math.max(0, Math.trunc(value));
      }
      return null;
    }
    return {
      count300: pick(["300", "count300"]),
      count200: pick(["200", "count200"]),
      count100: pick(["100", "count100"]),
      count50: pick(["50", "count50"]),
      countMiss: pick(["0", "miss", "countMiss", "count0"]),
      total: 0,
      hitTotal: 0,
    };
  }

  function finalizeHits(hits) {
    const values = [hits.count300, hits.count200, hits.count100, hits.count50, hits.countMiss];
    const known = values.filter(value => value !== null);
    hits.total = known.length ? known.reduce((sum, value) => sum + value, 0) : null;
    const hitValues = values.slice(0, 4).filter(value => value !== null);
    hits.hitTotal = hitValues.length ? hitValues.reduce((sum, value) => sum + value, 0) : null;
    return hits;
  }

  function normalizeMods(play) {
    const raw = play && play.mods;
    if (Array.isArray(raw)) return raw.map(clean).filter(Boolean).map(value => value.toUpperCase());
    if (raw && typeof raw === "object" && Array.isArray(raw.array)) {
      return raw.array.map(entry => entry && typeof entry === "object" ? clean(entry.acronym) : clean(entry))
        .filter(Boolean).map(value => value.toUpperCase());
    }
    return clean(raw).split(/[\s,;+]+/).filter(Boolean).map(value => value.toUpperCase());
  }

  function readOffsets(play) {
    const raw = play && Array.isArray(play.hitErrorArray) ? play.hitErrorArray : [];
    return raw.map(finite).filter(value => value !== null);
  }

  function same(a, b) { return Math.abs(a - b) < 0.001; }

  function median(values) {
    if (!values.length) return null;
    const sorted = values.slice().sort((a, b) => a - b);
    const middle = Math.floor(sorted.length / 2);
    return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
  }

  function stats(values) {
    if (!values.length) return { mean: null, median: null, sd: null, ur: null, early: null, late: null };
    const mean = values.reduce((sum, value) => sum + value, 0) / values.length;
    const variance = values.reduce((sum, value) => sum + Math.pow(value - mean, 2), 0) / values.length;
    return {
      mean,
      median: median(values),
      sd: Math.sqrt(variance),
      ur: Math.sqrt(variance) * 10,
      early: values.filter(value => value < 0).length * 100 / values.length,
      late: values.filter(value => value > 0).length * 100 / values.length,
    };
  }

  function confidence(value) {
    return value >= 0.75 ? "high" : value >= 0.55 ? "medium" : "low";
  }

  function qualityFor(timingQuality, hasObservedPerformance) {
    if (timingQuality !== QUALITY.UNAVAILABLE) return timingQuality;
    return hasObservedPerformance ? QUALITY.OBSERVED : QUALITY.UNAVAILABLE;
  }

  function create(options) {
    const config = Object.assign({}, DEFAULTS, options || {});
    let session = null;
    let previous = null;
    let sessionCounter = 0;
    let offsets = [];
    let latestOffsets = [];
    let accuracies = [];
    let timeline = [];

    function clearAttempt() {
      offsets = [];
      latestOffsets = [];
      accuracies = [];
      timeline = [];
      appendOffsets.previous = [];
    }

    function start(sample) {
      clearAttempt();
      sessionCounter += 1;
      session = {
        id: `${sample.beatmapId || "unknown"}:${sample.receivedAt}:${sessionCounter}`,
        beatmapId: sample.beatmapId || "",
        beatmapHash: sample.beatmapHash || "",
        startedAt: sample.receivedAt,
        endedAt: null,
        mods: sample.mods,
      };
      appendEvent(sample, "StateChanged", QUALITY.OBSERVED);
    }

    function finish(sample) {
      if (!session) return;
      session.endedAt = sample.receivedAt;
      appendEvent(sample, "SessionFinished", QUALITY.OBSERVED);
    }

    function appendEvent(sample, type, quality, value) {
      timeline.push({
        mapTime: sample.mapTimeMs,
        receivedAt: sample.receivedAt,
        type,
        value: value == null ? null : value,
        source: "tosu.v2",
        quality,
      });
      if (timeline.length > config.maxTimelineEvents) timeline.splice(0, timeline.length - config.maxTimelineEvents);
    }

    function isRetry(sample) {
      if (!session || !previous) return false;
      const mapReset = sample.mapTimeMs + 1500 < previous.mapTimeMs;
      const scoreReset = sample.score != null && previous.score != null && sample.score < previous.score;
      const missReset = sample.hits.countMiss != null && previous.hits.countMiss != null && sample.hits.countMiss < previous.hits.countMiss;
      const hitReset = sample.hits.hitTotal != null && previous.hits.hitTotal != null && sample.hits.hitTotal < previous.hits.hitTotal;
      return mapReset || scoreReset || missReset || hitReset;
    }

    function appendOffsets(sample) {
      const current = sample.offsets;
      latestOffsets = current.slice();
      if (!current.length) return;
      let prefix = 0;
      const old = appendOffsets.previous || [];
      if (current.length <= old.length && current.every((value, index) => same(value, old[index]))) {
        appendOffsets.previous = current.slice();
        return;
      }
      while (prefix < Math.min(current.length, old.length) && same(current[prefix], old[prefix])) prefix += 1;
      const startAt = prefix === old.length ? prefix : 0;
      for (let index = startAt; index < current.length; index += 1) {
        offsets.push({ receivedAt: sample.receivedAt, mapTimeMs: sample.mapTimeMs, value: current[index] });
      }
      appendOffsets.previous = current.slice();
      if (offsets.length > config.maxTimingSamples) offsets.splice(0, offsets.length - config.maxTimingSamples);
      if (current.length) appendEvent(sample, "HitErrorObserved", QUALITY.RECONSTRUCTED, current.length);
    }

    function appendAccuracy(sample) {
      if (sample.accuracy == null) return;
      accuracies.push({ receivedAt: sample.receivedAt, value: sample.accuracy });
      const cutoff = sample.receivedAt - Math.max(10000, (config.recentWindowSeconds + config.baselineWindowSeconds) * 1000);
      accuracies = accuracies.filter(point => point.receivedAt >= cutoff);
      appendEvent(sample, "AccuracyChanged", QUALITY.OBSERVED, sample.accuracy);
    }

    function currentSection(sample, recent, baseline, currentMisses) {
      const supplied = sample.currentSection && typeof sample.currentSection === "object" ? sample.currentSection : null;
      if (supplied) return supplied;
      if (sample.mapTimeMs == null || sample.mapTimeMs < 0) return null;
      const seconds = config.recentWindowSeconds;
      const startMs = Math.max(0, sample.mapTimeMs - seconds * 1000);
      return {
        id: `recent-${Math.floor(sample.mapTimeMs / (seconds * 1000))}`,
        label: `Recent ${seconds}s`,
        startTimeMs: startMs,
        endTimeMs: sample.mapTimeMs,
        patternTypes: [],
        accuracyDelta: sample.accuracy != null && baseline.accuracy != null ? sample.accuracy - baseline.accuracy : null,
        misses: currentMisses,
        meanHitErrorMs: recent.mean,
        timingDeviationMs: recent.sd,
        dataQuality: QUALITY.RECONSTRUCTED,
        dominantPatternKind: null,
      };
    }

    function buildInsights(sample, recent, baseline, recentStats, baselineStats, currentMisses, section, hasEnough) {
      const candidates = [];
      if (!hasEnough && recent.length === 0 && currentMisses == null) {
        candidates.push(candidate("InsufficientData", "Info", "Not enough telemetry yet", "Keep playing a little longer so Pause Coach can separate a trend from noise.", `Timing samples: ${recent.length}/${config.minimumTimingSamples}.`, 0.9, QUALITY.UNAVAILABLE));
      }
      if (recent.length >= config.minimumTimingSamples && recentStats.mean != null && Math.abs(recentStats.mean) >= config.timingBiasThresholdMs) {
        const late = recentStats.mean > 0;
        candidates.push(candidate(late ? "TimingLate" : "TimingEarly", "Warning", late ? "Consistent late timing" : "Consistent early timing", `Your recent hits are centered ${Math.abs(recentStats.mean).toFixed(1)} ms ${late ? "late" : "early"}.`, `Recent mean: ${recentStats.mean >= 0 ? "+" : ""}${recentStats.mean.toFixed(1)} ms; samples: ${recent.length}.`, 0.62, QUALITY.RECONSTRUCTED));
      }
      if (recent.length >= config.minimumTimingSamples && recentStats.ur != null && recentStats.ur >= config.timingInstabilityUrThreshold) {
        const worsened = baselineStats.ur != null && recentStats.ur >= baselineStats.ur * config.timingInstabilityMultiplier;
        candidates.push(candidate("TimingUnstable", worsened ? "Critical" : "Warning", worsened ? "Timing stability dropped" : "Timing is unstable", "Your timing spread is wide enough to explain a noticeable accuracy loss.", `Recent UR: ${recentStats.ur.toFixed(1)}; baseline UR: ${baselineStats.ur == null ? "—" : baselineStats.ur.toFixed(1)}; samples: ${recent.length}.`, 0.62, QUALITY.RECONSTRUCTED));
      }
      if (sample.accuracy != null && baseline.accuracy != null && baseline.accuracy - sample.accuracy >= config.accuracyDropThreshold) {
        candidates.push(candidate("AccuracyDrop", "Critical", "Accuracy dropped recently", "The latest section is performing below your earlier baseline.", `Current: ${(sample.accuracy * 100).toFixed(2)}%; baseline: ${(baseline.accuracy * 100).toFixed(2)}%.`, 0.72, QUALITY.OBSERVED));
      }
      if (currentMisses != null && currentMisses >= config.minimumMissesForSpike) {
        candidates.push(candidate("MissSpike", currentMisses >= config.minimumMissesForSpike * 2 ? "Critical" : "Warning", "Misses spiked in the recent window", "Most of the current damage happened recently, not evenly across the attempt.", `Recent misses: ${currentMisses}; recent hits: ${sample.recentHits == null ? "—" : sample.recentHits}.`, 0.6, QUALITY.OBSERVED));
      }
      if (section && section.accuracyDelta != null && section.accuracyDelta <= -config.sectionAccuracyDropThreshold) {
        candidates.push(candidate("SectionCollapse", "Warning", "The recent section is the weak point", `Performance fell in ${section.label || "the current section"}.`, `Section accuracy delta: ${(section.accuracyDelta * 100).toFixed(1)}%; data: ${section.dataQuality || QUALITY.RECONSTRUCTED}.`, 0.55, section.dataQuality || QUALITY.RECONSTRUCTED));
      }
      return candidates
        .sort((a, b) => ({ Info: 0, Warning: 1, Critical: 2 }[b.severity] - ({ Info: 0, Warning: 1, Critical: 2 }[a.severity]) || b.confidence - a.confidence))
        .filter((item, index, all) => all.findIndex(other => other.type === item.type) === index)
        .slice(0, Math.max(1, config.maxInsights))
        .map(item => Object.assign(item, { confidenceLabel: confidence(item.confidence), code: `pausecoach.${item.type.toLowerCase()}`, message: `${item.description} ${item.evidence}` }));
    }

    function candidate(type, severity, title, description, evidence, confidenceValue, dataQuality) {
      return { type, severity, title, description, evidence, confidence: confidenceValue, dataQuality };
    }

    function process(input) {
      const payload = input && typeof input === "object" ? input : {};
      const sourceState = payload.gameplay || payload.state || {};
      const state = normalizeState(sourceState);
      const play = payload.play && typeof payload.play === "object" ? payload.play : {};
      const hits = finalizeHits(normalizeHits(play));
      const sample = {
        beatmapId: clean(payload.beatmap && (payload.beatmap.id || payload.beatmap.beatmapId)) || clean(payload.beatmapId),
        beatmapHash: clean(payload.beatmap && payload.beatmap.hash),
        state,
        mapTimeMs: finite(payload.mapTimeMs != null ? payload.mapTimeMs : payload.beatmapTimeMs) || 0,
        score: finite(play.score),
        accuracy: finite(play.accuracy),
        combo: finite(play.combo != null ? play.combo : play.currentCombo),
        maxCombo: finite(play.maxCombo != null ? play.maxCombo : play.maximumCombo),
        health: finite(play.health != null ? play.health : play.hp),
        failed: play.failed === true,
        mods: normalizeMods(play),
        hits,
        offsets: readOffsets(play),
        focused: sourceState.isFocused,
        receivedAt: now(),
        currentSection: payload.currentSection || null,
      };
      sample.replay = state === "replay";
      sample.spectating = state === "spectating";

      if (sample.replay || sample.spectating) {
        finish(sample);
        previous = sample;
        return unavailable(sample, "Pause Coach is disabled for replay playback or spectating.");
      }

      const changedBeatmap = session && (session.beatmapId !== sample.beatmapId || session.beatmapHash !== sample.beatmapHash);
      const afterFinished = sample.state === "playing" && previous && !["playing", "paused"].includes(previous.state);
      const retry = isRetry(sample);
      if (sample.state === "playing" && (!session || changedBeatmap || retry || afterFinished)) start(sample);

      if (!session) {
        previous = sample;
        return waiting(sample);
      }

      if (previous && previous.state !== sample.state) {
        appendEvent(sample, "StateChanged", QUALITY.OBSERVED);
        if (sample.state === "paused") appendEvent(sample, "PauseStarted", QUALITY.OBSERVED);
        if (previous.state === "paused" && sample.state === "playing") appendEvent(sample, "PauseEnded", QUALITY.OBSERVED);
      }
      appendEvent(sample, "TelemetryReceived", QUALITY.OBSERVED);
      appendAccuracy(sample);
      appendOffsets.previous = appendOffsets.previous || [];
      appendOffsets(sample);

      const recentCutoff = sample.receivedAt - config.recentWindowSeconds * 1000;
      const baselineCutoff = recentCutoff - config.baselineWindowSeconds * 1000;
      const recent = offsets.filter(item => item.receivedAt >= recentCutoff).map(item => item.value);
      const baseline = offsets.filter(item => item.receivedAt < recentCutoff && item.receivedAt >= baselineCutoff).map(item => item.value);
      const recentStats = stats(recent);
      const baselineStats = stats(baseline);
      const baselineAccuracy = accuracies.filter(point => point.receivedAt < recentCutoff).map(point => point.value).slice(-1)[0] ?? null;
      const priorHits = previous ? previous.hits.hitTotal : null;
      const priorMisses = previous ? previous.hits.countMiss : null;
      const recentHits = priorHits != null && hits.hitTotal != null && hits.hitTotal >= priorHits ? hits.hitTotal - priorHits : (hits.hitTotal != null ? hits.hitTotal : null);
      const recentMisses = priorMisses != null && hits.countMiss != null && hits.countMiss >= priorMisses ? hits.countMiss - priorMisses : (hits.countMiss != null ? hits.countMiss : null);
      sample.recentHits = recentHits;
      const section = currentSection(sample, recentStats, { accuracy: baselineAccuracy }, recentMisses);
      const hasEnough = recent.length >= config.minimumTimingSamples || recentHits != null && recentHits > 0;
      const timingQuality = recent.length ? QUALITY.RECONSTRUCTED : QUALITY.UNAVAILABLE;
      const dataQuality = qualityFor(timingQuality, hits.total != null || sample.accuracy != null);
      const widgetState = sample.state === "paused" ? (hasEnough ? STATES.PAUSED : STATES.INSUFFICIENT)
        : sample.state === "results" ? (hasEnough ? STATES.READY : STATES.INSUFFICIENT)
          : sample.state === "playing" ? STATES.PLAYING : STATES.WAITING;
      const insights = sample.state === "paused" || sample.state === "results"
        ? buildInsights(sample, recent, baseline, recentStats, baselineStats, recentMisses, section, hasEnough)
        : [];
      const diagnostics = [];
      if (recent.length < config.minimumTimingSamples) diagnostics.push(`pausecoach.insufficient_timing: need ${config.minimumTimingSamples} timing samples, have ${recent.length}.`);
      diagnostics.push("pausecoach.columns.unavailable: Tosu v2 does not expose reliable key-to-note correlation in this adapter.");
      if (!payload.currentSection) diagnostics.push("pausecoach.patterns.unavailable: canonical pattern labels are not present in the realtime Tosu payload.");

      const snapshot = {
        state: widgetState,
        sessionId: session.id,
        dataQuality,
        updatedAt: new Date(sample.receivedAt).toISOString(),
        fidelity: "provisional",
        isProvisional: true,
        reason: "Realtime Tosu v2 telemetry. Exact per-column and per-object information requires an .osr replay.",
        mapProgressMs: sample.mapTimeMs,
        score: sample.score,
        accuracy: sample.accuracy,
        health: sample.health,
        combo: sample.combo,
        maxCombo: sample.maxCombo,
        failed: sample.failed,
        mods: sample.mods,
        timing: {
          sampleCount: recent.length,
          meanMs: recentStats.mean,
          medianMs: recentStats.median,
          unstableRate: recentStats.ur,
          earlyLateRatio: recentStats.late ? recentStats.early / recentStats.late : null,
          earlyPercentage: recentStats.early,
          latePercentage: recentStats.late,
          driftMs: recentStats.mean,
          previousBaselineMs: baselineStats.mean,
          timingMargin: recent.length < config.minimumTimingSamples ? "unknown" : "observed",
          recentOffsets: latestOffsets.slice(-20),
          dataQuality: timingQuality,
        },
        overall: {
          accuracy: sample.accuracy,
          combo: sample.combo,
          maxCombo: sample.maxCombo,
          score: sample.score,
          hits: hits.hitTotal,
          misses: hits.countMiss,
          dataQuality: hits.total != null || sample.accuracy != null ? QUALITY.OBSERVED : QUALITY.UNAVAILABLE,
        },
        recent: {
          windowSeconds: config.recentWindowSeconds,
          accuracy: sample.accuracy,
          meanTimingMs: recentStats.mean,
          timingDeviationMs: recentStats.sd,
          hits: recentHits,
          misses: recentMisses,
          dataQuality,
        },
        performance: {
          wholeHits: hits.hitTotal,
          wholeMisses: hits.countMiss,
          wholeAccuracy: sample.accuracy,
          recentAccuracy: sample.accuracy != null && baselineAccuracy != null ? sample.accuracy : null,
          recentHits,
          recentMisses,
        },
        section: section || {},
        sections: section ? [section] : [],
        columns: [],
        insights,
        diagnostics,
        hasData: sample.mapTimeMs != null || sample.accuracy != null || recent.length > 0 || hits.total != null,
      };
      if (sample.state === "menu" || sample.state === "results" || sample.failed) finish(sample);
      previous = sample;
      return snapshot;
    }

    function waiting(sample) {
      return {
        state: STATES.WAITING,
        sessionId: "",
        dataQuality: QUALITY.UNAVAILABLE,
        updatedAt: new Date(sample.receivedAt).toISOString(),
        fidelity: "provisional",
        isProvisional: true,
        reason: "Start a mania play to collect realtime telemetry.",
        mapProgressMs: sample.mapTimeMs,
        score: sample.score,
        accuracy: sample.accuracy,
        timing: { sampleCount: 0, dataQuality: QUALITY.UNAVAILABLE, timingMargin: "unknown", recentOffsets: [] },
        overall: { accuracy: sample.accuracy, dataQuality: sample.accuracy == null ? QUALITY.UNAVAILABLE : QUALITY.OBSERVED },
        recent: { windowSeconds: config.recentWindowSeconds, dataQuality: QUALITY.UNAVAILABLE },
        performance: {}, section: {}, sections: [], columns: [], insights: [],
        diagnostics: ["pausecoach.waiting: start a mania play to collect realtime telemetry."],
        hasData: false,
      };
    }

    function unavailable(sample, reason) {
      return {
        state: STATES.UNAVAILABLE,
        sessionId: "",
        dataQuality: QUALITY.UNAVAILABLE,
        updatedAt: new Date(sample.receivedAt).toISOString(),
        fidelity: "unavailable",
        isProvisional: true,
        reason,
        mapProgressMs: sample.mapTimeMs,
        timing: { sampleCount: 0, dataQuality: QUALITY.UNAVAILABLE, timingMargin: "unknown", recentOffsets: [] },
        overall: { dataQuality: QUALITY.UNAVAILABLE }, recent: { windowSeconds: config.recentWindowSeconds, dataQuality: QUALITY.UNAVAILABLE },
        performance: {}, section: {}, sections: [], columns: [], insights: [], diagnostics: [reason], hasData: false,
      };
    }

    function dispose() {
      session = null;
      previous = null;
      clearAttempt();
    }

    return { process, dispose, get session() { return session; }, get timeline() { return timeline.slice(); } };
  }

  window.__createRealtimePauseCoachRuntime = create;
})();
