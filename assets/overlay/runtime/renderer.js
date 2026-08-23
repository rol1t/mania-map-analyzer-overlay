(function () {
  "use strict";

  if (window.__overlaySnapshotRendererBound) {
    if (window.__overlayLatestAnalysisSnapshot && typeof window.__overlayRenderAnalysisSnapshot === "function") {
      // A preset change can replace the host DOM while keeping this runtime
      // alive. Force one render in that case even when the data signature is
      // unchanged.
      window.__overlayRenderAnalysisSnapshot(window.__overlayLatestAnalysisSnapshot, true);
    }
    return;
  }
  window.__overlaySnapshotRendererBound = true;

  function byId(id) {
    return document.getElementById(id);
  }

  function text(id, value, fallback) {
    const element = byId(id);
    if (!element) return;
    const next = String(value == null || value === "" ? fallback : value);
    if (element.textContent !== next) element.textContent = next;
  }

  function formatNumber(value, maximumFractionDigits) {
    const number = Number(value);
    if (!Number.isFinite(number)) return "";
    return number.toFixed(maximumFractionDigits).replace(/\.0+$|(?<=\.\d)0+$/g, "");
  }

  function formatAccuracyPercentage(value) {
    const number = Number(value);
    if (!Number.isFinite(number)) return "";
    // Native realtime snapshots use the canonical 0..1 fraction while some
    // adapter/replay snapshots already contain a 0..100 percentage.
    const percentage = Math.abs(number) <= 1.000001 ? number * 100 : number;
    return formatNumber(percentage, 2) + "%";
  }

  function rank(snapshot, systemId) {
    return (snapshot.ranks || []).find(function (entry) {
      return String(entry.systemId || "").toLowerCase() === systemId;
    });
  }

  function beatmapKey(snapshot) {
    const beatmap = snapshot && snapshot.beatmap || {};
    const id = String(beatmap.id || "").trim().toLowerCase();
    if (id) return `id:${id}`;
    const setId = String(beatmap.setId || "").trim().toLowerCase();
    const version = String(beatmap.version || "").trim().toLowerCase();
    if (setId) return `set:${setId}|${version}`;
    const values = [beatmap.artist, beatmap.title, beatmap.version]
      .map(function (value) { return String(value || "").trim().toLowerCase(); });
    return values.some(Boolean) ? values.join("|") : "";
  }

  function rankHasValue(entry) {
    const value = String(entry && entry.value || "").trim();
    return value !== "" && value !== "—" && value !== "-";
  }

  function hasText(value) {
    return value != null && String(value).trim() !== "";
  }

  function mergeBeatmap(previous, current) {
    if (!previous || !current) return current || previous || {};
    const merged = Object.assign({}, previous, current);
    ["id", "setId", "artist", "title", "version", "mapper", "bpmLabel",
      "overallDifficulty", "healthDrain", "backgroundUrl"].forEach(function (key) {
      if (!hasText(current[key]) && hasText(previous[key])) merged[key] = previous[key];
    });
    return merged;
  }

  function mergeDifficulty(previous, current) {
    if (!previous || !current) return current || previous || {};
    const merged = Object.assign({}, previous, current);
    const currentStar = Number(current.starRating);
    const previousStar = Number(previous.starRating);
    // Tosu realtime frames do not carry headless difficulty metrics and are
    // serialized as a zero/empty difficulty block. Keep completed analysis
    // values for the same beatmap instead of displaying `0 SR`.
    if ((!Number.isFinite(currentStar) || currentStar <= 0) && Number.isFinite(previousStar) && previousStar > 0) {
      merged.starRating = previous.starRating;
      if (!hasText(current.starLabel) && hasText(previous.starLabel)) merged.starLabel = previous.starLabel;
    }
    ["starLabel", "unit", "lnPercent", "keys"].forEach(function (key) {
      const value = current[key];
      if ((value == null || (typeof value === "string" && value.trim() === "")) && previous[key] != null) {
        merged[key] = previous[key];
      }
    });
    return merged;
  }

  function isNativePauseCoachSnapshot(snapshot) {
    return !!(snapshot && (snapshot.nativePauseCoach === true
      || snapshot.extensions && snapshot.extensions.nativePauseCoach === true));
  }

  function realtimeProducer(snapshot) {
    if (!snapshot) return "";
    if (isNativePauseCoachSnapshot(snapshot)) return "native";
    const extensions = snapshot.extensions || {};
    return String(snapshot.realtimeProducer || extensions.realtimeProducer || "")
      .trim()
      .toLowerCase();
  }

  function isRealtimeSnapshot(snapshot) {
    if (!snapshot) return false;
    if (isNativePauseCoachSnapshot(snapshot)) return true;
    if (snapshot.pauseCoach && snapshot.pauseCoach.hasData === true) return true;
    // The browser adapter marks all Tosu live replay containers as
    // provisional, including the first id-only frame after navigation.
    // Headless analysis snapshots intentionally do not set this flag.
    return !!(snapshot.replay && snapshot.replay.isProvisional === true);
  }

  function isReplayedHeadlessSnapshot(snapshot) {
    return !!(snapshot && snapshot.extensions && snapshot.extensions.headlessReplay === true);
  }

  function mergeSnapshot(snapshot) {
    var nativeMarker = isNativePauseCoachSnapshot(snapshot);
    if (nativeMarker) {
      window.__overlayNativePauseCoachSnapshot = snapshot;
    }
    const nativeSnapshot = window.__overlayNativePauseCoachSnapshot;
    if (nativeSnapshot && nativeSnapshot.pauseCoach && snapshot && snapshot !== nativeSnapshot) {
      const nativeKey = beatmapKey(nativeSnapshot);
      let currentKey = beatmapKey(snapshot);
      const nativeId = String(nativeSnapshot.beatmap && nativeSnapshot.beatmap.id || "").trim().toLowerCase();
      const currentId = String(snapshot.beatmap && snapshot.beatmap.id || "").trim().toLowerCase();
      const nativeSession = String(nativeSnapshot.pauseCoach.sessionId || "").trim();
      const currentSession = String(snapshot.pauseCoach && snapshot.pauseCoach.sessionId || "").trim();
      const nativeProducer = realtimeProducer(nativeSnapshot);
      const currentProducer = realtimeProducer(snapshot);
      // sourceId identifies the analyzer, not the producer. Native C# and
      // browser adapter sessions are generated independently even though both
      // production snapshots use sourceId=mania-map-analyser. Only a newer
      // native snapshot may establish a different native attempt.
      const sessionsAreComparable = currentProducer === "native"
        && nativeProducer === "native"
        && isNativePauseCoachSnapshot(snapshot);
      const positiveDifferentBeatmap = (nativeId && currentId && nativeId !== currentId)
        || (!nativeId && !currentId && nativeKey && currentKey && nativeKey !== currentKey);
      const positiveDifferentAttempt = positiveDifferentBeatmap
        || (sessionsAreComparable && nativeSession && currentSession && nativeSession !== currentSession);
      // The native collector is authoritative for realtime coaching for the
      // current attempt. Browser frames are still useful for map metadata,
      // but must not replace native pause/gameplay/replay values for the same
      // map: the two sources use different rolling windows and alternating
      // them makes the visible card jump backwards and forwards. A positive
      // map transition releases native authority; the next native frame then
      // establishes the new attempt again.
      const currentMissingBeatmapIdentity = !currentKey;
      if (positiveDifferentAttempt) {
        // A positive map/session transition releases the previous native
        // authority. The next native marker (if any) will establish it again.
        window.__overlayNativePauseCoachSnapshot = null;
      } else if (currentMissingBeatmapIdentity) {
        snapshot = Object.assign({}, snapshot, {
          beatmap: mergeBeatmap(snapshot.beatmap, nativeSnapshot.beatmap),
          pauseCoach: nativeSnapshot.pauseCoach,
          replay: nativeSnapshot.replay || snapshot.replay,
          gameplay: nativeSnapshot.gameplay || snapshot.gameplay,
        });
        currentKey = beatmapKey(snapshot);
      } else if ((nativeKey && currentKey && nativeKey === currentKey) ||
                 (nativeId && currentId && nativeId === currentId)) {
        // Keep browser metadata, but retain the native rolling-window values
        // until the collector confirms a new map/attempt. This makes preview
        // and overlay presentation deterministic even though both producers
        // continue to run concurrently.
        snapshot = Object.assign({}, snapshot, {
          beatmap: mergeBeatmap(nativeSnapshot.beatmap, snapshot.beatmap),
          pauseCoach: nativeSnapshot.pauseCoach || snapshot.pauseCoach,
          replay: nativeSnapshot.replay || snapshot.replay,
          gameplay: nativeSnapshot.gameplay || snapshot.gameplay,
        });
        currentKey = beatmapKey(snapshot);
      }
    }
    const previous = window.__overlayLatestAnalysisSnapshot;
    const previousKey = beatmapKey(previous);
    const currentKey = beatmapKey(snapshot);
    const currentMissingBeatmapIdentity = !currentKey;
    // A completed headless analysis can finish after a map switch and after
    // the adapter has already published the new live map. The old result is
    // still a valid snapshot, but it is no longer the current presentation.
    // Do not let that late, non-realtime frame roll the widget back to the
    // previous map. A new live/native frame remains allowed to establish the
    // next map because it carries positive realtime evidence.
    if (previousKey && currentKey && previousKey !== currentKey
        && isRealtimeSnapshot(previous) && isReplayedHeadlessSnapshot(snapshot)) {
      return previous;
    }
    // Headless snapshots can arrive without beatmap metadata while the Tosu
    // adapter still owns the live identity. Preserve live blocks during that
    // short gap; only discard them when both snapshots identify different
    // maps.
    if (!previous || (previousKey && currentKey && previousKey !== currentKey)) {
      return snapshot;
    }

    const previousRanks = Array.isArray(previous.ranks) ? previous.ranks : [];
    const currentRanks = Array.isArray(snapshot.ranks) ? snapshot.ranks : [];
    const merged = Object.assign({}, snapshot);
    merged.beatmap = mergeBeatmap(previous.beatmap, snapshot.beatmap);
    merged.difficulty = mergeDifficulty(previous.difficulty, snapshot.difficulty);
    if ((!Array.isArray(snapshot.skills) || snapshot.skills.length === 0) &&
        Array.isArray(previous.skills) && previous.skills.length > 0) {
      // Realtime Tosu frames do not include headless skill metrics. Keep the
      // completed chart while the live Pause Coach block continues updating.
      merged.skills = previous.skills;
    }
    if (previousRanks.length > 0 || currentRanks.length > 0) {
      const ranks = new Map();
      previousRanks.forEach(function (entry) {
        const id = String(entry && entry.systemId || "").toLowerCase();
        if (id) ranks.set(id, entry);
      });
      currentRanks.forEach(function (entry) {
        const id = String(entry && entry.systemId || "").toLowerCase();
        if (id && (!ranks.has(id) || rankHasValue(entry))) ranks.set(id, entry);
      });

      // Suppress stale LN DAN for maps without long notes.
      var currentLnPercent = snapshot && snapshot.difficulty ? snapshot.difficulty.lnPercent : null;
      var hasCurrentLn = currentLnPercent != null && Number(currentLnPercent) > 0;
      if (!hasCurrentLn) {
        ranks.delete("ln-dan");
      }
      merged.ranks = Array.from(ranks.values());
    }

    // The adapter may publish provisional tosu telemetry before the headless
    // snapshot arrives. MMA does not know replay data, so preserve the live
    // block for the same beatmap instead of flashing it off.
    const previousReplayIsExact = previous.replay
      && previous.replay.hasData
      && previous.replay.isProvisional !== true
      && String(previous.replay.fidelity || "").toLowerCase() !== "provisional";
    const incomingReplayIsProvisional = snapshot.replay && snapshot.replay.isProvisional === true;
    if (previousReplayIsExact && incomingReplayIsProvisional) {
      // Live polling continues after an explicit .osr import. Keep the exact
      // post-play result until another exact replay is imported.
      merged.replay = previous.replay;
    } else if ((!snapshot.replay || !snapshot.replay.hasData) && previous.replay && previous.replay.hasData) {
      merged.replay = previous.replay;
    }
    // Headless/map snapshots do not own the live Pause Coach state. Preserve
    // the latest attempt diagnosis while those snapshots continue to arrive.
    if (!snapshot.pauseCoach && previous.pauseCoach) {
      merged.pauseCoach = previous.pauseCoach;
    }
    if (currentMissingBeatmapIdentity && previousKey && previousKey !== currentKey) {
      // A navigation/headless frame can briefly omit beatmap metadata while
      // the live adapter is still publishing the current attempt. Do not let
      // that partial frame reset a visible coach card to WaitingForGame or
      // erase the latest gameplay/replay values.
      if (previous.pauseCoach) merged.pauseCoach = previous.pauseCoach;
      if (previous.gameplay) merged.gameplay = previous.gameplay;
      if (previous.replay) merged.replay = previous.replay;
    }
    return merged;
  }

  function renderSummary(snapshot) {
    const difficulty = snapshot.difficulty || {};
    const beatmap = snapshot.beatmap || {};
    const rc = rank(snapshot, "rc-dan") || {};
    const ln = rank(snapshot, "ln-dan") || {};

    const star = difficulty.starLabel || formatNumber(difficulty.starRating, 2) || "—";
    const unit = difficulty.unit || "SR";
    const starWithUnit = star === "—" ? star : (star.trim().toLowerCase().endsWith(unit.toLowerCase()) ? star : `${star} ${unit}`);
    text("overlay-summary-star", starWithUnit, "—");

    const lnValue = difficulty.lnPercent == null ? Number.NaN : Number(difficulty.lnPercent);
    const lnLabel = Number.isFinite(lnValue) ? `${formatNumber(lnValue, 1)}%` : "—";
    const keyCount = difficulty.keys == null ? Number.NaN : Number(difficulty.keys);
    const keys = Number.isFinite(keyCount) ? String(difficulty.keys) : "—";
    text("overlay-summary-star-meta", `LN%: ${lnLabel} · Keys: ${keys}`, "LN — · Keys —");
    text("overlay-summary-bpm", beatmap.bpmLabel, "—");
    text("overlay-summary-set", beatmap.setId, "—");
    text("overlay-summary-map", beatmap.id, "—");
    text("overlay-summary-rc-dan", rc.value, "—");
    text("overlay-summary-ln-dan", ln.value, "—");
    const rcNumericValue = rc.numericValue == null ? Number.NaN : Number(rc.numericValue);
    text("overlay-summary-rc-dan-value",
      Number.isFinite(rcNumericValue) ? `≈ ${rcNumericValue.toFixed(2)}` : "—",
      "—");

    text("overlay-comp-mapper", beatmap.mapper ? `Mapped by ${beatmap.mapper}` : "Mapper —", "Mapper —");
    text("overlay-comp-version", beatmap.version ? ` · [${beatmap.version}]` : "", "");

    if (beatmap.backgroundUrl) {
      const safeUrl = String(beatmap.backgroundUrl).replace(/"/g, "\\\"");
      document.documentElement.style.setProperty("--overlay-comp-cover", `url("${safeUrl}")`);
    } else {
      document.documentElement.style.removeProperty("--overlay-comp-cover");
    }
  }

  function renderSkills(snapshot) {
    const chart = byId("overlay-comp-chart");
    if (!chart) return;

    const skills = Array.isArray(snapshot.skills) ? snapshot.skills.slice(0, 8) : [];
    chart.textContent = "";
    chart.hidden = skills.length === 0;
    chart.style.setProperty("--overlay-comp-count", String(Math.max(1, skills.length)));
    const rootStyle = getComputedStyle(document.documentElement);

    skills.forEach(function (skill, index) {
      const normalized = Math.max(0, Math.min(100, Number(skill.normalizedValue) || 0));
      const color = rootStyle.getPropertyValue(`--overlay-comp-color-${index + 1}`).trim() || "#69ced1";
      const value = skill.value == null || skill.value === "" ? Number.NaN : Number(skill.value);
      const displayValue = Number.isFinite(value)
        ? formatNumber(value, 2)
        : `${Math.round(normalized)}%`;

      const column = document.createElement("div");
      column.className = "overlay-comp-column";

      const box = document.createElement("div");
      box.className = "overlay-comp-barbox";

      const bar = document.createElement("div");
      bar.className = "overlay-comp-bar";
      bar.style.setProperty("--overlay-value", `${Math.max(2, normalized)}%`);
      bar.style.setProperty("--overlay-color", color);

      const number = document.createElement("div");
      number.className = "overlay-comp-number" + (normalized < 24 ? " overlay-comp-number-outside" : "");
      number.textContent = displayValue;
      number.title = displayValue;
      bar.appendChild(number);
      box.appendChild(bar);

      const label = document.createElement("div");
      label.className = "overlay-comp-label";
      label.textContent = skill.label || "—";

      column.appendChild(box);
      column.appendChild(label);

      const detail = String(skill.detail || skill.valueLabel || "").trim();
      if (detail && detail !== displayValue) {
        const detailElement = document.createElement("div");
        detailElement.className = "overlay-comp-detail";
        detailElement.textContent = detail;
        detailElement.title = detail;
        column.appendChild(detailElement);
      }

      chart.appendChild(column);
    });
  }

  function renderReplay(snapshot) {
    const replay = snapshot.replay;
    const hasReplayNodes = byId("overlay-replay") || byId("overlay-replay-ur") || byId("overlay-replay-insights");
    const isReplayPreset = document.documentElement.classList.contains("overlay-layout-companella-replay");
    if (!replay && !hasReplayNodes && !isReplayPreset) {
      const container = byId("overlay-replay");
      if (container) container.hidden = true;
      return;
    }
    const container = byId("overlay-replay");
    if (container) container.hidden = false;
    // For companella-replay without data, fabricate empty replay so block stays visible as demo
    const effectiveReplay = replay || (isReplayPreset ? { hasData: false, ur: null, meanMs: null, medianMs: null, sampleCount: null, earlyCount: null, lateCount: null, fidelity: "", reason: "", columns: [], insights: [{ code: "demo", message: "No .osr yet — play a map to see UR/bias per column" }], hasData: false } : null);
    if (!effectiveReplay) return;
    const r = effectiveReplay;

    function fmt(value, digits) {
      return formatNumber(value, digits) || "—";
    }

    text("overlay-replay-ur", r.ur == null ? "—" : fmt(r.ur, 1), "—");
    text("overlay-replay-score", r.score == null ? "—" : String(r.score), "—");
    text("overlay-replay-map-time", r.mapProgressMs == null ? "—" : fmt(r.mapProgressMs, 0) + " ms", "—");
    text("overlay-replay-accuracy", r.accuracy == null ? "—" : formatAccuracyPercentage(r.accuracy), "—");
    text("overlay-replay-mean", r.meanMs == null ? "—" : fmt(r.meanMs, 1) + " ms", "—");
    text("overlay-replay-median", r.medianMs == null ? "—" : fmt(r.medianMs, 1) + " ms", "—");
    text("overlay-replay-sample", r.sampleCount == null ? "—" : String(r.sampleCount), "—");
    text("overlay-replay-fidelity", r.fidelity ? r.fidelity.replace("replay.fidelity.", "") : (r.hasData ? "exact" : ""), "");

    const early = r.earlyCount, late = r.lateCount;
    const earlyLate = early != null || late != null ? `${early ?? 0} / ${late ?? 0}` : "—";
    text("overlay-replay-earlylate", earlyLate, "—");

    const colChart = byId("overlay-replay-columns");
    if (colChart) {
      colChart.textContent = "";
      const cols = Array.isArray(r.columns) ? r.columns : [];
      colChart.hidden = cols.length === 0;
      cols.forEach(function (col) {
        const item = document.createElement("div");
        item.className = "overlay-replay-column";
        const label = document.createElement("span");
        label.className = "overlay-replay-col-label";
        label.textContent = `C${col.column + 1}`;
        const bias = document.createElement("span");
        bias.className = "overlay-replay-col-bias";
        bias.textContent = col.biasMs == null ? "—" : fmt(col.biasMs, 1);
        bias.title = col.biasMs == null ? "" : `bias ${fmt(col.biasMs,1)}ms`;
        const ur = document.createElement("span");
        ur.className = "overlay-replay-col-ur";
        ur.textContent = col.ur == null ? "—" : fmt(col.ur, 0);
        item.append(label, bias, ur);
        colChart.appendChild(item);
      });
    }

    const insightsEl = byId("overlay-replay-insights");
    if (insightsEl) {
      const insights = Array.isArray(r.insights) ? r.insights : [];
      insightsEl.textContent = "";
      insightsEl.hidden = insights.length === 0;
      insights.forEach(function (insight) {
        const line = document.createElement("div");
        line.className = "overlay-replay-insight";
        line.textContent = insight.message || String(insight.code || "");
        line.title = insight.message || "";
        insightsEl.appendChild(line);
      });
      if (insights.length === 0 && r.reason) {
        const line = document.createElement("div");
        line.className = "overlay-replay-insight overlay-replay-reason";
        line.textContent = r.reason;
        insightsEl.appendChild(line);
        insightsEl.hidden = false;
      }
    }
  }

  function renderPauseCoach(snapshot) {
    const container = byId("overlay-pause-coach");
    if (!container) return;

    const pc = snapshot && snapshot.pauseCoach;
    if (!pc) {
      container.hidden = true;
      return;
    }

    const timing = pc.timing || {};
    const performance = pc.performance || {};
    const overall = pc.overall || {};
    const recent = pc.recent || {};
    const state = String(pc.state || "").toLowerCase();
    const hasMeaningfulData = pc.hasData !== false || timing.sampleCount || (pc.insights || []).length;
    container.hidden = !hasMeaningfulData && state === "unavailable";
    container.dataset.state = state || "unknown";
    container.dataset.quality = String(pc.dataQuality || "Unavailable").toLowerCase();

    function fmt(value, digits) {
      return formatNumber(value, digits) || "—";
    }

    const stateLabels = {
      waitingforgame: "Waiting for a play",
      playing: "Analyzing current attempt",
      paused: "Paused — diagnosis ready",
      insufficientdata: "Paused — not enough telemetry",
      ready: "Attempt complete",
      unavailable: "Unavailable for this mode",
    };
    const statusLabel = stateLabels[state] || (pc.fidelity || "provisional");
    text("overlay-pause-status", statusLabel, "—");
    text("overlay-pause-coach-subtitle", pc.reason || "Deterministic, evidence-backed realtime analysis", "—");

    const fidelityRaw = pc.fidelity ? String(pc.fidelity) : (pc.isProvisional ? "provisional" : "");
    const fidelityLabel = fidelityRaw ? fidelityRaw.replace("replay.fidelity.", "") : "";
    const marginRaw = timing.timingMargin != null ? timing.timingMargin : timing.margin;
    const margin = String(marginRaw || "").trim().toLowerCase();

    const bias = timing.meanMs != null ? timing.meanMs : timing.driftMs;
    text("overlay-pause-bias", bias == null ? "—" : fmt(bias, 1) + " ms", "—");

    text("overlay-pause-ur", timing.unstableRate == null ? "—" : fmt(timing.unstableRate, 1), "—");

    // Pause Coach owns canonical accuracy (0..1). The renderer deliberately
    // does not infer whether a value is a fraction or a percentage.
    const accuracyValue = recent.accuracy ?? performance.recentAccuracy ?? overall.accuracy ?? pc.accuracy;
    text("overlay-pause-accuracy", accuracyValue != null ? fmt(Number(accuracyValue) * 100, 2) + "%" : "—", "—");
    text("overlay-pause-score", pc.score ?? overall.score, "—");
    text("overlay-pause-combo", pc.combo ?? overall.combo, "—");

    const section = pc.section || {};
    text("overlay-pause-section", section.label || section.dominantPatternKind || "—", "—");

    let recentText = "—";
    if (performance.recentHits != null || performance.recentMisses != null) {
      const rh = performance.recentHits != null ? String(performance.recentHits) : "0";
      const rm = performance.recentMisses != null ? String(performance.recentMisses) : "0";
      recentText = rh + " / " + rm;
    } else if (performance.wholeHits != null || performance.wholeMisses != null) {
      const wh = performance.wholeHits != null ? String(performance.wholeHits) : "0";
      const wm = performance.wholeMisses != null ? String(performance.wholeMisses) : "0";
      recentText = wh + " / " + wm;
    }
    text("overlay-pause-recent", recentText, "—");

    const primary = byId("overlay-pause-coach-primary");
    const secondary = byId("overlay-pause-coach-secondary");
    const legacyInsightsLayout = document.documentElement.classList.contains("overlay-layout-companella-replay");
    if (primary) {
      primary.textContent = "";
      const insights = Array.isArray(pc.insights) ? pc.insights : [];
      const first = insights[0];
      primary.hidden = legacyInsightsLayout || !first || state === "waitingforgame";
      if (first) {
        primary.dataset.severity = String(first.severity || "info").toLowerCase();
        const title = document.createElement("strong");
        title.textContent = first.title || first.message || first.code || "Observation";
        const description = document.createElement("span");
        description.textContent = first.description || first.message || "";
        const evidence = document.createElement("small");
        evidence.textContent = first.evidence || "";
        primary.append(title, description, evidence);
      }
    }

    if (secondary) {
      secondary.textContent = "";
      const secondaryInsights = (Array.isArray(pc.insights) ? pc.insights : []).slice(1, 4);
      secondary.hidden = legacyInsightsLayout || secondaryInsights.length === 0;
      secondaryInsights.forEach(function (insight) {
        const item = document.createElement("div");
        item.className = "overlay-pause-coach-item";
        item.dataset.severity = String(insight.severity || "info").toLowerCase();
        const title = document.createElement("strong");
        title.textContent = insight.title || insight.code || "Observation";
        const evidence = document.createElement("span");
        evidence.textContent = insight.evidence || insight.message || "";
        item.append(title, evidence);
        secondary.appendChild(item);
      });
    }

    const insightsEl = byId("overlay-pause-insights");
    if (insightsEl) {
      const insights = Array.isArray(pc.insights) ? pc.insights : [];
      insightsEl.textContent = "";
      // Standalone presets already place insight #1 in Primary and the rest
      // in Secondary. The generic list is intentionally retained only by the
      // legacy Companella Replay layout.
      const legacyList = legacyInsightsLayout;
      insightsEl.hidden = !legacyList || insights.length === 0;
      if (legacyList) insights.forEach(function (insight) {
          const line = document.createElement("div");
          line.className = "overlay-replay-insight";
          line.dataset.severity = String(insight.severity || "info").toLowerCase();
          line.textContent = insight.message || insight.description || String(insight.code || "");
          line.title = [insight.evidence, insight.dataQuality, insight.confidenceLabel].filter(Boolean).join(" · ");
          insightsEl.appendChild(line);
        });
    }

    const diagnostics = byId("overlay-pause-coach-diagnostics");
    if (diagnostics) {
      diagnostics.textContent = "";
      const technicalState = ["unavailable", "insufficientdata", "error"].includes(state);
      const items = technicalState && Array.isArray(pc.diagnostics) ? pc.diagnostics.filter(Boolean).slice(0, 3) : [];
      diagnostics.hidden = technicalState && items.length === 0;
      if (!technicalState) {
        diagnostics.dataset.kind = "provenance";
        diagnostics.textContent = "Data quality: " + String(pc.dataQuality || recent.accuracyProvenance || "Observed");
      }
      items.forEach(function (diagnostic) {
        const line = document.createElement("span");
        line.textContent = String(diagnostic).replace(/^pausecoach\.[^:]+:\s*/i, "");
        diagnostics.appendChild(line);
      });
    }
  }

  var lastPauseCoachRenderTrace = "";
  function tracePauseCoachRender(snapshot) {
    var coach = snapshot && snapshot.pauseCoach;
    if (!coach) return;
    var gameplay = snapshot.gameplay || {};
    var replay = snapshot.replay || {};
    var overall = coach.overall || {};
    var judgements = overall.judgements || {};
    var hitCount = [judgements.count300, judgements.count200, judgements.count100, judgements.count50,
      judgements.countGeki, judgements.countKatu, judgements.countMiss]
      .filter(function (value) { return value != null; })
      .reduce(function (sum, value) { return sum + Number(value || 0); }, 0);
    var trace = {
      source: snapshot.nativePauseCoach === true
        || snapshot.extensions && snapshot.extensions.nativePauseCoach === true
        ? "native-render"
        : "adapter-render",
      normalizedIsPlaying: gameplay.isPlaying == null ? null : gameplay.isPlaying,
      normalizedIsPaused: gameplay.isPaused == null ? null : gameplay.isPaused,
      runtimeState: gameplay.state || "",
      beatmapId: snapshot.beatmap && snapshot.beatmap.id || "",
      mapTimeMs: coach.mapProgressMs == null ? replay.mapProgressMs : coach.mapProgressMs,
      score: coach.score == null ? replay.score : coach.score,
      accuracy: coach.accuracy == null ? replay.accuracy : coach.accuracy,
      judgementCount: hitCount,
      hitErrorArrayLength: Array.isArray(replay.recentOffsets) ? replay.recentOffsets.length : 0,
      sessionId: coach.sessionId || "",
      coachState: coach.state || "",
    };
    var signature = JSON.stringify(trace);
    if (signature === lastPauseCoachRenderTrace) return;
    lastPauseCoachRenderTrace = signature;
    try {
      var encoded = encodeURIComponent(JSON.stringify(trace));
      if (typeof window.__overlayHostSend === "function") window.__overlayHostSend("overlay:pause-coach-render-debug:" + encoded);
    } catch (exception) {
      // Diagnostics must never interfere with rendering.
    }
  }

  function renderMainCard(snapshot) {
    var difficulty = snapshot.difficulty || {};
    var starText = difficulty.starLabel || formatNumber(difficulty.starRating, 2) || "—";
    var unit = difficulty.unit || "SR";
    var starValue = starText === "—" ? starText : (starText.trim().toLowerCase().endsWith(unit.toLowerCase()) ? starText : starText + " " + unit);
    text("rework-star", starValue, "—");
    var lnValue = difficulty.lnPercent == null ? Number.NaN : Number(difficulty.lnPercent);
    var lnLabel = Number.isFinite(lnValue) ? formatNumber(lnValue, 1) + "%" : "—";
    var keyCount = difficulty.keys == null ? Number.NaN : Number(difficulty.keys);
    var keys = Number.isFinite(keyCount) ? String(difficulty.keys) : "—";
    text("rework-meta", "LN%: " + lnLabel + " · Keys: " + keys, "LN — · Keys —");
    var rc = rank(snapshot, "rc-dan") || {};
    var ln = rank(snapshot, "ln-dan") || {};
    var hasLnPercent = Number.isFinite(lnValue) && lnValue > 0;
    var rcHas = rankHasValue(rc);
    var lnHas = hasLnPercent && rankHasValue(ln);
    if (rcHas || lnHas) {
      var rcText = rc.value || "—";
      var lnText = ln.value || "—";
      text("rework-diff", lnHas ? rcText + " || " + lnText : rcText, "—");
    }
    var card = document.querySelector(".main-card");
    if (card) {
      card.classList.remove("card-hidden-by-play");
      if (card.getAttribute("aria-hidden") === "true") card.removeAttribute("aria-hidden");
    }
  }

  function renderSignature(snapshot) {
    const beatmap = snapshot.beatmap || {};
    const difficulty = snapshot.difficulty || {};
    const ranks = (Array.isArray(snapshot.ranks) ? snapshot.ranks : []).map(function (entry) {
      return [entry.systemId, entry.value, entry.numericValue];
    });
    const skills = (Array.isArray(snapshot.skills) ? snapshot.skills : []).slice(0, 8).map(function (skill) {
      return [skill.label, skill.value, skill.valueLabel, skill.normalizedValue, skill.detail];
    });
    const replay = snapshot.replay || {};
    const coach = snapshot.pauseCoach || {};
    const timing = coach.timing || {};
    const performance = coach.performance || {};
    const overall = coach.overall || {};
    const recent = coach.recent || {};
    const section = coach.section || {};
    const insights = (Array.isArray(coach.insights) ? coach.insights : []).slice(0, 4).map(function (insight) {
      return [insight.code, insight.title, insight.description, insight.message, insight.evidence, insight.severity];
    });
    const replayColumns = (Array.isArray(replay.columns) ? replay.columns : []).map(function (column) {
      return [column.column, column.biasMs, column.ur];
    });
    const replayInsights = (Array.isArray(replay.insights) ? replay.insights : []).map(function (insight) {
      return [insight.code, insight.message];
    });
    const gameplay = snapshot.gameplay || {};
    return JSON.stringify({
      beatmap: [beatmap.id, beatmap.setId, beatmap.artist, beatmap.title, beatmap.version,
        beatmap.mapper, beatmap.bpmLabel, beatmap.backgroundUrl],
      gameplay: [gameplay.state, gameplay.isPlaying, gameplay.isPaused, gameplay.isFocused],
      difficulty: [difficulty.starRating, difficulty.starLabel, difficulty.unit, difficulty.lnPercent, difficulty.keys],
      ranks,
      skills,
      replay: [replay.hasData, replay.ur, replay.score, replay.mapProgressMs, replay.accuracy,
        replay.meanMs, replay.medianMs, replay.sampleCount, replay.earlyCount, replay.lateCount,
        replay.fidelity, replay.reason, replayColumns, replayInsights],
      pauseCoach: [coach.state, coach.hasData, coach.reason, coach.fidelity, coach.sessionId,
        coach.mapProgressMs, coach.score, coach.accuracy, coach.combo,
        timing.meanMs, timing.driftMs, timing.unstableRate, timing.sampleCount, timing.timingMargin,
        performance.recentHits, performance.recentMisses, performance.recentAccuracy,
        performance.wholeHits, performance.wholeMisses, overall.accuracy, overall.score, overall.combo,
        recent.accuracy, section.label, section.dominantPatternKind, insights],
    });
  }

  // Keep the card stable without making realtime state feel frozen. The
  // adapter/native collector continue to process every frame; only DOM
  // presentation is coalesced to the newest value every 300 ms.
  const PRESENTATION_UPDATE_INTERVAL_MS = 300;
  // A fresh WebView receives cached headless analysis, browser telemetry and
  // the replayable native snapshot in quick succession. Hold only the first
  // DOM frame for the same short interval so those sources settle into one
  // merged snapshot before the widget becomes visible; subsequent updates
  // use the normal latest-wins throttle above.
  const INITIAL_RENDER_SETTLE_MS = 300;
  const rendererStartedAt = Date.now();
  var lastRenderSignature = "";
  var lastRenderAt = 0;
  var pendingRenderSnapshot = null;
  var pendingRenderTimer = 0;

  function renderNow(snapshot, force) {
    const effectiveSnapshot = snapshot;
    const signature = renderSignature(effectiveSnapshot);
    if (!force && signature === lastRenderSignature) return;
    lastRenderSignature = signature;
    lastRenderAt = Date.now();
    tracePauseCoachRender(effectiveSnapshot);
    renderSummary(effectiveSnapshot);
    renderSkills(effectiveSnapshot);
    renderReplay(effectiveSnapshot);
    renderPauseCoach(effectiveSnapshot);
    renderMainCard(effectiveSnapshot);
    if (typeof window.__overlayHostQueueSizeReport === "function") {
      window.__overlayHostQueueSizeReport();
    }
  }

  function flushPendingRender() {
    pendingRenderTimer = 0;
    const snapshot = pendingRenderSnapshot;
    pendingRenderSnapshot = null;
    if (snapshot) renderNow(snapshot, false);
  }

  function render(snapshot, force) {
    const effectiveSnapshot = mergeSnapshot(snapshot);
    window.__overlayLatestAnalysisSnapshot = effectiveSnapshot;
    if (force) {
      pendingRenderSnapshot = null;
      if (pendingRenderTimer) {
        window.clearTimeout(pendingRenderTimer);
        pendingRenderTimer = 0;
      }
      renderNow(effectiveSnapshot, true);
      return;
    }

    const elapsed = Date.now() - lastRenderAt;
    const startupElapsed = Date.now() - rendererStartedAt;
    if (lastRenderAt === 0 && startupElapsed < INITIAL_RENDER_SETTLE_MS) {
      pendingRenderSnapshot = effectiveSnapshot;
      if (!pendingRenderTimer) {
        pendingRenderTimer = window.setTimeout(
          flushPendingRender,
          Math.max(0, INITIAL_RENDER_SETTLE_MS - startupElapsed));
      }
      return;
    }
    if (lastRenderAt === 0 || elapsed >= PRESENTATION_UPDATE_INTERVAL_MS) {
      pendingRenderSnapshot = null;
      if (pendingRenderTimer) {
        window.clearTimeout(pendingRenderTimer);
        pendingRenderTimer = 0;
      }
      renderNow(effectiveSnapshot, false);
      return;
    }

    // Keep collecting/merging every frame, but delay the visible DOM update
    // until the 300 ms presentation gap expires. This is latest-wins, so
    // intermediate score/UR/accuracy values cannot make the card flicker.
    pendingRenderSnapshot = effectiveSnapshot;
    if (!pendingRenderTimer) {
      pendingRenderTimer = window.setTimeout(
        flushPendingRender,
        Math.max(0, PRESENTATION_UPDATE_INTERVAL_MS - elapsed));
    }
  }

  window.__overlayRenderAnalysisSnapshot = render;

  window.addEventListener("analysis:snapshot", function (event) {
    if (event && event.detail) render(event.detail);
  });

  if (window.__overlayLatestAnalysisSnapshot) render(window.__overlayLatestAnalysisSnapshot);
})();
