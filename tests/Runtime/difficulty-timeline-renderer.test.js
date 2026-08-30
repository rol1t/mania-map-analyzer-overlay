"use strict";

// Renderer contract test for the analyzer-provided difficulty timeline.
// The test uses the same public snapshot entry point as the WebView bridge and
// deliberately does not call private chart helpers.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "assets/overlay/runtime/renderer.js"), "utf8");
const nodes = new Map();
const listeners = new Map();
const styleProperties = {};
let renderCount = 0;

function createClassList() {
  const values = new Set();
  return {
    contains(value) { return values.has(value); },
    add(...entries) { entries.forEach(entry => values.add(entry)); },
    remove(...entries) { entries.forEach(entry => values.delete(entry)); },
    toggle(value, force) {
      if (force === true) values.add(value);
      else if (force === false) values.delete(value);
      else if (values.has(value)) values.delete(value);
      else values.add(value);
      return values.has(value);
    },
  };
}

function createNode() {
  const attributes = {};
  return {
    attributes,
    classList: createClassList(),
    dataset: {},
    hidden: false,
    textContent: "",
    style: {
      setProperty(name, value) { styleProperties[name] = value; },
      removeProperty(name) { delete styleProperties[name]; },
      getPropertyValue(name) { return styleProperties[name] || ""; },
    },
    setAttribute(name, value) { attributes[name] = String(value); },
    getAttribute(name) { return Object.prototype.hasOwnProperty.call(attributes, name) ? attributes[name] : null; },
    removeAttribute(name) { delete attributes[name]; },
    appendChild() {},
    append() {},
    querySelector() { return null; },
    querySelectorAll() { return []; },
  };
}

[
  "overlay-difficulty-timeline",
  "overlay-difficulty-timeline-area",
  "overlay-difficulty-timeline-line",
  "overlay-difficulty-timeline-cursor",
  "overlay-difficulty-timeline-start",
  "overlay-difficulty-timeline-current",
  "overlay-difficulty-timeline-end",
].forEach(id => nodes.set(id, createNode()));

const mainCard = createNode();
const document = {
  documentElement: {
    classList: createClassList(),
    style: {
      setProperty(name, value) { styleProperties[name] = value; },
      removeProperty(name) { delete styleProperties[name]; },
    },
  },
  getElementById(id) { return nodes.get(id) || null; },
  querySelector(selector) { return selector === ".main-card" ? mainCard : null; },
};
const window = {
  addEventListener(type, callback) {
    const callbacks = listeners.get(type) || [];
    callbacks.push(callback);
    listeners.set(type, callbacks);
  },
  dispatchEvent(event) {
    for (const callback of listeners.get(event.type) || []) callback(event);
    return true;
  },
  setTimeout,
  clearTimeout,
  __overlayHostQueueSizeReport() { renderCount += 1; },
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
  window.__overlayRenderAnalysisSnapshot(snapshot);
}

const timeline = {
  times: [0, 1000, 2500, 4000],
  values: [2.1, 3.4, 2.8, 4.2],
};

publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "timeline-map" },
  difficulty: { timeline },
  replay: { mapProgressMs: 2500 },
});

const container = nodes.get("overlay-difficulty-timeline");
const area = nodes.get("overlay-difficulty-timeline-area");
const line = nodes.get("overlay-difficulty-timeline-line");
const cursor = nodes.get("overlay-difficulty-timeline-cursor");
assert.equal(container.hidden, false);
assert.match(area.attributes.d, /^M /);
assert.match(line.attributes.d, /^M /);
assert.equal(nodes.get("overlay-difficulty-timeline-start").textContent, "0:00");
assert.equal(nodes.get("overlay-difficulty-timeline-end").textContent, "0:04");
assert.equal(cursor.attributes.x1, "625.00");
assert.equal(nodes.get("overlay-difficulty-timeline-current").textContent, "0:03 · 2.8");

const firstLinePath = line.attributes.d;
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "timeline-map" },
  difficulty: { timeline },
  replay: { mapProgressMs: 3500 },
});
assert.equal(line.attributes.d, firstLinePath);
assert.equal(cursor.attributes.x1, "875.00");
assert.equal(nodes.get("overlay-difficulty-timeline-current").textContent, "0:04 · 3.73");

// A browser realtime frame omits analyzer metrics. Renderer merging must keep
// the analyzer-owned series while moving the playback cursor forward.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser",
  realtimeProducer: "browser",
  beatmap: { id: "timeline-map" },
  difficulty: {},
  replay: { mapProgressMs: 3750, isProvisional: true },
  pauseCoach: { state: "Playing", hasData: true },
});
assert.deepEqual(window.__overlayLatestAnalysisSnapshot.difficulty.timeline, timeline);
assert.equal(cursor.attributes.x1, "937.50");

// A map without a graph hides the presenter; a later valid analysis for that
// map reveals it again without requiring a document/preset recreation.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "next-map" },
  difficulty: {},
});
assert.equal(container.hidden, true);
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "next-map" },
  difficulty: { timeline: { points: [
    { timeMs: 0, value: 1 },
    { timeMs: 2000, value: 2 },
  ] } },
});
assert.equal(container.hidden, false);
assert.equal(cursor.hidden, true);
assert.ok(renderCount >= 4);

console.log("difficulty-timeline-renderer.test.js: all assertions passed");
