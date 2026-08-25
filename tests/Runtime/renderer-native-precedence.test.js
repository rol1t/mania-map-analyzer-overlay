"use strict";

// Behavioural regression test for the native Pause Coach authority boundary.
// A partial browser snapshot must not replace a valid native paused snapshot.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "assets/overlay/runtime/renderer.js"), "utf8");
const listeners = new Map();
let renderCount = 0;
const styleProperties = {};
const documentClasses = new Set();
const statusStyleProperties = {};
const statusElement = {
  textContent: "[Error] Rework failed: Beatmap mode is not mania",
  className: "status error marquee",
  style: {
    removeProperty(name) { delete statusStyleProperties[name]; },
  },
};
const document = {
  documentElement: {
    classList: {
      contains: value => documentClasses.has(value),
      toggle(value, force) {
        if (force === true) documentClasses.add(value);
        else if (force === false) documentClasses.delete(value);
        else if (documentClasses.has(value)) documentClasses.delete(value);
        else documentClasses.add(value);
      },
    },
    style: {
      setProperty(name, value) { styleProperties[name] = value; },
      removeProperty(name) { delete styleProperties[name]; },
    },
  },
  getElementById: id => id === "status" ? statusElement : null,
  querySelector: () => null,
};
const window = {
  setTimeout,
  clearTimeout,
  __overlayHostQueueSizeReport: () => { renderCount++; },
  addEventListener(type, callback) {
    const callbacks = listeners.get(type) || [];
    callbacks.push(callback);
    listeners.set(type, callbacks);
  },
  dispatchEvent(event) {
    for (const callback of listeners.get(event.type) || []) callback(event);
  },
};
const context = {
  window,
  document,
  console,
  Number,
  String,
  Object,
  Array,
  JSON,
  Math,
  encodeURIComponent,
  location: { host: "127.0.0.1:24050" },
  getComputedStyle: () => ({ getPropertyValue: () => "" }),
};
context.CustomEvent = class CustomEventStub {
  constructor(type, init) {
    this.type = type;
    this.detail = init && init.detail;
  }
};
vm.createContext(context);
vm.runInContext(source, context, { filename: "renderer.js" });

function publish(snapshot) {
  window.dispatchEvent({ type: "analysis:snapshot", detail: snapshot });
}

function publishViewState(viewState) {
  window.dispatchEvent({ type: "overlay:view-state", detail: viewState });
}

publishViewState({
  version: 1,
  beatmapGeneration: 1,
  producer: "native",
  beatmap: { id: "674175" },
  gameplay: { state: "Paused", isPlaying: true, isPaused: true },
  replay: {
    hasData: true,
    fidelity: "exact",
    mapProgressMs: 30000,
    score: 123456,
    columns: [{ column: 1, biasMs: 8.5 }],
  },
  realtimeReplay: { mapProgressMs: 30000, score: 123456, isProvisional: true },
  // RealtimePlayState.Paused is serialized as enum value 3 by the current
  // System.Text.Json transport; gameplay.state remains the view-facing text.
  realtime: { state: 3, mapTimeMs: 30000, score: 123456 },
  pauseCoach: { state: "Paused", sessionId: "native-A", mapProgressMs: 30000, hasData: true },
  presentation: {
    overlayMode: true,
    visible: true,
    ready: true,
    osuWindowMinimized: true,
  },
});

assert.equal(window.__overlayLatestViewState.version, 1);
assert.equal(window.__overlayLatestAnalysisSnapshot.replay.columns[0].biasMs, 8.5);
assert.equal(documentClasses.has("launcher-osu-minimized"), true);

// Preview/browser presentation can resolve the map's difficulty and skill
// columns before the first native realtime frame. A later native frame is
// intentionally partial until headless analysis finishes and must not erase
// those already-known fields for the same map.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  beatmap: { id: "674175" },
  difficulty: { starRating: 4.89, starLabel: "4.89 SR", lnPercent: 12.4, keys: 4 },
  skills: [{ id: "stream", label: "Stream", normalizedValue: 82, value: 12.3 }],
  pauseCoach: { state: "Playing", sessionId: "browser-B", hasData: true },
  realtimeProducer: "browser",
});
assert.equal(window.__overlayLatestAnalysisSnapshot.difficulty.starRating, 4.89);
assert.equal(window.__overlayLatestAnalysisSnapshot.skills[0].id, "stream");

