(function () {
  "use strict";

  const SOURCE_ID = "mania-map-analyser";
  const SCHEMA_VERSION = 1;

  if (window.__overlayAnalyzerAdapter && typeof window.__overlayAnalyzerAdapter.dispose === "function") {
    window.__overlayAnalyzerAdapter.dispose();
  }

  let socket = null;
  let observer = null;
  let animationFrame = 0;
  let publishTimer = 0;
  let lastPublishAt = 0;
  let reconnectTimer = 0;
  let statePollTimer = 0;
  let statePollInFlight = false;
  let lastStatePollWarningAt = 0;
  let disposed = false;
  let lastSignature = "";
  let beatmap = emptyBeatmap();
  let gameplay = emptyGameplay();
  let replay = emptyReplay();
  let pauseCoach = null;
  // HTTP polling is the authoritative state source for the native game. Keep
  // a confirmed pause through a stale websocket `paused:false` delta until
  // HTTP confirms that gameplay actually resumed.
  let httpPauseState = null;
  // Presentation changes re-inject this adapter while the WebView stays
  // alive. Keep the current-attempt coach across those re-initializations so
  // resizing or changing a preset does not erase the paused snapshot.
  let pauseCoachRuntime = typeof window.__createRealtimePauseCoachRuntime === "function"
    ? (window.__overlayPauseCoachRuntime || (window.__overlayPauseCoachRuntime = window.__createRealtimePauseCoachRuntime()))
    : null;
  let lastPlayingHits = null;

  function emptyBeatmap() {
    return {
      id: "",
      setId: "",
      artist: "",
      title: "",
      version: "",
      mapper: "",
      bpmLabel: "",
      overallDifficulty: null,
      healthDrain: null,
      backgroundUrl: "",
    };
  }

  function emptyGameplay() {
    return {
      state: "",
      isPlaying: null,
      isPaused: null,
      isFocused: null,
    };
  }

  function emptyReplay() {
    return {
      mapProgressMs: null,
      score: null,
      accuracy: null,
      ur: null,
      meanMs: null,
      medianMs: null,
      sdMs: null,
      earlyCount: null,
      lateCount: null,
      sampleCount: null,
      recentOffsets: [],
      columns: [],
      sections: [],
      insights: [],
      fidelity: "provisional",
      reason: "Live tosu telemetry is provisional; exact column and LN data requires an .osr file.",
      isProvisional: true,
      hasData: false,
    };
  }

  function clean(value) {
    return String(value == null ? "" : value).replace(/\s+/g, " ").trim();
  }

  function finiteNumber(value) {
    if (value == null || String(value).trim() === "") return null;
    const number = Number(String(value).replace(",", "."));
    return Number.isFinite(number) ? number : null;
  }

  function booleanValue(value) {
    if (typeof value === "boolean") return value;
    if (typeof value === "number" && (value === 0 || value === 1)) return value === 1;
    const token = clean(value).toLowerCase();
    if (["true", "1", "yes", "paused", "pause"].includes(token)) return true;
    if (["false", "0", "no", "playing", "play"].includes(token)) return false;
    return null;
  }

  function firstNumber(value) {
    const match = String(value || "").match(/[+-]?(?:\d+(?:[.,]\d+)?|[.,]\d+)/);
    return match ? finiteNumber(match[0]) : null;
  }

  function median(values) {
    if (!values.length) return null;
    const sorted = values.slice().sort((a, b) => a - b);
    const middle = Math.floor(sorted.length / 2);
    return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
  }

  function readLiveReplay(payload) {
    const sourceBeatmap = payload && payload.beatmap && typeof payload.beatmap === "object"
      ? payload.beatmap
      : {};
    const sourcePlay = payload && payload.play && typeof payload.play === "object"
      ? payload.play
      : {};
    const sourceTime = sourceBeatmap.time && typeof sourceBeatmap.time === "object"
      ? sourceBeatmap.time
      : {};
    const mapProgressMs = finiteNumber(sourceTime.live);
    const score = finiteNumber(sourcePlay.score);
    const accuracy = finiteNumber(sourcePlay.accuracy);
    const aggregateUr = finiteNumber(sourcePlay.unstableRate);
    const offsets = Array.isArray(sourcePlay.hitErrorArray)
      ? sourcePlay.hitErrorArray.map(finiteNumber).filter(value => value !== null)
      : [];
    const meanMs = offsets.length ? offsets.reduce((sum, value) => sum + value, 0) / offsets.length : null;
    const medianMs = median(offsets);
    const variance = offsets.length
      ? offsets.reduce((sum, value) => sum + Math.pow(value - meanMs, 2), 0) / offsets.length
      : null;
    const sdMs = variance === null ? null : Math.sqrt(variance);
    const ur = aggregateUr !== null && (aggregateUr !== 0 || offsets.length > 0)
      ? aggregateUr
      : (sdMs === null ? null : sdMs * 10);

    return {
      ...replay,
      mapProgressMs,
      score,
      accuracy,
      ur,
      meanMs,
      medianMs,
      sdMs,
      earlyCount: offsets.filter(value => value < 0).length,
      lateCount: offsets.filter(value => value > 0).length,
      sampleCount: offsets.length,
      recentOffsets: offsets.slice(-20),
      hasData: mapProgressMs !== null || score !== null || offsets.length > 0,
    };
  }

  function text(id) {
    const element = document.getElementById(id);
    return clean(element && element.textContent);
  }

  function readDifficulty() {
    const starElement = document.getElementById("rework-star");
    const meta = text("rework-meta");
    const lnMatch = meta.match(/LN\s*%?\s*[:=]?\s*([\d.,]+)\s*%?/i);
    const keysMatch = meta.match(/Keys?\s*[:=]?\s*(\d+)/i);

    return {
      starRating: firstNumber(starElement && starElement.textContent),
      starLabel: clean(starElement && starElement.textContent),
      unit: clean(starElement && starElement.getAttribute("data-unit")) || "SR",
      lnPercent: lnMatch ? finiteNumber(lnMatch[1]) : null,
      keys: keysMatch ? Number(keysMatch[1]) : null,
    };
  }

  function splitRanks(rawValue) {
    const normalized = clean(rawValue);
    let parts = String(rawValue || "")
      .split(/\s*\|\|\s*|\r?\n\s*(?=(?:[<>]\s+)?(?:LN\b|(?:[A-Za-z][\w./-]*\s+)?LN\b))/i)
      .map(clean)
      .filter(Boolean);

    if (parts.length < 2) {
      parts = normalized.split(/(?=\s+[<>]\s+LN(?:\s+DAN)?\b)/i).map(clean).filter(Boolean);
    }

    if (parts.length < 2) {
      const explicit = normalized.match(/^(.+?)\s+(?=LN(?:\s+DAN)?\b)(.+)$/i);
      const looksLikeRc = /^(?:[<>]\s*|(?:rc|reform|rework|regular|intro|alpha|beta|gamma|delta|epsilon|zeta|eta|theta|iota|kappa|cloverwisp|emik|thaumiel)\b)/i;
      if (explicit && looksLikeRc.test(clean(explicit[1]))) {
        parts = [clean(explicit[1]), clean(explicit[2])];
      }
    }

    let rc = parts.length ? parts[0] : "";
    let ln = parts.length > 1 ? clean(parts.slice(1).join(" || ")) : "";
    const looksLikeLn = /^(?:[<>]\s*)?(?:ln\b|(?:[A-Za-z][\w./-]*\s+)+ln\b)/i;
    if (parts.length < 2 && looksLikeLn.test(rc)) {
      ln = rc;
      rc = "";
    }

    function withoutPrefix(value) {
      return clean(value)
        .replace(/^(?:rc|ln)\b\s*(?:dan\b)?\s*[:\-]?\s*/i, "")
        .replace(/^([<>])\s*ln\b\s*(?:dan\b)?\s*[:\-]?\s*/i, "$1 ")
        .trim();
    }

    rc = withoutPrefix(rc);
    ln = withoutPrefix(ln);
    const missing = /^(?:—|-|n\/a|none)$/i;
    if (!rc || missing.test(rc)) rc = "—";
    if (!ln || missing.test(ln) || withoutPrefix(rc).toLowerCase() === withoutPrefix(ln).toLowerCase()) ln = "—";

    const numericCaption = text("est-diff-caption");
    const numericMatch = numericCaption.match(/\((?:RC\s*)?(-?\d+(?:[.,]\d+)?)\)/i);
    const numeric = numericMatch ? finiteNumber(numericMatch[1]) : null;

    return [
      { systemId: "rc-dan", label: "RC DAN", value: rc, numericValue: numeric },
      { systemId: "ln-dan", label: "LN DAN", value: ln, numericValue: null },
    ];
  }

  function absoluteSkillValue(row, valueElement, rawValue) {
    const simple = /^[+-]?(?:\d+(?:[.,]\d+)?|[.,]\d+)$/;
    if (simple.test(rawValue) && !rawValue.includes("%")) return finiteNumber(rawValue);

    const names = ["data-value", "data-amount", "data-score", "data-rating", "data-absolute", "aria-valuenow"];
    for (const node of [valueElement, row]) {
      if (!node) continue;
      for (const name of names) {
        const candidate = clean(node.getAttribute(name));
        if (simple.test(candidate) && !candidate.includes("%")) return finiteNumber(candidate);
      }
    }

    if (valueElement && valueElement.classList.contains("ett-skill-head") && !rawValue.includes("%")) {
      return firstNumber(rawValue);
    }
    return null;
  }

  function readSkills() {
    const patterns = document.getElementById("pattern-clusters");
    const etterna = document.getElementById("ett-skill-bars");
    const source = patterns && !patterns.hidden ? patterns : etterna && !etterna.hidden ? etterna : null;
    if (!source) return [];

    return Array.from(source.children)
      .filter((row) => !row.classList.contains("empty") && !row.classList.contains("skeleton"))
      .map((row, index) => {
        const labelElement = row.querySelector(".cluster-label,.ett-skill-label");
        const valueElement = row.querySelector(".cluster-subtype,.ett-skill-head");
        const fillElement = row.querySelector(".cluster-fill,.ett-skill-fill");
        if (!labelElement || !fillElement) return null;

        const rawWidth = fillElement.style.getPropertyValue("--bar-width")
          || getComputedStyle(fillElement).getPropertyValue("--bar-width")
          || fillElement.style.width
          || "0";
        const parsedWidth = Number.parseFloat(rawWidth);
        const normalizedValue = Number.isFinite(parsedWidth)
          ? Math.max(0, Math.min(100, parsedWidth))
          : 0;
        const valueLabel = clean(valueElement && valueElement.textContent);
        const value = absoluteSkillValue(row, valueElement, valueLabel);

        return {
          id: clean(row.getAttribute("data-skill-id")) || `skill-${index + 1}`,
          label: clean(labelElement.textContent) || "—",
          valueLabel,
          value,
          normalizedValue,
          detail: valueLabel,
        };
      })
      .filter(Boolean)
      .slice(0, 8);
  }

  function arrangeSourceDetails() {
    const card = document.querySelector(".main-card");
    if (!card) return;

    const root = document.documentElement;
    const needsDetailsHost = root.classList.contains("overlay-layout-horizontal")
      || root.classList.contains("overlay-layout-companella")
      || root.classList.contains("overlay-layout-companella-replay");
    let details = document.getElementById("overlay-host-details");

    if (needsDetailsHost) {
      if (!details) {
        details = document.createElement("div");
        details.id = "overlay-host-details";
        details.className = "overlay-host-details";
        const anchor = card.querySelector(".mode-tag-group");
        card.insertBefore(details, anchor);
      }

      ["sep-pattern", "pattern-clusters", "sep-etterna", "ett-skill-bars", "sep-graph", "body-graph-wrap"]
        .map(function (id) { return document.getElementById(id); })
        .filter(Boolean)
        .forEach(function (node) { details.appendChild(node); });
      return;
    }

    if (details) {
      while (details.firstChild) card.insertBefore(details.firstChild, details);
      details.remove();
    }
  }

  // Mania Map Analyser owns the source document and normally hides its card
  // on the osu! main menu. The application owns overlay visibility, so keep
  // the source host available and let the native overlay controller decide
  // whether the normalized domain snapshot is shown (for example, gameplay
  // is hidden by the native window). This source-specific compatibility code
  // deliberately stays inside the analyzer adapter.
  function keepSourceHostAvailable() {
    const card = document.querySelector(".main-card");
    if (!card) return;

    if (card.classList.contains("card-hidden-by-play")) {
      card.classList.remove("card-hidden-by-play");
    }

    if (card.getAttribute("aria-hidden") === "true") {
      card.removeAttribute("aria-hidden");
    }
  }

  function parsePlayHits(play) {
    const source = play && typeof play === "object" && play.hits && typeof play.hits === "object" ? play.hits : null;
    function pick(names) {
      if (!source) return null;
      for (const name of names) {
        if (Object.prototype.hasOwnProperty.call(source, name)) {
          const value = finiteNumber(source[name]);
          if (value !== null) return value;
        }
      }
      return null;
    }
    return {
      "300": pick(["300", "count300"]),
      "200": pick(["200", "count200"]),
      "100": pick(["100", "count100"]),
      "50": pick(["50", "count50"]),
      geki: pick(["geki", "countGeki", "gekis"]),
      katu: pick(["katu", "countKatu", "katus"]),
      miss: pick(["0", "miss", "countMiss", "count0"]),
      _raw: source,
    };
  }

  function sumHits(hits) {
    if (!hits) return null;
    const keys = ["300", "200", "100", "50", "geki", "katu"];
    let sum = 0;
    let has = false;
    for (const key of keys) {
      const value = hits[key];
      if (value !== null && Number.isFinite(value)) {
        sum += value;
        has = true;
      }
    }
    return has ? sum : null;
  }

  function createPauseCoach(currentReplay, play, currentHits) {
    const sampleCount = currentReplay.sampleCount;
    const meanMs = currentReplay.meanMs;
    const medianMs = currentReplay.medianMs;
    const unstableRate = currentReplay.ur;
    const earlyCount = currentReplay.earlyCount;
    const lateCount = currentReplay.lateCount;
    let earlyLateRatio = null;
    if (typeof earlyCount === "number" && typeof lateCount === "number" && earlyCount > 0 && lateCount > 0) {
      const ratio = earlyCount / lateCount;
      earlyLateRatio = Number.isFinite(ratio) ? ratio : null;
    }
    const driftMs = meanMs;
    const recentOffsets = Array.isArray(currentReplay.recentOffsets) ? currentReplay.recentOffsets.slice(-20) : [];
    const hasEnoughSamples = typeof sampleCount === "number" && sampleCount >= 10;
    let timingMargin = "unknown";
    if (hasEnoughSamples && unstableRate !== null && Number.isFinite(unstableRate)) {
      if (unstableRate < 80) timingMargin = "tight";
      else if (unstableRate < 140) timingMargin = "moderate";
      else timingMargin = "wide";
    }
    const wholeHits = sumHits(currentHits);
    const wholeMisses = currentHits ? currentHits.miss : null;
    const wholeAccuracy = finiteNumber(play && play.accuracy);
    const recentAccuracy = null;
    let recentHits = null;
    let recentMisses = null;
    if (currentHits) {
      if (wholeHits !== null) {
        if (lastPlayingHits) {
          const prevWhole = sumHits(lastPlayingHits);
          if (prevWhole !== null && Number.isFinite(prevWhole)) {
            const delta = wholeHits - prevWhole;
            recentHits = delta >= 0 ? delta : wholeHits;
          } else {
            recentHits = wholeHits;
          }
        } else {
          recentHits = 0;
        }
      }
      if (wholeMisses !== null) {
        if (lastPlayingHits && lastPlayingHits.miss !== null && Number.isFinite(lastPlayingHits.miss)) {
          const delta = wholeMisses - lastPlayingHits.miss;
          recentMisses = delta >= 0 ? delta : wholeMisses;
        } else if (lastPlayingHits) {
          recentMisses = wholeMisses;
        } else {
          recentMisses = 0;
        }
      }
    }
    if (recentHits !== null && !Number.isFinite(recentHits)) recentHits = null;
    if (recentMisses !== null && !Number.isFinite(recentMisses)) recentMisses = null;

    const mapProgressMs = currentReplay.mapProgressMs;
    const score = finiteNumber(play && play.score);
    const accuracy = wholeAccuracy;
    const health = finiteNumber(play && (play.health ?? play.hp ?? play.life));
    const combo = finiteNumber(play && (play.combo ?? play.currentCombo ?? play.comboCurrent));
    const maxCombo = finiteNumber(play && (play.maxCombo ?? play.maximumCombo ?? play.max_combo));
    const failed = play && typeof play.failed === "boolean" ? play.failed : false;
    let mods = [];
    if (play && play.mods !== undefined && play.mods !== null) {
      if (Array.isArray(play.mods)) {
        mods = play.mods.map(function (value) { return String(value).trim(); }).filter(Boolean).map(function (value) { return value.toUpperCase(); });
      } else if (typeof play.mods === "object" && Array.isArray(play.mods.array)) {
        mods = play.mods.array.map(function (entry) {
          if (entry && typeof entry === "object" && typeof entry.acronym === "string") return entry.acronym.trim().toUpperCase();
          return String(entry).trim().toUpperCase();
        }).filter(Boolean);
      } else {
        const raw = String(play.mods).trim();
        if (raw) {
          mods = raw.split(/[\s,;+]+/).map(function (value) { return value.trim().toUpperCase(); }).filter(Boolean);
        }
      }
    }

    const insights = [];
    if (hasEnoughSamples) {
      if (meanMs !== null && Math.abs(meanMs) > 8) {
        const direction = meanMs < 0 ? "early" : "late";
        insights.push({
          code: "pausecoach.timing.drift_" + direction,
          message: "Aggregate bias " + meanMs.toFixed(1) + "ms " + direction + " (n=" + sampleCount + ", provisional aggregate).",
          confidence: 0.6,
        });
      }
      if (unstableRate !== null && Number.isFinite(unstableRate) && unstableRate > 45) {
        insights.push({
          code: "pausecoach.timing.unstable",
          message: "UR " + unstableRate.toFixed(1) + " suggests unstable timing (n=" + sampleCount + ", provisional).",
          confidence: 0.55,
        });
      }
      if (earlyLateRatio !== null && Number.isFinite(earlyLateRatio) && (earlyLateRatio > 2 || earlyLateRatio < 0.5) && insights.length === 0) {
        insights.push({
          code: "pausecoach.timing.imbalance",
          message: "Early/late ratio " + earlyLateRatio.toFixed(2) + " (n=" + sampleCount + ", provisional).",
          confidence: 0.5,
        });
      }
      insights.splice(3);
    }

    const diagnostics = [];
    if (!(typeof sampleCount === "number" && sampleCount > 0)) {
      diagnostics.push("pausecoach.timing.no_offsets: HitErrorArray unavailable; timing stats suppressed.");
    }
    if (wholeHits === null && wholeMisses === null) {
      diagnostics.push("pausecoach.performance.no_counts: Cumulative judgement counts empty; totals provisional.");
    }

    return {
      fidelity: "provisional",
      isProvisional: true,
      reason: "Live aggregate only; per-column, per-object, finger and LN claims suppressed. Provisional timing from latest HitErrorArray and cumulative counts.",
      mapProgressMs: mapProgressMs,
      score: score,
      accuracy: accuracy,
      health: health,
      combo: combo,
      maxCombo: maxCombo,
      failed: failed,
      mods: mods,
      timing: {
        sampleCount: sampleCount,
        meanMs: meanMs,
        medianMs: medianMs,
        unstableRate: unstableRate,
        earlyLateRatio: earlyLateRatio,
        driftMs: driftMs,
        recentOffsets: recentOffsets,
        timingMargin: timingMargin,
      },
      performance: {
        wholeHits: wholeHits,
        wholeMisses: wholeMisses,
        wholeAccuracy: wholeAccuracy,
        recentAccuracy: recentAccuracy,
        recentHits: recentHits,
        recentMisses: recentMisses,
      },
      section: {},
      insights: insights,
      diagnostics: diagnostics,
    };
  }

  function buildSnapshot() {
    return {
      schemaVersion: SCHEMA_VERSION,
      sourceId: SOURCE_ID,
      beatmap,
      gameplay,
      difficulty: readDifficulty(),
      ranks: splitRanks(text("rework-diff")),
      skills: readSkills(),
      replay,
      pauseCoach,
    };
  }

  function sendToHost(message) {
    try {
      if (typeof window.__overlayHostSend === "function") {
        window.__overlayHostSend(message);
      } else if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === "function") {
        window.chrome.webview.postMessage(message);
      } else if (typeof invokeCSharpAction === "function") {
        invokeCSharpAction(message);
      }
    } catch (exception) {
      reportRuntimeError("Analyzer bridge message", exception);
    }
  }

  // Keep gameplay visibility independent from the DOM event listener. A
  // renderer can be reloaded while the analyzer adapter remains alive, so the
  // native host must receive the state directly as well as through the
  // browser event.
  function publishGameplayState() {
    if (typeof gameplay.isPlaying === "boolean") {
      sendToHost(`overlay:play:${gameplay.isPlaying ? "1" : "0"}`);
    }
    if (typeof gameplay.isPaused === "boolean") {
      sendToHost(`overlay:pause:${gameplay.isPaused ? "1" : "0"}`);
    }
  }

  function publishGameplayTrace(source, stateNumber, stateName, isPlaying, isPaused, isFocused) {
    try {
      sendToHost("overlay:state-debug:" + encodeURIComponent(JSON.stringify({
        source: source,
        name: stateName || "",
        number: stateNumber,
        isPlaying: typeof isPlaying === "boolean" ? isPlaying : null,
        isPaused: typeof isPaused === "boolean" ? isPaused : null,
        focused: typeof isFocused === "boolean" ? isFocused : null,
      })));
    } catch (exception) {
      reportRuntimeError("Publishing gameplay state trace", exception);
    }
  }

  function reportRuntimeError(operation, exception) {
    const message = exception && exception.message ? exception.message : String(exception || "Unknown runtime error");
    console.error(operation, exception);
    try {
      window.dispatchEvent(new CustomEvent("overlay:runtime-error", {
        detail: { operation: operation, message: message },
      }));
    } catch (dispatchException) {
      console.error("Dispatching overlay runtime error failed", dispatchException);
    }
  }

  function publish() {
    animationFrame = 0;
    publishTimer = 0;
    lastPublishAt = Date.now();
    const snapshot = buildSnapshot();
    const json = JSON.stringify(snapshot, function (_key, value) {
      return typeof value === "number" && !Number.isFinite(value) ? null : value;
    });
    if (json === lastSignature) return;
    lastSignature = json;
    window.dispatchEvent(new CustomEvent("analysis:snapshot", { detail: snapshot }));
    sendToHost(`analysis:${SOURCE_ID}:${json}`);
  }

  function queuePublish() {
    if (animationFrame || publishTimer) return;
    // Telemetry processing stays event-driven, but the DOM renderer only needs
    // a few updates per second while the player is actively playing. Pause and
    // results snapshots bypass the throttle for immediate feedback.
    const isActivePlay = gameplay.isPlaying === true && gameplay.isPaused !== true;
    const minimumInterval = isActivePlay ? 250 : 0;
    const elapsed = Date.now() - lastPublishAt;
    if (minimumInterval > elapsed) {
      publishTimer = window.setTimeout(queuePublish, minimumInterval - elapsed);
      return;
    }
    animationFrame = requestAnimationFrame(publish);
  }

  function readObjectNumber(source, names) {
    if (!source) return null;
    for (const name of names) {
      const value = finiteNumber(source[name]);
      if (value !== null) return value;
    }
    return null;
  }

  function bpmLabel(source, stats) {
    let value = source.bpm || source.BPM || (stats && (stats.bpm || stats.BPM));
    if (value && typeof value === "object") {
      const minimum = readObjectNumber(value, ["min", "minimum", "lowest"]);
      const maximum = readObjectNumber(value, ["max", "maximum", "highest"]);
      const common = readObjectNumber(value, ["common", "base", "current"]);
      if (minimum !== null && maximum !== null && Math.abs(minimum - maximum) > 0.1) return `${minimum}–${maximum}`;
      if (common !== null) return String(common);
      if (maximum !== null) return String(maximum);
      if (minimum !== null) return String(minimum);
    }
    const numeric = finiteNumber(value);
    return numeric !== null && numeric > 0 ? String(numeric) : "";
  }

  function applyTosuPayload(payload, source) {
    const previousGameplay = { ...gameplay };
    let beatmapIdentityChanged = false;
    const sourceBeatmap = payload && payload.beatmap;
    if (sourceBeatmap) {
      const metadata = sourceBeatmap.metadata || {};
      const stats = sourceBeatmap.stats || {};
      const id = String(sourceBeatmap.id || sourceBeatmap.beatmapId || "");
      const setId = String(sourceBeatmap.set || sourceBeatmap.setId || sourceBeatmap.beatmapSetId || "");
      if (id && beatmap.id && id !== beatmap.id) {
        replay = emptyReplay();
        beatmapIdentityChanged = true;
        pauseCoach = null;
        lastPlayingHits = null;
      }
      const identity = id || setId || `${metadata.artist || sourceBeatmap.artist || ""}-${metadata.title || sourceBeatmap.title || ""}-${sourceBeatmap.version || metadata.difficulty || metadata.version || ""}`;
      beatmap = {
        id,
        setId,
        artist: clean(sourceBeatmap.artist || metadata.artist),
        title: clean(sourceBeatmap.title || metadata.title),
        version: clean(sourceBeatmap.version || metadata.difficulty || metadata.version),
        mapper: clean(sourceBeatmap.mapper || metadata.mapper || metadata.creator),
        bpmLabel: bpmLabel(sourceBeatmap, stats),
        overallDifficulty: readObjectNumber(stats, ["OD", "od", "overallDifficulty"]),
        healthDrain: readObjectNumber(stats, ["HP", "hp", "drainRate"]),
        backgroundUrl: identity
          ? `http://${location.host}/files/beatmap/background?ts=${encodeURIComponent(identity)}`
          : "",
      };
    }

    replay = readLiveReplay(payload);
    const sourcePlayForCoach = payload && payload.play && typeof payload.play === "object" ? payload.play : {};
    const currentHits = parsePlayHits(sourcePlayForCoach);

    const rawState = payload && payload.state;
    const state = rawState && typeof rawState === "object" ? rawState : null;
    const game = payload && payload.game && typeof payload.game === "object" ? payload.game : null;
    const stateName = clean(state ? state.name : rawState).toLowerCase();
    const stateToken = stateName.replace(/[^a-z]/g, "");
    const stateNumber = finiteNumber(state && state.number) ?? finiteNumber(rawState);
    // The name is the safest discriminator when both fields are present: it
    // describes the state emitted by the running osu! client, while numeric
    // values can differ between older stable integrations. Use the numeric
    // enum only when the name is missing or unknown.
    const namedPlaying = ["play", "gameplay", "playing", "spectating", "watchingreplay", "replay"].includes(stateToken);
    const namedNonPlaying = ["menu", "edit", "selectplay", "selectedit", "selectdrawings", "resultscreen", "result", "options", "songselect"].includes(stateToken);
    const isPlaying = namedPlaying
      ? true
      : namedNonPlaying
        ? false
        : stateNumber !== null
          ? stateNumber === 2
          : false;
    const hasState = Boolean(stateToken) || stateNumber !== null;
    const nextState = hasState ? stateName : gameplay.state;
    const nextIsPlaying = hasState ? isPlaying : gameplay.isPlaying;
    const pauseCandidates = [
      game && game.paused,
      game && game.isPaused,
      state && state.paused,
      state && state.isPaused,
      payload && payload.paused,
      payload && payload.isPaused,
    ];
    const explicitPause = pauseCandidates.map(booleanValue).find(value => value !== null);
    if (source === "browser-http" && explicitPause !== undefined) {
      httpPauseState = explicitPause;
    }
    const websocketPauseStale = source !== "browser-http"
      && explicitPause === false
      && httpPauseState === true;
    const nextIsPaused = stateToken === "pause" || stateToken === "paused" || stateToken === "break"
      ? true
      : websocketPauseStale ? true
      : explicitPause !== undefined ? explicitPause : gameplay.isPaused;
    const isFocused = game && typeof game.focused === "boolean" ? game.focused : gameplay.isFocused;
    gameplay = {
      state: nextState,
      isPlaying: nextIsPlaying,
      isPaused: nextIsPaused,
      isFocused,
    };

    const isNewPlayingAttempt = gameplay.isPlaying === true && gameplay.isPaused !== true && previousGameplay.isPlaying !== true;
    if (beatmapIdentityChanged || isNewPlayingAttempt) {
      pauseCoach = null;
      lastPlayingHits = null;
    }

    if (pauseCoachRuntime) {
      pauseCoach = pauseCoachRuntime.process({
        beatmap: {
          id: beatmap.id,
          setId: beatmap.setId,
          hash: sourceBeatmap && (sourceBeatmap.hash || sourceBeatmap.md5 || sourceBeatmap.checksum) || "",
          time: sourceBeatmap && sourceBeatmap.time,
        },
        gameplay: {
          state: nextState,
          isPlaying: nextIsPlaying,
          isPaused: nextIsPaused,
          isFocused,
          isReplay: stateToken === "replay" || stateToken === "watchingreplay",
          isSpectating: stateToken === "spectating",
        },
        play: sourcePlayForCoach,
        mapTimeMs: replay.mapProgressMs,
      });
    } else if (gameplay.isPlaying === true && gameplay.isPaused !== true) {
      lastPlayingHits = currentHits;
      pauseCoach = null;
    } else if (gameplay.isPaused === true && previousGameplay.isPlaying === true && previousGameplay.isPaused !== true) {
      pauseCoach = createPauseCoach(replay, sourcePlayForCoach, currentHits);
    } else if (gameplay.isPaused === true) {
      if (!pauseCoach) pauseCoach = createPauseCoach(replay, sourcePlayForCoach, currentHits);
    } else if (gameplay.isPlaying !== true) {
      pauseCoach = null;
    }

    publishGameplayTrace(source || "unknown", stateNumber, stateName, nextIsPlaying, nextIsPaused, isFocused);
    keepSourceHostAvailable();
    window.dispatchEvent(new CustomEvent("overlay:gameplay-state", { detail: gameplay }));
    publishGameplayState();
    queuePublish();
  }

  // Some tosu versions do not deliver state-only changes to an overlay
  // websocket after the page has been reloaded. Keep a small HTTP fallback so
  // visibility does not depend on one websocket delta reaching the host.
  async function pollState() {
    if (disposed || statePollInFlight) return;
    statePollInFlight = true;
    try {
      const response = await fetch(`/json/v2?mma_state=${Date.now()}`, {
        cache: "no-store",
      });
      if (!response.ok) return;
      applyTosuPayload(await response.json(), "browser-http");
    } catch (exception) {
      const now = Date.now();
      if (now - lastStatePollWarningAt >= 5000) {
        lastStatePollWarningAt = now;
        console.warn("tosu state polling failed", exception);
      }
    } finally {
      statePollInFlight = false;
    }
  }

  function startStatePolling() {
    if (statePollTimer) window.clearInterval(statePollTimer);
    statePollTimer = window.setInterval(pollState, 400);
    pollState();
  }

  function connect() {
    if (disposed) return;
    if (socket) {
      try { socket.close(); } catch (exception) { reportRuntimeError("Closing analyzer websocket", exception); }
    }

    socket = new WebSocket(`ws://${location.host}/websocket/v2?l=${encodeURIComponent(window.COUNTER_PATH || location.pathname)}`);
    socket.addEventListener("open", function () {
      socket.send("applyFilters:" + JSON.stringify([
        { field: "state", keys: ["number", "name"] },
        { field: "game", keys: ["focused", "paused"] },
        {
          field: "beatmap",
          keys: ["artist", "title", "version", "mapper", "id", "set", "setId", "beatmapSetId", "metadata", "stats", "bpm", "time"],
        },
        {
          field: "play",
          keys: ["score", "accuracy", "combo", "maxCombo", "mods", "health", "hp", "failed", "hits", "hitErrorArray", "unstableRate"],
        },
      ]));
    });
    socket.addEventListener("message", function (event) {
      try { applyTosuPayload(JSON.parse(event.data), "websocket"); }
      catch (exception) { reportRuntimeError("Processing analyzer websocket payload", exception); }
    });
    socket.addEventListener("close", function () {
      if (disposed) return;
      // A disconnected stream is unknown, not a known menu state. Preserve
      // the last play state so reconnects cannot briefly show the overlay
      // while a map is still running; focus is cleared so the cursor remains
      // available while osu! state is unavailable.
      gameplay = { ...gameplay, isPlaying: null, isFocused: false };
      window.dispatchEvent(new CustomEvent("overlay:gameplay-state", { detail: gameplay }));
      reconnectTimer = window.setTimeout(connect, 1000);
    });
  }

  observer = new MutationObserver(function () {
    keepSourceHostAvailable();
    queuePublish();
  });
  const observedRoot = document.querySelector(".main-card") || document.body || document.documentElement;
  arrangeSourceDetails();
  keepSourceHostAvailable();
  observer.observe(observedRoot, {
    attributes: true,
    childList: true,
    characterData: true,
    subtree: true,
  });

  connect();
  startStatePolling();
  queuePublish();
  window.setTimeout(queuePublish, 120);
  window.setTimeout(queuePublish, 600);

  window.__overlayAnalyzerAdapter = {
    id: SOURCE_ID,
    schemaVersion: SCHEMA_VERSION,
    dispose: function () {
      disposed = true;
      if (observer) observer.disconnect();
      if (animationFrame) cancelAnimationFrame(animationFrame);
      if (publishTimer) clearTimeout(publishTimer);
      if (reconnectTimer) clearTimeout(reconnectTimer);
      if (statePollTimer) window.clearInterval(statePollTimer);
      if (socket) {
        try { socket.close(); } catch (exception) { reportRuntimeError("Disposing analyzer websocket", exception); }
      }
      // Do not dispose the singleton coach here: setup/resize re-injects the
      // adapter without navigating the WebView, and its bounded session must
      // survive that lifecycle event. A full document navigation recreates
      // the window and releases it naturally.
      pauseCoachRuntime = null;
      observer = null;
      socket = null;
      animationFrame = 0;
      publishTimer = 0;
      reconnectTimer = 0;
      statePollTimer = 0;
      statePollInFlight = false;
      httpPauseState = null;
    },
  };
})();
