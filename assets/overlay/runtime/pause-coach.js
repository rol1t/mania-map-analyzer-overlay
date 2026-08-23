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

  function canonicalAccuracy(value) {
    const number = finite(value);
    if (number === null) return null;
    // Tosu has emitted both percentage points (98.73) and fractions (0.9873)
    // over time.  The runtime boundary is the only place where that ambiguity
    // is allowed to exist.
    const normalized = Math.abs(number) > 1.000001 ? number / 100 : number;
    return Math.max(0, Math.min(1, normalized));
  }

  function now() { return Date.now(); }

  function clean(value) { return String(value == null ? "" : value).trim(); }

  function normalizeState(input) {
    const source = input && typeof input === "object" ? input : {};
    const name = clean(source.state || source.name).toLowerCase().replace(/[^a-z]/g, "");
    if (source.isSpectating === true || name === "spectating") return "spectating";
    if (source.isReplay === true || ["replay", "watchingreplay"].includes(name)) return "replay";
    // Tosu may report the pause menu as `isPlaying: false` while retaining
    // `game.paused: true`. The pause flag is the stronger signal here; using
    // it unconditionally prevents the widget from falling back to Waiting
    // with only the last accuracy value visible.
    if (source.isPaused === true) return "paused";
    if (["pause", "paused", "break"].includes(name)) return "paused";
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
      countGeki: pick(["geki", "MAX", "max", "countGeki"]),
      countKatu: pick(["katu", "Katu", "countKatu"]),
      countMiss: pick(["0", "miss", "countMiss", "count0"]),
      total: 0,
      hitTotal: 0,
    };
  }

  function finalizeHits(hits) {
    const values = [hits.count300, hits.count200, hits.count100, hits.count50, hits.countGeki, hits.countKatu, hits.countMiss];
    const known = values.filter(value => value !== null);
    hits.total = known.length ? known.reduce((sum, value) => sum + value, 0) : null;
    const hitValues = values.slice(0, 6).filter(value => value !== null);
    hits.hitTotal = hitValues.length ? hitValues.reduce((sum, value) => sum + value, 0) : null;
    return hits;
  }

  function normalizeMods(play) {
    const raw = play && play.mods;
    if (Array.isArray(raw)) return raw.map(clean).filter(Boolean).map(value => value.toUpperCase());
    if (raw && typeof raw === "object") {
      const values = Array.isArray(raw.array)
        ? raw.array
        : (raw.acronym || raw.name
          ? [raw]
          : Object.keys(raw).filter(key => raw[key] === true));
      return values.map(entry => entry && typeof entry === "object" ? clean(entry.acronym || entry.name) : clean(entry))
        .filter(Boolean).map(value => value.toUpperCase());
    }
    return clean(raw).split(/[\s,;+]+/).filter(Boolean).map(value => value.toUpperCase());
  }

  function readOffsets(play) {
    const raw = play && Array.isArray(play.hitErrorArray) ? play.hitErrorArray : [];
    return raw.map(finite).filter(value => value !== null);
  }

  function readCombo(play) {
    const raw = play && play.combo;
    if (raw && typeof raw === "object") {
      return {
        current: finite(raw.current != null ? raw.current : raw.value),
        max: finite(raw.max != null ? raw.max : raw.maximum),
      };
    }
    return { current: finite(raw != null ? raw : play && play.currentCombo), max: finite(play && (play.maxCombo != null ? play.maxCombo : play.maximumCombo)) };
  }

  function readHealth(play) {
    const bar = play && play.healthBar;
    if (bar && typeof bar === "object") {
      const value = finite(bar.normal != null ? bar.normal : bar.smooth);
      if (value !== null) return value;
    }
    return finite(play && (play.health != null ? play.health : play.hp));
  }

  function accuracyFromHits(hits) {
    if (!hits) return null;
    const required = [hits.count300, hits.count200, hits.count100, hits.count50, hits.countGeki, hits.countKatu, hits.countMiss];
    if (required.some(value => value === null)) return null;
    const max = hits.count300 + hits.countGeki;
    const weighted = max * 6 + (hits.count200 + hits.countKatu) * 4 + hits.count100 * 2 + hits.count50;
    const total = hits.total;
    return total > 0 ? weighted / (total * 6) : null;
  }

  // Tosu v2 -> Pause Coach contract. Everything below this function consumes
  // this normalized shape; raw nested Tosu objects never reach the windowing
  // or insight code.
  function normalizeTosuPlay(play) {
    const source = play && typeof play === "object" ? play : {};
    const combo = readCombo(source);
    return {
      score: finite(source.score),
      accuracy: canonicalAccuracy(source.accuracy),
      combo: combo.current,
      maxCombo: combo.max,
      health: readHealth(source),
      failed: source.failed === true,
      mods: normalizeMods(source),
      hits: finalizeHits(normalizeHits(source)),
      offsets: readOffsets(source),
      unstableRate: finite(source.unstableRate),
    };
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
    let judgementHistory = [];
    let timeline = [];

    function clearAttempt() {
      offsets = [];
      latestOffsets = [];
      judgementHistory = [];
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
      appendEvent(sample, "AccuracyChanged", QUALITY.OBSERVED, sample.accuracy);
    }

    function appendJudgements(sample) {
      if (sample.hits.hitTotal == null && sample.hits.countMiss == null) return;
      const last = judgementHistory[judgementHistory.length - 1];
      if (last && sample.mapTimeMs < last.mapTimeMs) return;
      if (last && sample.mapTimeMs === last.mapTimeMs) {
        judgementHistory[judgementHistory.length - 1] = { mapTimeMs: sample.mapTimeMs, hits: sample.hits.hitTotal, misses: sample.hits.countMiss, hitData: sample.hits };
      } else {
        judgementHistory.push({ mapTimeMs: sample.mapTimeMs, hits: sample.hits.hitTotal, misses: sample.hits.countMiss, hitData: sample.hits });
      }
      const cutoff = sample.mapTimeMs - Math.max(10000, (config.recentWindowSeconds + config.baselineWindowSeconds) * 1000) - 1000;
      judgementHistory = judgementHistory.filter(point => point.mapTimeMs >= cutoff);
      if (judgementHistory.length > config.maxTimelineEvents) judgementHistory.splice(0, judgementHistory.length - config.maxTimelineEvents);
    }

    function counterAtOrBefore(mapTimeMs) {
      let result = null;
      for (const point of judgementHistory) {
        if (point.mapTimeMs > mapTimeMs) break;
        result = point;
      }
      // Before the first retained point, the attempt start is the only
      // defensible baseline. Do not manufacture a delta from zero counters.
      return result || (judgementHistory.length && mapTimeMs < judgementHistory[0].mapTimeMs ? judgementHistory[0] : null);
    }

    function deltaCounter(current, start) {
      if (current == null) return null;
      if (start == null || start > current) return current;
      return Math.max(0, current - start);
    }

    function windowMetrics(sample) {
      const recentStart = sample.mapTimeMs - config.recentWindowSeconds * 1000;
      const baselineStart = recentStart - config.baselineWindowSeconds * 1000;
      const recentPoint = counterAtOrBefore(recentStart);
      const baselinePoint = counterAtOrBefore(baselineStart);
      const recentHits = deltaCounter(sample.hits.hitTotal, recentPoint && recentPoint.hits);
      const recentMisses = deltaCounter(sample.hits.countMiss, recentPoint && recentPoint.misses);
      const baselineHits = recentPoint && baselinePoint ? deltaCounter(recentPoint.hits, baselinePoint.hits) : null;
      const baselineMisses = recentPoint && baselinePoint ? deltaCounter(recentPoint.misses, baselinePoint.misses) : null;
      const currentLocal = sample.hits;
      const recentAccuracy = recentPoint && currentLocal ? accuracyFromHits(deltaHits(currentLocal, recentPoint.hitData)) : null;
      const baselineAccuracy = recentPoint && baselinePoint ? accuracyFromHits(deltaHits(recentPoint.hitData, baselinePoint.hitData)) : null;
      return { recentHits, recentMisses, baselineHits, baselineMisses, recentAccuracy, baselineAccuracy };
    }

    function deltaHits(current, start) {
      if (!current || !start) return null;
      const result = {};
      for (const key of ["count300", "count200", "count100", "count50", "countGeki", "countKatu", "countMiss"]) {
        if (current[key] == null || start[key] == null || current[key] < start[key]) return null;
        result[key] = current[key] - start[key];
      }
      return finalizeHits(result);
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
        accuracyDelta: recent.accuracy != null && baseline.accuracy != null ? recent.accuracy - baseline.accuracy : null,
        misses: currentMisses,
        meanHitErrorMs: recent.mean,
        timingDeviationMs: recent.sd,
        dataQuality: QUALITY.RECONSTRUCTED,
        dominantPatternKind: null,
      };
    }

    function buildInsights(sample, recent, baseline, recentStats, baselineStats, currentMisses, section, hasEnough, recentAccuracy) {
      const candidates = [];
      if (!hasEnough && recent.length === 0 && (currentMisses == null || sample.mapTimeMs < config.recentWindowSeconds * 1000)) {
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
      if (recent.accuracy != null && baseline.accuracy != null && baseline.accuracy - recent.accuracy >= config.accuracyDropThreshold) {
        candidates.push(candidate("AccuracyDrop", "Critical", "Accuracy dropped recently", "The latest section is performing below your earlier baseline.", `Recent: ${(recentAccuracy * 100).toFixed(2)}%; baseline: ${(baseline.accuracy * 100).toFixed(2)}%.`, 0.72, QUALITY.RECONSTRUCTED));
      }
      if (currentMisses != null && currentMisses >= config.minimumMissesForSpike) {
        candidates.push(candidate("MissSpike", currentMisses >= config.minimumMissesForSpike * 2 ? "Critical" : "Warning", "Misses spiked in the recent window", "Most of the current damage happened recently, not evenly across the attempt.", `Recent misses: ${currentMisses}; recent hits: ${sample.recentHits == null ? "—" : sample.recentHits}.`, 0.6, QUALITY.RECONSTRUCTED));
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
      const play = normalizeTosuPlay(payload.play);
      const hits = play.hits;
      const mapTimeValue = finite(payload.mapTimeMs != null ? payload.mapTimeMs : payload.beatmapTimeMs);
      const receivedValue = finite(payload.receivedAt);
      const currentBeatmapId = clean(payload.beatmap && (payload.beatmap.id || payload.beatmap.beatmapId)) || clean(payload.beatmapId);
      const currentBeatmapHash = clean(payload.beatmap && payload.beatmap.hash) || clean(payload.beatmapHash);
      const sample = {
        beatmapId: currentBeatmapId,
        beatmapHash: currentBeatmapHash,
        state,
        mapTimeMs: mapTimeValue != null ? mapTimeValue : (previous ? previous.mapTimeMs : 0),
        score: play.score,
        accuracy: play.accuracy,
        unstableRate: play.unstableRate,
        combo: play.combo,
        maxCombo: play.maxCombo,
        health: play.health,
        failed: play.failed,
        mods: play.mods,
        hits,
        offsets: play.offsets,
        focused: sourceState.isFocused,
        receivedAt: receivedValue != null ? receivedValue : now(),
        currentSection: payload.currentSection || null,
      };
      sample.replay = state === "replay";
      sample.spectating = state === "spectating";

      if (sample.replay || sample.spectating) {
        finish(sample);
        previous = sample;
        return unavailable(sample, "Pause Coach is disabled for replay playback or spectating.");
      }

      const changedBeatmap = session && ((sample.beatmapId && session.beatmapId && session.beatmapId !== sample.beatmapId)
        || (sample.beatmapHash && session.beatmapHash && session.beatmapHash !== sample.beatmapHash));
      const afterFinished = sample.state === "playing" && previous && !["playing", "paused"].includes(previous.state);
      const retry = isRetry(sample);
      if (sample.state === "playing" && (!session || changedBeatmap || retry || afterFinished)) start(sample);

      if (!session) {
        previous = sample;
        return waiting(sample);
      }

      // A state-only Tosu packet may omit beatmap identity. It is not evidence
      // of a new attempt; fill missing identity only when a later packet gives
      // us positive information.
      if (sample.beatmapId && !session.beatmapId) session.beatmapId = sample.beatmapId;
      if (sample.beatmapHash && !session.beatmapHash) session.beatmapHash = sample.beatmapHash;

      if (previous && previous.state !== sample.state) {
        appendEvent(sample, "StateChanged", QUALITY.OBSERVED);
        if (sample.state === "paused") appendEvent(sample, "PauseStarted", QUALITY.OBSERVED);
        if (previous.state === "paused" && sample.state === "playing") appendEvent(sample, "PauseEnded", QUALITY.OBSERVED);
      }
      appendEvent(sample, "TelemetryReceived", QUALITY.OBSERVED);
      appendAccuracy(sample);
      appendOffsets.previous = appendOffsets.previous || [];
      appendOffsets(sample);
      appendJudgements(sample);

      const recentCutoff = sample.mapTimeMs - config.recentWindowSeconds * 1000;
      const baselineCutoff = recentCutoff - config.baselineWindowSeconds * 1000;
      const recent = offsets.filter(item => item.mapTimeMs >= recentCutoff && item.mapTimeMs <= sample.mapTimeMs).map(item => item.value);
      const baseline = offsets.filter(item => item.mapTimeMs < recentCutoff && item.mapTimeMs >= baselineCutoff).map(item => item.value);
      const recentStats = stats(recent);
      const baselineStats = stats(baseline);
      const counters = windowMetrics(sample);
      const recentHits = counters.recentHits;
      const recentMisses = counters.recentMisses;
      const recentAccuracy = counters.recentAccuracy;
      const baselineAccuracy = counters.baselineAccuracy;
      sample.recentHits = recentHits;
      const section = currentSection(sample, { mean: recentStats.mean, sd: recentStats.sd, accuracy: recentAccuracy }, { accuracy: baselineAccuracy }, recentMisses);
      const hasEnough = recent.length >= config.minimumTimingSamples
        || recentHits != null && recentHits > 0 && sample.mapTimeMs >= config.recentWindowSeconds * 1000;
      const timingQuality = recent.length ? QUALITY.RECONSTRUCTED : QUALITY.UNAVAILABLE;
      const dataQuality = qualityFor(timingQuality, hits.total != null || sample.accuracy != null);
      const widgetState = sample.state === "paused" ? (hasEnough ? STATES.PAUSED : STATES.INSUFFICIENT)
        : sample.state === "results" ? (hasEnough ? STATES.READY : STATES.INSUFFICIENT)
          : sample.state === "playing" ? STATES.PLAYING : STATES.WAITING;
      // Keep the diagnosis live throughout the attempt. Pause/results are
      // still lifecycle boundaries, but they no longer gate calculation: the
      // same deterministic windowing runs for every gameplay sample.
      const insights = sample.state === "menu" ? []
        : buildInsights(sample, recent, baseline, recentStats, baselineStats, recentMisses, section, hasEnough, recentAccuracy);
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
          unstableRate: sample.unstableRate,
          hits: hits.hitTotal,
          misses: hits.countMiss,
          judgements: {
            count300: hits.count300,
            count200: hits.count200,
            count100: hits.count100,
            count50: hits.count50,
            countGeki: hits.countGeki,
            countKatu: hits.countKatu,
            countMiss: hits.countMiss,
          },
          dataQuality: hits.total != null || sample.accuracy != null ? QUALITY.OBSERVED : QUALITY.UNAVAILABLE,
        },
        recent: {
          windowSeconds: config.recentWindowSeconds,
          accuracy: recentAccuracy,
          accuracyProvenance: recentAccuracy == null ? QUALITY.UNAVAILABLE : QUALITY.RECONSTRUCTED,
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
          recentAccuracy,
          recentAccuracyProvenance: recentAccuracy == null ? QUALITY.UNAVAILABLE : QUALITY.RECONSTRUCTED,
          baselineAccuracy,
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
