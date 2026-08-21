(function () {
  "use strict";

  if (window.__overlaySnapshotRendererBound) {
    if (window.__overlayLatestAnalysisSnapshot && typeof window.__overlayRenderAnalysisSnapshot === "function") {
      window.__overlayRenderAnalysisSnapshot(window.__overlayLatestAnalysisSnapshot);
    }
    return;
  }
  window.__overlaySnapshotRendererBound = true;

  function byId(id) {
    return document.getElementById(id);
  }

  function text(id, value, fallback) {
    const element = byId(id);
    if (element) element.textContent = String(value == null || value === "" ? fallback : value);
  }

  function formatNumber(value, maximumFractionDigits) {
    const number = Number(value);
    if (!Number.isFinite(number)) return "";
    return number.toFixed(maximumFractionDigits).replace(/\.0+$|(?<=\.\d)0+$/g, "");
  }

  function rank(snapshot, systemId) {
    return (snapshot.ranks || []).find(function (entry) {
      return String(entry.systemId || "").toLowerCase() === systemId;
    });
  }

  function beatmapKey(snapshot) {
    const beatmap = snapshot && snapshot.beatmap || {};
    const values = [beatmap.id, beatmap.setId, beatmap.artist, beatmap.title, beatmap.version]
      .map(function (value) { return String(value || "").trim().toLowerCase(); })
    return values.some(Boolean) ? values.join("|") : "";
  }

  function rankHasValue(entry) {
    const value = String(entry && entry.value || "").trim();
    return value !== "" && value !== "—" && value !== "-";
  }

  function mergeSnapshot(snapshot) {
    const previous = window.__overlayLatestAnalysisSnapshot;
    const previousKey = beatmapKey(previous);
    const currentKey = beatmapKey(snapshot);
    if (!previous || !previousKey || !currentKey || previousKey !== currentKey) {
      return snapshot;
    }

    const previousRanks = Array.isArray(previous.ranks) ? previous.ranks : [];
    const currentRanks = Array.isArray(snapshot.ranks) ? snapshot.ranks : [];
    if (previousRanks.length === 0 && currentRanks.length === 0) {
      return snapshot;
    }

    const ranks = new Map();
    previousRanks.forEach(function (entry) {
      const id = String(entry && entry.systemId || "").toLowerCase();
      if (id) ranks.set(id, entry);
    });
    currentRanks.forEach(function (entry) {
      const id = String(entry && entry.systemId || "").toLowerCase();
      if (id && (!ranks.has(id) || rankHasValue(entry))) ranks.set(id, entry);
    });

    const merged = Object.assign({}, snapshot, { ranks: Array.from(ranks.values()) });
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
    text("overlay-replay-accuracy", r.accuracy == null ? "—" : fmt(r.accuracy, 2) + "%", "—");
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
    const hasRankData = [rc, ln].some(rankHasValue);
    if (hasRankData) {
      text("rework-diff", (rc.value || "—") + " || " + (ln.value || "—"), "—");
    }
    var card = document.querySelector(".main-card");
    if (card) {
      card.classList.remove("card-hidden-by-play");
      if (card.getAttribute("aria-hidden") === "true") card.removeAttribute("aria-hidden");
    }
  }

  function render(snapshot) {
    const effectiveSnapshot = mergeSnapshot(snapshot);
    window.__overlayLatestAnalysisSnapshot = effectiveSnapshot;
    renderSummary(effectiveSnapshot);
    renderSkills(effectiveSnapshot);
    renderReplay(effectiveSnapshot);
    renderMainCard(effectiveSnapshot);
  }

  window.__overlayRenderAnalysisSnapshot = render;

  window.addEventListener("analysis:snapshot", function (event) {
    if (event && event.detail) render(event.detail);
  });

  if (window.__overlayLatestAnalysisSnapshot) render(window.__overlayLatestAnalysisSnapshot);
})();