publishViewState({
  schemaVersion: 99,
  version: 99,
  producer: "native",
  beatmap: { id: "future-map" },
  realtime: { state: 3, mapTimeMs: 99999 },
  pauseCoach: { state: "Paused", sessionId: "future", hasData: true },
});
assert.equal(window.__overlayLatestViewState.version, 1);
assert.equal(window.__overlayLatestAnalysisSnapshot.beatmap.id, "674175");
assert.equal(window.__overlayViewStateProtocolError,
  "Unsupported overlay view-state schema version: 99");

publishViewState({
  schemaVersion: 1,
  version: 0,
  beatmapGeneration: 0,
  producer: "native",
  beatmap: { id: "stale-map" },
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  realtime: { state: 2, mapTimeMs: 1_000 },
  realtimeReplay: { mapProgressMs: 1_000, isProvisional: true },
  pauseCoach: { state: "Playing", sessionId: "stale", hasData: true },
});
assert.equal(window.__overlayLatestViewState.version, 1);
assert.equal(window.__overlayLatestAnalysisSnapshot.beatmap.id, "674175");

publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  beatmap: {
    id: "674175",
    backgroundUrl: "http://127.0.0.1:24050/files/beatmap/background?ts=674175",
  },
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  replay: { mapProgressMs: 1000, score: 1 },
  pauseCoach: { state: "Playing", sessionId: "browser-B", mapProgressMs: 1000, hasData: true },
  realtimeProducer: "browser",
});

const finalSnapshot = window.__overlayLatestAnalysisSnapshot;
assert.equal(finalSnapshot.pauseCoach.state, "Paused");
assert.equal(finalSnapshot.pauseCoach.sessionId, "native-A");
assert.equal(finalSnapshot.pauseCoach.mapProgressMs, 30000);
assert.equal(finalSnapshot.gameplay.state, "Paused");
assert.equal(finalSnapshot.replay.mapProgressMs, 30000);
assert.equal(finalSnapshot.beatmap.id, "674175");

// A native/browser realtime frame can omit headless difficulty fields. That
// absence must not erase an LN DAN rank already resolved for the map.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  beatmap: { id: "674175" },
  difficulty: { lnPercent: 51.4 },
  ranks: [{ systemId: "ln-dan", label: "LN DAN", value: "LN 6" }],
  pauseCoach: { state: "Playing", sessionId: "browser-B", hasData: true },
  realtimeProducer: "browser",
});
assert.equal(window.__overlayLatestAnalysisSnapshot.ranks.find(rank => rank.systemId === "ln-dan").value, "LN 6");

publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  beatmap: { id: "674175" },
  difficulty: {},
  ranks: [],
  pauseCoach: { state: "Playing", sessionId: "browser-B", hasData: true },
  realtimeProducer: "browser",
});
assert.equal(window.__overlayLatestAnalysisSnapshot.ranks.find(rank => rank.systemId === "ln-dan").value, "LN 6");

// Once the browser supplies the beatmap identity for the same live attempt,
// its metadata may be merged, but native rolling-window values remain
// authoritative. Otherwise the two sources alternate between different
// sample windows and the card visibly jumps every few hundred milliseconds.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  beatmap: { id: "674175" },
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  replay: { mapProgressMs: 31000, score: 124000 },
  pauseCoach: { state: "Playing", sessionId: "browser-B", mapProgressMs: 31000, hasData: true },
  realtimeProducer: "browser",
});

const updatedSnapshot = window.__overlayLatestAnalysisSnapshot;
assert.equal(updatedSnapshot.pauseCoach.state, "Paused");
assert.equal(updatedSnapshot.pauseCoach.sessionId, "native-A");
assert.equal(updatedSnapshot.pauseCoach.mapProgressMs, 30000);
assert.equal(updatedSnapshot.gameplay.state, "Paused");
assert.equal(updatedSnapshot.replay.mapProgressMs, 30000);
assert.equal(updatedSnapshot.replay.score, 123456);
assert.equal(updatedSnapshot.beatmap.id, "674175");

// Even without beatmap identity, an independently generated browser session
// must not release native authority. Only a native producer may establish a
// different native session in this identity-poor state.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  beatmap: {},
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  replay: { mapProgressMs: 32000, score: 125000 },
  pauseCoach: { state: "Playing", sessionId: "browser-C", mapProgressMs: 32000, hasData: true },
  realtimeProducer: "browser",
});

