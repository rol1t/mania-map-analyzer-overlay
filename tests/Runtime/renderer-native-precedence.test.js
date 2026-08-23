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
const document = {
  documentElement: {
    classList: { contains: () => false },
    style: { setProperty() {}, removeProperty() {} },
  },
  getElementById: () => null,
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
  getComputedStyle: () => ({ getPropertyValue: () => "" }),
};
vm.createContext(context);
vm.runInContext(source, context, { filename: "renderer.js" });

function publish(snapshot) {
  window.dispatchEvent({ type: "analysis:snapshot", detail: snapshot });
}

publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  beatmap: { id: "674175" },
  gameplay: { state: "Paused", isPlaying: true, isPaused: true },
  replay: { mapProgressMs: 30000, score: 123456 },
  pauseCoach: { state: "Paused", sessionId: "A", mapProgressMs: 30000, hasData: true },
  extensions: { nativePauseCoach: true },
});

publish({
  schemaVersion: 1,
  sourceId: "browser-adapter",
  beatmap: {},
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  replay: { mapProgressMs: 1000, score: 1 },
  pauseCoach: { state: "Playing", sessionId: "", mapProgressMs: 1000, hasData: true },
});

const finalSnapshot = window.__overlayLatestAnalysisSnapshot;
assert.equal(finalSnapshot.pauseCoach.state, "Paused");
assert.equal(finalSnapshot.pauseCoach.sessionId, "A");
assert.equal(finalSnapshot.pauseCoach.mapProgressMs, 30000);
assert.equal(finalSnapshot.gameplay.state, "Paused");
assert.equal(finalSnapshot.replay.mapProgressMs, 30000);
assert.equal(finalSnapshot.beatmap.id, "674175");

// Once the browser supplies the beatmap identity for the same live attempt,
// its metadata may be merged, but native rolling-window values remain
// authoritative. Otherwise the two sources alternate between different
// sample windows and the card visibly jumps every few hundred milliseconds.
publish({
  schemaVersion: 1,
  sourceId: "browser-adapter",
  beatmap: { id: "674175" },
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  replay: { mapProgressMs: 31000, score: 124000 },
  pauseCoach: { state: "Playing", sessionId: "browser-session", mapProgressMs: 31000, hasData: true },
});

const updatedSnapshot = window.__overlayLatestAnalysisSnapshot;
assert.equal(updatedSnapshot.pauseCoach.state, "Paused");
assert.equal(updatedSnapshot.pauseCoach.sessionId, "A");
assert.equal(updatedSnapshot.pauseCoach.mapProgressMs, 30000);
assert.equal(updatedSnapshot.gameplay.state, "Paused");
assert.equal(updatedSnapshot.replay.mapProgressMs, 30000);
assert.equal(updatedSnapshot.replay.score, 123456);
assert.equal(updatedSnapshot.beatmap.id, "674175");

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
assert.equal(afterPartialUpdate.pauseCoach.sessionId, "A");
assert.equal(afterPartialUpdate.pauseCoach.mapProgressMs, 30000);
assert.equal(afterPartialUpdate.gameplay.state, "Paused");
assert.equal(afterPartialUpdate.replay.mapProgressMs, 30000);
assert.equal(afterPartialUpdate.replay.score, 123456);
assert.equal(afterPartialUpdate.beatmap.id, "674175");

// A live adapter frame for a genuinely new map is positive evidence and must
// replace the previous attempt.
publish({
  schemaVersion: 1,
  sourceId: "browser-adapter",
  beatmap: { id: "998877" },
  gameplay: { state: "Playing", isPlaying: true, isPaused: false },
  replay: { mapProgressMs: 1000, score: 10, isProvisional: true, hasData: true },
  pauseCoach: { state: "Playing", sessionId: "B", mapProgressMs: 1000, hasData: true },
});

const newMapSnapshot = window.__overlayLatestAnalysisSnapshot;
assert.equal(newMapSnapshot.beatmap.id, "998877");
assert.equal(newMapSnapshot.pauseCoach.sessionId, "B");

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
assert.equal(afterStaleHeadless.pauseCoach.sessionId, "B");
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

// A fresh document settles its initial headless/browser/native burst before
// rendering; subsequent frames are coalesced into one latest-wins render.
assert.equal(renderCount, 0);
setTimeout(() => {
  try {
    assert.equal(renderCount, 1);
    console.log("renderer-native-precedence.test.js: all assertions passed");
  } catch (exception) {
    console.error(exception);
    process.exitCode = 1;
  }
}, 1100);
