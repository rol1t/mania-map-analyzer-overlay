(function () {
  "use strict";

  const OVERLAY_VIEW_STATE_SCHEMA_VERSION = 1;

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

  function positiveBeatmapKey(snapshot) {
    const beatmap = snapshot && snapshot.beatmap || {};
    const id = String(beatmap.id || "").trim().toLowerCase();
    if (id) return `id:${id}`;
    const setId = String(beatmap.setId || "").trim().toLowerCase();
    const version = String(beatmap.version || "").trim().toLowerCase();
    return setId ? `set:${setId}|${version}` : "";
  }

  function rankHasValue(entry) {
    const value = String(entry && entry.value || "").trim();
    return value !== "" && value !== "—" && value !== "-";
  }

  function hasText(value) {
    return value != null && String(value).trim() !== "";
  }

  // Native realtime frames intentionally contain only the beatmap identity.
  // The browser adapter normally supplies the full background URL, but a
  // navigation/minimize race can make that frame arrive later (or be absent
  // altogether). Tosu exposes a stable background endpoint keyed by map id,
  // so the renderer can keep the image available without waiting for a
  // second producer.
  function backgroundUrlFor(beatmap) {
    if (!beatmap) return "";
    if (hasText(beatmap.backgroundUrl)) {
      const supplied = String(beatmap.backgroundUrl).trim();
      // Headless metadata may contain a local filename/path rather than a
      // browser URL. Do not turn that into a broken relative CSS URL; fall
      // back to Tosu's HTTP endpoint below.
      if (/^(?:https?:|data:|blob:|\/)/i.test(supplied)) return supplied;
    }
    const id = String(beatmap.id || "").trim();
    const host = typeof location !== "undefined" && location && location.host
      ? String(location.host)
      : "";
    return id && host
      ? `http://${host}/files/beatmap/background?ts=${encodeURIComponent(id)}`
      : "";
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
    // Realtime/browser frames do not carry headless analysis series. Keep the
    // latest analyzer-owned timeline for the same map instead of making the
    // graph disappear while the gameplay snapshot is refreshed.
    if (!hasDifficultyTimeline(current.timeline) && hasDifficultyTimeline(previous.timeline)) {
      merged.timeline = previous.timeline;
    }
    return merged;
  }

  function hasDifficultyTimeline(timeline) {
    if (!timeline || typeof timeline !== "object") return false;
    if (Array.isArray(timeline.points)) return timeline.points.length >= 2;
    return Array.isArray(timeline.times)
      && Array.isArray(timeline.values)
      && Math.min(timeline.times.length, timeline.values.length) >= 2;
  }

  function beatmapStatus(beatmap) {
    const source = beatmap || {};
    const hasMetadata = [source.artist, source.title, source.version, source.mapper].some(hasText);
    if (hasMetadata) {
      const artist = hasText(source.artist) ? String(source.artist).trim() : "Unknown Artist";
      const title = hasText(source.title) ? String(source.title).trim() : "Unknown Title";
      const version = hasText(source.version) ? String(source.version).trim() : "Unknown Difficulty";
      const mapper = hasText(source.mapper) ? String(source.mapper).trim() : "Unknown Mapper";
      return `${artist} - ${title} [${version}] // ${mapper}`;
    }

    const id = String(source.id || "").trim();
    return id ? `Beatmap ${id}` : "Waiting for beatmap data...";
  }

  function renderBeatmapStatus(beatmap) {
    const element = byId("status");
    if (!element) return;

    const next = beatmapStatus(beatmap);
    if (element.textContent !== next) element.textContent = next;

    // The upstream analyser owns this node in the source document and marks
    // it as `error` when a transient/non-mania carousel file is parsed. Once
    // an application view-state is being rendered, presentation is native-
    // authoritative: remove that legacy state together with its marquee
    // styles so an old error cannot remain attached to a newer map id.
    element.className = "status ok";
    if (element.style && typeof element.style.removeProperty === "function") {
      element.style.removeProperty("--status-marquee-distance");
      element.style.removeProperty("--status-marquee-duration");
    }
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

  function selectReplay(exactReplay, realtimeReplay) {
    const exact = exactReplay || null;
    const realtime = realtimeReplay || null;
    if (!exact) return realtime || {};
    if (!realtime) return exact;
    const exactIsAuthoritative = isExactReplay(exact);
    // OverlayViewState carries exact .osr analysis and provisional native
    // telemetry in separate slots. Prefer the exact block as a whole when it
    // exists; choosing realtimeReplay first silently erased stable columns and
    // sections every time the native frame arrived.
    return exactIsAuthoritative ? exact : realtime;
  }

  function isExactReplay(replay) {
    return !!(replay
      && replay.hasData === true
      && replay.isProvisional !== true
      && String(replay.fidelity || "").toLowerCase() !== "provisional");
  }

  function mergeNativePresentationFields(nativeSnapshot, previousSnapshot) {
    if (!nativeSnapshot || !previousSnapshot) return nativeSnapshot;

    const nativeKey = beatmapKey(nativeSnapshot);
    const previousKey = beatmapKey(previousSnapshot);
    // Presentation metadata is safe to carry only across the same positive
    // beatmap identity. A native frame for another map must start a clean
    // presentation even when the old browser document is still alive.
    if (!nativeKey || !previousKey || nativeKey !== previousKey) return nativeSnapshot;

    const merged = Object.assign({}, nativeSnapshot);
    merged.beatmap = mergeBeatmap(previousSnapshot.beatmap, nativeSnapshot.beatmap);
    merged.difficulty = mergeDifficulty(previousSnapshot.difficulty, nativeSnapshot.difficulty);

    // Native realtime frames deliberately do not contain headless skills or
    // ranks until analysis is complete. Do not erase already-rendered values
    // for the same map while those frames continue arriving every poll.
    if ((!Array.isArray(nativeSnapshot.skills) || nativeSnapshot.skills.length === 0)
        && Array.isArray(previousSnapshot.skills) && previousSnapshot.skills.length > 0) {
      merged.skills = previousSnapshot.skills;
    }
    if ((!Array.isArray(nativeSnapshot.ranks) || nativeSnapshot.ranks.length === 0)
        && Array.isArray(previousSnapshot.ranks) && previousSnapshot.ranks.length > 0) {
      merged.ranks = previousSnapshot.ranks;
    }

    // Exact replay analysis is a separate authoritative slot. A provisional
    // native frame must not remove exact per-column/LN data already shown for
    // this map (this also covers WebView recreation before the cache replay).
    if (!isExactReplay(nativeSnapshot.replay) && isExactReplay(previousSnapshot.replay)) {
      merged.replay = previousSnapshot.replay;
    }

    return merged;
  }

  // The application now has a versioned view-state contract. Keep the
  // conversion to the renderer's existing flat block shape deliberately
  // mechanical while the remaining browser producers are migrated; no Tosu
  // parsing or producer/session decisions belong here.
  function viewStateToSnapshot(viewState) {
    const realtime = viewState && viewState.realtime || {};
    const producer = String(viewState && viewState.producer || "application").toLowerCase();
    const isNative = producer === "native" && !!(viewState && viewState.realtime);
    const gameplay = Object.assign({}, viewState && viewState.gameplay || {});
    const previousSnapshot = window.__overlayLatestAnalysisSnapshot;
    const rawBeatmap = viewState && viewState.beatmap || {};
    const currentBeatmap = Object.assign(
      { id: viewState && viewState.beatmapId || "" },
      rawBeatmap);
    const previousBeatmap = previousSnapshot && previousSnapshot.beatmap || {};
    const currentBeatmapKey = beatmapKey({ beatmap: currentBeatmap });
    const previousBeatmapKey = beatmapKey({ beatmap: previousBeatmap });
    // Native realtime frames intentionally carry only the map identity. Keep
    // presentation metadata learned by the browser adapter for that same map
    // (especially the background URL), but never carry it across a map
    // transition.
    const beatmap = currentBeatmapKey && currentBeatmapKey === previousBeatmapKey
      ? mergeBeatmap(previousBeatmap, currentBeatmap)
      : currentBeatmap;
    // System.Text.Json serializes the domain enum numerically in the
    // transport view-state; the composed gameplay block already contains
    // the renderer-facing string state. Only accept a string override from a
    // future transport that explicitly opts into string enum serialization.
    if (typeof realtime.state === "string" && realtime.state) gameplay.state = realtime.state;
    if (typeof realtime.state === "string" && (realtime.state === "Playing" || realtime.state === "Paused")) gameplay.isPlaying = true;
    if (realtime.state === "Playing") gameplay.isPaused = false;
    if (realtime.state === "Paused") gameplay.isPaused = true;
    return {
      schemaVersion: 1,
      sourceId: "mania-map-analyser",
      beatmap,
      gameplay,
      difficulty: viewState && viewState.difficulty || {},
      ranks: Array.isArray(viewState && viewState.ranks) ? viewState.ranks : [],
      skills: Array.isArray(viewState && viewState.skills) ? viewState.skills : [],
      replay: selectReplay(viewState && viewState.replay, viewState && viewState.realtimeReplay),
      pauseCoach: viewState && viewState.pauseCoach || {},
      extensions: {
        nativePauseCoach: isNative,
        realtimeProducer: producer,
        viewStateVersion: viewState && viewState.version,
        beatmapGeneration: viewState && viewState.beatmapGeneration,
      },
    };
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
      const nativePositiveKey = positiveBeatmapKey(nativeSnapshot);
      const currentPositiveKey = positiveBeatmapKey(snapshot);
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
      const positiveDifferentBeatmap = nativePositiveKey
        && currentPositiveKey
        && nativePositiveKey !== currentPositiveKey;
      // The native stream is the ordering source for the current Tosu map.
      // Browser snapshots are produced by an independently scheduled
      // websocket/DOM pipeline and can legitimately arrive after a map
      // transition with the previous map still in their payload. Treating
      // that late browser identity as proof of a new attempt rolls the card
      // back to the old map (the visible "stuck" card failure). A browser
      // producer may enrich the native map once it catches up, but only a
      // native frame is allowed to release native authority.
      const nativeProducerChangedMap = currentProducer === "native" && positiveDifferentBeatmap;
      const positiveDifferentAttempt = nativeProducerChangedMap
        || (sessionsAreComparable && nativeSession && currentSession && nativeSession !== currentSession);
      // The native collector is authoritative for realtime coaching for the
      // current attempt. Browser frames are still useful for map metadata,
      // but must not replace native pause/gameplay/replay values for the same
      // map: the two sources use different rolling windows and alternating
      // them makes the visible card jump backwards and forwards. A positive
      // Native map/session transitions release native authority; browser
      // frames never do so while native realtime is active.
      const currentMissingBeatmapIdentity = !currentPositiveKey;
      if (positiveDifferentAttempt) {
        // A positive map/session transition releases the previous native
        // authority. The next native marker (if any) will establish it again.
        window.__overlayNativePauseCoachSnapshot = null;
      } else if (currentProducer === "browser" && positiveDifferentBeatmap) {
        // Do not let a stale browser frame replace the native map identity or
        // its realtime blocks. The next native frame establishes the new map;
        // a browser frame for that same identity can then merge metadata.
        snapshot = nativeSnapshot;
        currentKey = beatmapKey(snapshot);
      } else if (currentMissingBeatmapIdentity && currentProducer === "browser") {
        // An identity-less browser frame is not evidence that its DOM fields
        // belong to the current native map. In production this is exactly how
        // an upstream `Beatmap mode is not mania` error became the visible
        // title of an unrelated Tosu map. Keep the complete native snapshot;
        // browser enrichment is accepted only after it carries the same
        // positive beatmap identity.
        snapshot = nativeSnapshot;
        currentKey = beatmapKey(snapshot);
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
      var hasExplicitLnPercent = currentLnPercent !== null
        && currentLnPercent !== undefined
        && Number.isFinite(Number(currentLnPercent));
      // Realtime/native frames intentionally omit headless difficulty data.
      // Missing LN% is not evidence that a map has no long notes; only an
      // explicit zero may suppress a previously resolved LN DAN rank.
      if (hasExplicitLnPercent && Number(currentLnPercent) <= 0) {
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

    const backgroundUrl = backgroundUrlFor(beatmap);
    if (backgroundUrl) {
      const safeUrl = backgroundUrl.replace(/"/g, "\\\"");
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

      chart.appendChild(column);
    });
  }

  function readDifficultyTimeline(snapshot) {
    const timeline = snapshot && snapshot.difficulty && snapshot.difficulty.timeline;
    if (!timeline || typeof timeline !== "object") return [];

    const points = [];
    if (Array.isArray(timeline.points)) {
      timeline.points.forEach(function (point) {
        const timeMs = Number(point && (point.timeMs ?? point.time));
        const value = Number(point && point.value);
        if (Number.isFinite(timeMs) && Number.isFinite(value) && timeMs >= 0) {
          points.push({ timeMs: timeMs, value: value });
        }
      });
    } else if (Array.isArray(timeline.times) && Array.isArray(timeline.values)) {
      const count = Math.min(timeline.times.length, timeline.values.length);
      for (let index = 0; index < count; index++) {
        const timeMs = Number(timeline.times[index]);
        const value = Number(timeline.values[index]);
        if (Number.isFinite(timeMs) && Number.isFinite(value) && timeMs >= 0) {
          points.push({ timeMs: timeMs, value: value });
        }
      }
    }

    points.sort(function (left, right) { return left.timeMs - right.timeMs; });
    const ordered = [];
    points.forEach(function (point) {
      if (!ordered.length || point.timeMs > ordered[ordered.length - 1].timeMs) {
        ordered.push(point);
      }
    });
    return ordered.length >= 2 ? ordered : [];
  }

  function timelineSeriesSignature(points) {
    if (!points.length) return "";
    let checksum = 0;
    const stride = Math.max(1, Math.floor(points.length / 32));
    points.forEach(function (point, index) {
      if (index % stride === 0 || index === points.length - 1) {
        checksum += (point.timeMs * 0.000001 + point.value) * (index + 1);
      }
    });
    const first = points[0];
    const last = points[points.length - 1];
    return [points.length, first.timeMs, first.value, last.timeMs, last.value, checksum].join(":");
  }

  function timelineCursorMs(snapshot) {
    const coachTime = Number(snapshot && snapshot.pauseCoach && snapshot.pauseCoach.mapProgressMs);
    if (Number.isFinite(coachTime) && coachTime >= 0) return coachTime;
    const replayTime = Number(snapshot && snapshot.replay && snapshot.replay.mapProgressMs);
    return Number.isFinite(replayTime) && replayTime >= 0 ? replayTime : null;
  }

  function interpolateTimelineValue(points, timeMs) {
    if (!points.length || !Number.isFinite(timeMs)) return null;
    if (timeMs <= points[0].timeMs) return points[0].value;
    const last = points[points.length - 1];
    if (timeMs >= last.timeMs) return last.value;
    for (let index = 1; index < points.length; index++) {
      const right = points[index];
      if (timeMs <= right.timeMs) {
        const left = points[index - 1];
        const span = right.timeMs - left.timeMs;
        const ratio = span > 0 ? (timeMs - left.timeMs) / span : 0;
        return left.value + (right.value - left.value) * ratio;
      }
    }
    return last.value;
  }

  function formatTimelineTime(timeMs) {
    const seconds = Math.max(0, Math.round(Number(timeMs) / 1000));
    const minutes = Math.floor(seconds / 60);
    return `${minutes}:${String(seconds % 60).padStart(2, "0")}`;
  }

  function renderDifficultyTimeline(snapshot) {
    const container = byId("overlay-difficulty-timeline");
    if (!container) return;

    const points = readDifficultyTimeline(snapshot);
    if (points.length < 2) {
      container.hidden = true;
      return;
    }

    container.hidden = false;
    const startTime = points[0].timeMs;
    const endTime = points[points.length - 1].timeMs;
    let minValue = points[0].value;
    let maxValue = points[0].value;
    points.forEach(function (point) {
      minValue = Math.min(minValue, point.value);
      maxValue = Math.max(maxValue, point.value);
    });
    const valueSpan = Math.max(0.000001, maxValue - minValue);
    const timeSpan = Math.max(1, endTime - startTime);
    const width = 1000;
    const height = 180;
    const paddingTop = 8;
    const paddingBottom = 8;
    const plotHeight = height - paddingTop - paddingBottom;
    const x = function (timeMs) { return ((timeMs - startTime) / timeSpan) * width; };
    const y = function (value) { return paddingTop + (1 - (value - minValue) / valueSpan) * plotHeight; };
    const linePath = points.map(function (point, index) {
      return `${index === 0 ? "M" : "L"} ${x(point.timeMs).toFixed(2)} ${y(point.value).toFixed(2)}`;
    }).join(" ");
    const areaPath = `${linePath} L ${width.toFixed(2)} ${height.toFixed(2)} L 0 ${height.toFixed(2)} Z`;
    const area = byId("overlay-difficulty-timeline-area");
    const line = byId("overlay-difficulty-timeline-line");
    const cursor = byId("overlay-difficulty-timeline-cursor");
    const seriesSignature = timelineSeriesSignature(points);
    if (container.__overlayTimelineSeriesSignature !== seriesSignature) {
      container.__overlayTimelineSeriesSignature = seriesSignature;
      if (area && typeof area.setAttribute === "function") area.setAttribute("d", areaPath);
      if (line && typeof line.setAttribute === "function") line.setAttribute("d", linePath);
      text("overlay-difficulty-timeline-start", formatTimelineTime(startTime), "0:00");
      text("overlay-difficulty-timeline-end", formatTimelineTime(endTime), "—");
    }

    const cursorTime = timelineCursorMs(snapshot);
    if (cursor && typeof cursor.setAttribute === "function" && Number.isFinite(cursorTime)) {
      const boundedTime = Math.max(startTime, Math.min(endTime, cursorTime));
      const cursorX = x(boundedTime).toFixed(2);
      cursor.setAttribute("x1", cursorX);
      cursor.setAttribute("x2", cursorX);
      cursor.setAttribute("y1", "0");
      cursor.setAttribute("y2", String(height));
      cursor.hidden = false;
      const currentValue = interpolateTimelineValue(points, boundedTime);
      text("overlay-difficulty-timeline-current",
        `${formatTimelineTime(boundedTime)} · ${formatNumber(currentValue, 2)}`,
        "—");
    } else {
      if (cursor) cursor.hidden = true;
      text("overlay-difficulty-timeline-current", "—", "—");
    }
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
    } else {
      text("rework-diff", "—", "—");
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
        beatmap.mapper, beatmap.bpmLabel, backgroundUrlFor(beatmap)],
      gameplay: [gameplay.state, gameplay.isPlaying, gameplay.isPaused, gameplay.isFocused],
      difficulty: [difficulty.starRating, difficulty.starLabel, difficulty.unit, difficulty.lnPercent, difficulty.keys,
        timelineSeriesSignature(readDifficultyTimeline(snapshot)), timelineCursorMs(snapshot)],
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

  var lastRenderSignature = "";

  function renderNow(snapshot, force) {
    const effectiveSnapshot = snapshot;
    // The legacy analyser can mutate #status after the application snapshot
    // was accepted. Reassert this single application-owned field even when
    // the data signature is unchanged; otherwise an asynchronous source
    // error can remain visible until map time or another metric changes.
    renderBeatmapStatus(effectiveSnapshot && effectiveSnapshot.beatmap || {});
    const signature = renderSignature(effectiveSnapshot);
    if (!force && signature === lastRenderSignature) return;
    lastRenderSignature = signature;
    tracePauseCoachRender(effectiveSnapshot);
    renderSummary(effectiveSnapshot);
    renderSkills(effectiveSnapshot);
    renderDifficultyTimeline(effectiveSnapshot);
    renderReplay(effectiveSnapshot);
    renderPauseCoach(effectiveSnapshot);
    renderMainCard(effectiveSnapshot);
    if (typeof window.__overlayHostQueueSizeReport === "function") {
      window.__overlayHostQueueSizeReport();
    }
  }

  function scheduleSnapshot(effectiveSnapshot, force) {
    window.__overlayLatestAnalysisSnapshot = effectiveSnapshot;
    if (force) {
      renderNow(effectiveSnapshot, true);
      return;
    }

    // The application reducer and producer arbitration have already accepted
    // this snapshot. Render it immediately; a wall-clock delay here made
    // modifier changes and realtime values appear late or out of order.
    renderNow(effectiveSnapshot, false);
  }

  function render(snapshot, force) {
    scheduleSnapshot(mergeSnapshot(snapshot), force);
  }

  window.__overlayRenderAnalysisSnapshot = render;

  function renderViewState(viewState, force) {
    if (!viewState) return;
    const schemaVersion = Number(viewState.schemaVersion);
    // Older documents did not carry a schema field. Keep accepting those
    // compatibility payloads, but never let a future contract be interpreted
    // as the current shape or replace the last valid render.
    if (Number.isFinite(schemaVersion) && schemaVersion !== OVERLAY_VIEW_STATE_SCHEMA_VERSION) {
      const message = `Unsupported overlay view-state schema version: ${schemaVersion}`;
      window.__overlayViewStateProtocolError = message;
      try {
        window.dispatchEvent(new CustomEvent("overlay:runtime-error", {
          detail: { operation: "Overlay view-state protocol", message: message },
        }));
      } catch (exception) {
        console.error(message, exception);
      }
      return;
    }
    const epoch = String(viewState.presentationEpoch || "");
    const previousEpoch = String(window.__overlayPresentationEpoch || "");
    if (epoch && epoch !== previousEpoch) {
      // A fullscreen document may outlive the native process. Versions restart
      // per process, so discard old renderer/native authority before applying
      // a new epoch.
      window.__overlayLatestViewStateVersion = undefined;
      window.__overlayLatestViewState = undefined;
      window.__overlayNativePauseCoachSnapshot = null;
    }
    if (epoch) window.__overlayPresentationEpoch = epoch;
    const version = Number(viewState.version);
    const latestVersion = Number(window.__overlayLatestViewStateVersion);
    if (!force && Number.isFinite(version) && Number.isFinite(latestVersion) && version < latestVersion) {
      return;
    }
    if (Number.isFinite(version)) window.__overlayLatestViewStateVersion = version;
    window.__overlayLatestViewState = viewState;
    const presentation = viewState.presentation || {};
    if (document.documentElement && document.documentElement.classList) {
      document.documentElement.classList.toggle(
        "launcher-osu-minimized",
        presentation.osuWindowMinimized === true);
    }
    const snapshot = viewStateToSnapshot(viewState);
    if (String(viewState.producer || "").toLowerCase() === "native" && viewState.realtime) {
      const previousSnapshot = window.__overlayLatestAnalysisSnapshot;
      const reconciledSnapshot = mergeNativePresentationFields(snapshot, previousSnapshot);
      // The application composer has already reconciled beatmap, difficulty,
      // replay, and Pause Coach slots. Reconcile only incomplete presentation
      // fields from the same-map previous frame; native gameplay/Pause Coach
      // values remain authoritative and are never taken from the browser.
      window.__overlayNativePauseCoachSnapshot = reconciledSnapshot;
      scheduleSnapshot(reconciledSnapshot, force);
      return;
    }
    render(snapshot, force);
  }

  window.__overlayRenderViewState = renderViewState;

  window.addEventListener("analysis:snapshot", function (event) {
    if (event && event.detail) render(event.detail);
  });

  window.addEventListener("overlay:view-state", function (event) {
    if (event && event.detail) renderViewState(event.detail);
  });

  if (window.__overlayLatestAnalysisSnapshot) render(window.__overlayLatestAnalysisSnapshot);
})();