const afterIdentityPoorBrowserUpdate = window.__overlayLatestAnalysisSnapshot;
assert.equal(afterIdentityPoorBrowserUpdate.pauseCoach.sessionId, "native-A");
assert.equal(afterIdentityPoorBrowserUpdate.pauseCoach.state, "Paused");

publish({
  schemaVersion: 1,
  sourceId: "headless-analysis",
  beatmap: {},
  gameplay: { state: "WaitingForGame", isPlaying: false, isPaused: false },
  replay: { mapProgressMs: 1000, score: 1 },
  pauseCoach: { state: "WaitingForGame", sessionId: "", mapProgressMs: 1000, hasData: false },
});

const afterPartialUpdate = window.__overlayLatestAnalysisSnapshot;
assert.equal(afterPartialUpdate.pauseCoach.state, "Paused");
assert.equal(afterPartialUpdate.pauseCoach.sessionId, "native-A");
assert.equal(afterPartialUpdate.pauseCoach.mapProgressMs, 30000);
assert.equal(afterPartialUpdate.gameplay.state, "Paused");
assert.equal(afterPartialUpdate.replay.mapProgressMs, 30000);
assert.equal(afterPartialUpdate.replay.score, 123456);
assert.equal(afterPartialUpdate.beatmap.id, "674175");

// A later native frame for the same map can omit browser-only presentation
// metadata. The already-known background must survive that authoritative
// update instead of flashing back to the default background.
publishViewState({
  version: 2,
  beatmapGeneration: 1,
  producer: "native",
  beatmapId: "674175",
  beatmap: { id: "674175" },
  gameplay: { state: "Paused", isPlaying: true, isPaused: true },
  realtime: { state: 3, mapTimeMs: 30500, score: 124500 },
  realtimeReplay: { mapProgressMs: 30500, score: 124500, isProvisional: true },
  pauseCoach: { state: "Paused", sessionId: "native-A", mapProgressMs: 30500, hasData: true },
});
assert.equal(window.__overlayLatestAnalysisSnapshot.beatmap.backgroundUrl,
  "http://127.0.0.1:24050/files/beatmap/background?ts=674175");
assert.equal(window.__overlayLatestAnalysisSnapshot.replay.columns[0].biasMs, 8.5);
assert.equal(window.__overlayLatestAnalysisSnapshot.difficulty.starRating, 4.89);
assert.equal(window.__overlayLatestAnalysisSnapshot.skills[0].id, "stream");
assert.equal(documentClasses.has("launcher-osu-minimized"), false);

// A browser frame for another map can be stale while the native polling
// request is between two map-selection states. It must not release native
// authority or roll the card back to that browser identity.
publish({
  schemaVersion: 1,
  sourceId: "browser-adapter",
  beatmap: { id: "998877" },
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  replay: { mapProgressMs: 1000, score: 10, isProvisional: true, hasData: true },
  pauseCoach: { state: "Playing", sessionId: "B", mapProgressMs: 1000, hasData: true },
  realtimeProducer: "browser",
});

const staleBrowserSnapshot = window.__overlayLatestAnalysisSnapshot;
assert.equal(staleBrowserSnapshot.beatmap.id, "674175");
assert.equal(staleBrowserSnapshot.pauseCoach.sessionId, "native-A");

// Only the native producer establishes the new map. Once it does, a browser
// frame for that same map is allowed to provide presentation metadata.
publishViewState({
  version: 4,
  beatmapGeneration: 3,
  producer: "native",
  beatmapId: "998877",
  beatmap: { id: "998877" },
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  realtime: { state: 2, mapTimeMs: 1000, score: 10 },
  realtimeReplay: { mapProgressMs: 1000, score: 10, isProvisional: true },
  pauseCoach: { state: "Playing", sessionId: "native-B", mapProgressMs: 1000, hasData: true },
});

const newMapSnapshot = window.__overlayLatestAnalysisSnapshot;
assert.equal(newMapSnapshot.beatmap.id, "998877");
assert.equal(newMapSnapshot.pauseCoach.sessionId, "native-B");
assert.equal(statusElement.textContent, "Beatmap 998877");
assert.equal(statusElement.className, "status ok");

