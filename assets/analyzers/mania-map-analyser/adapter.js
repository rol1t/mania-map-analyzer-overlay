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
  let reconnectTimer = 0;
  let statePollTimer = 0;
  let statePollInFlight = false;
  let lastStatePollWarningAt = 0;
  let disposed = false;
  let lastSignature = "";
  let beatmap = emptyBeatmap();
  let gameplay = emptyGameplay();

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

  function clean(value) {
    return String(value == null ? "" : value).replace(/\s+/g, " ").trim();
  }

  function finiteNumber(value) {
    if (value == null || String(value).trim() === "") return null;
    const number = Number(String(value).replace(",", "."));
    return Number.isFinite(number) ? number : null;
  }

  function firstNumber(value) {
    const match = String(value || "").match(/[+-]?(?:\d+(?:[.,]\d+)?|[.,]\d+)/);
    return match ? finiteNumber(match[0]) : null;
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
      || root.classList.contains("overlay-layout-companella");
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

  function buildSnapshot() {
    return {
      schemaVersion: SCHEMA_VERSION,
      sourceId: SOURCE_ID,
      beatmap,
      gameplay,
      difficulty: readDifficulty(),
      ranks: splitRanks(text("rework-diff")),
      skills: readSkills(),
    };
  }

  function sendToHost(message) {
    try {
      if (typeof window.__overlayHostSend === "function") {
        window.__overlayHostSend(message);
      } else if (typeof invokeCSharpAction === "function") {
        invokeCSharpAction(message);
      } else if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(message);
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
    const snapshot = buildSnapshot();
    const json = JSON.stringify(snapshot);
    if (json === lastSignature) return;
    lastSignature = json;
    window.dispatchEvent(new CustomEvent("analysis:snapshot", { detail: snapshot }));
    sendToHost(`analysis:${SOURCE_ID}:${json}`);
  }

  function queuePublish() {
    if (animationFrame) return;
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
    const sourceBeatmap = payload && payload.beatmap;
    if (sourceBeatmap) {
      const metadata = sourceBeatmap.metadata || {};
      const stats = sourceBeatmap.stats || {};
      const id = String(sourceBeatmap.id || sourceBeatmap.beatmapId || "");
      const setId = String(sourceBeatmap.set || sourceBeatmap.setId || sourceBeatmap.beatmapSetId || "");
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
    const nextIsPaused = game && typeof game.paused === "boolean" ? game.paused : gameplay.isPaused;
    const isFocused = game && typeof game.focused === "boolean" ? game.focused : gameplay.isFocused;
    gameplay = {
      state: nextState,
      isPlaying: nextIsPlaying,
      isPaused: nextIsPaused,
      isFocused,
    };

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
          keys: ["artist", "title", "version", "mapper", "id", "set", "setId", "beatmapSetId", "metadata", "stats", "bpm"],
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
      if (reconnectTimer) clearTimeout(reconnectTimer);
      if (statePollTimer) window.clearInterval(statePollTimer);
      if (socket) {
        try { socket.close(); } catch (exception) { reportRuntimeError("Disposing analyzer websocket", exception); }
      }
      observer = null;
      socket = null;
      animationFrame = 0;
      reconnectTimer = 0;
      statePollTimer = 0;
      statePollInFlight = false;
    },
  };
})();