// The source analyser can finish later and mutate only #status. Replaying an
// otherwise identical application snapshot must still reclaim that field;
// render-signature deduplication must not preserve the legacy error.
statusElement.textContent = "[Error] Rework failed: Beatmap mode is not mania";
statusElement.className = "status error marquee";
window.__overlayRenderAnalysisSnapshot(window.__overlayLatestAnalysisSnapshot);
assert.equal(statusElement.textContent, "Beatmap 998877");
assert.equal(statusElement.className, "status ok");

// The upstream MMA DOM can publish a failed/non-mania result without a
// positive beatmap identity while native Tosu has already selected another
// map. That browser frame must not attach its legacy error title or empty
// analysis fields to the current native id.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  beatmap: {
    title: "[Error] Rework failed: Beatmap mode is not mania",
    version: "Unknown Difficulty",
  },
  gameplay: { state: "Menu", isPlaying: false, isPaused: false },
  difficulty: { starRating: 0 },
  ranks: [],
  skills: [],
  replay: { mapProgressMs: 0, score: 0, isProvisional: true },
  pauseCoach: { state: "WaitingForGame", sessionId: "", hasData: false },
  realtimeProducer: "browser",
});
assert.equal(window.__overlayLatestAnalysisSnapshot.beatmap.id, "998877");
assert.equal(window.__overlayLatestAnalysisSnapshot.beatmap.title || "", "");
assert.equal(window.__overlayLatestAnalysisSnapshot.pauseCoach.sessionId, "native-B");
assert.equal(statusElement.textContent, "Beatmap 998877");

publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  beatmap: { id: "998877", title: "New map" },
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  replay: { mapProgressMs: 1100, score: 11, isProvisional: true },
  pauseCoach: { state: "Playing", sessionId: "browser-B", mapProgressMs: 1100, hasData: true },
  realtimeProducer: "browser",
});
assert.equal(window.__overlayLatestAnalysisSnapshot.beatmap.id, "998877");
assert.equal(window.__overlayLatestAnalysisSnapshot.pauseCoach.sessionId, "native-B");
assert.equal(statusElement.textContent,
  "Unknown Artist - New map [Unknown Difficulty] // Unknown Mapper");

// A cached headless result for the previous map may complete after the live
// adapter has already switched to the next map (especially while entering
// widget mode). It must not roll the visible card back to that old map.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "674175", title: "Old map", version: "Old" },
  gameplay: { state: "", isPlaying: null, isPaused: null },
  replay: {
    mapProgressMs: null,
    score: null,
    isProvisional: false,
    fidelity: "exact",
    hasData: true,
  },
  difficulty: { starRating: 8.5 },
  skills: [{ id: "stream", valueLabel: "old" }],
  extensions: { headlessReplay: true },
});

const afterStaleHeadless = window.__overlayLatestAnalysisSnapshot;
assert.equal(afterStaleHeadless.beatmap.id, "998877");
assert.equal(afterStaleHeadless.pauseCoach.sessionId, "native-B");
assert.equal(afterStaleHeadless.replay.mapProgressMs, 1000);

// A real headless result for a new map is still allowed through when it is
// not a replay of the previous cached document.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "776655", title: "New map", version: "New" },
  gameplay: { state: "", isPlaying: null, isPaused: null },
  difficulty: { starRating: 4.2 },
});
assert.equal(window.__overlayLatestAnalysisSnapshot.beatmap.id, "776655");

// Native-only frames still have enough identity to resolve the Tosu
// background endpoint. This covers a document that is recreated while the
// browser adapter is between websocket/HTTP frames.
publishViewState({
  version: 5,
  beatmapGeneration: 4,
  producer: "native",
  beatmapId: "776655",
  beatmap: { id: "776655" },
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  realtime: { state: 2, mapTimeMs: 2000, score: 20 },
  realtimeReplay: { mapProgressMs: 2000, score: 20, isProvisional: true },
  pauseCoach: { state: "Playing", sessionId: "native-C", mapProgressMs: 2000, hasData: true },
});

// Accepted snapshots are rendered synchronously; there is no startup settle
// or presentation throttle hiding the newest map/mod state.
assert.ok(renderCount > 0);
assert.match(styleProperties["--overlay-comp-cover"], /776655/);
console.log("renderer-native-precedence.test.js: all assertions passed");
