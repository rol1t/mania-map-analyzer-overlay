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

function createNode(tagName = "div") {
  const attributes = {};
  const children = [];
  return {
    tagName,
    attributes,
    children,
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
    appendChild(child) { children.push(child); return child; },
    append() {},
    querySelector() { return null; },
    querySelectorAll() { return []; },
  };
}

[
  "overlay-difficulty-timeline",
  "overlay-difficulty-timeline-area",
  "overlay-difficulty-timeline-line",
  "overlay-difficulty-timeline-ln-area",
  "overlay-difficulty-timeline-ln-line",
  "overlay-difficulty-timeline-cursor",
  "overlay-difficulty-timeline-start",
  "overlay-difficulty-timeline-current",
  "overlay-difficulty-timeline-end",
  "overlay-difficulty-timeline-rice-legend",
  "overlay-difficulty-timeline-ln-legend",
  "overlay-replay-map-time",
  "overlay-summary-star",
  "overlay-summary-star-method",
  "overlay-summary-rc-dan-card",
  "overlay-summary-ln-dan-card",
  "overlay-summary-rc-dan",
  "overlay-summary-ln-dan",
  "overlay-summary-rc-dan-value",
  "overlay-summary-ln-dan-value",
  "overlay-comp-chart",
].forEach(id => nodes.set(id, createNode()));

const mainCard = createNode();
// Match the preset markup: LN SVG paths are initially hidden. This catches
// regressions where assigning SVGElement.hidden leaves the literal attribute
// in place and the browser keeps the valid LN series invisible.
nodes.get("overlay-difficulty-timeline-ln-area").setAttribute("hidden", "");
nodes.get("overlay-difficulty-timeline-ln-line").setAttribute("hidden", "");
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
  createElement(tagName) { return createNode(tagName); },
  createElementNS(namespace, tagName) { return createNode(tagName); },
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
  __overlayDanAssets: {
    "reform/7.svg": "data:image/svg+xml;base64,cmVmb3JtLTc=",
    "reform/alpha.webp": "data:image/webp;base64,YWxwaGE=",
    "ln/6.svg": "data:image/svg+xml;base64,bG4tNg==",
    "ln/10.svg": "data:image/svg+xml;base64,bG4tMTA=",
    "6k/terra.svg": "data:image/svg+xml;base64,NmstdGVycmE=",
    "6k/ln-finish.svg": "data:image/svg+xml;base64,NmstbG4tZmluaXNo",
    "7k/gamma.svg": "data:image/svg+xml;base64,N2stZ2FtbWE=",
    "7k/ln-stellium.svg": "data:image/svg+xml;base64,N2stbG4tc3RlbGxpdW0=",
  },
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
  difficulty: {
    timeline,
    starRating: 4.89,
    starLabel: "4.89 SR",
    starRatingProvider: "mania-map-analyser-headless",
    starRatingAlgorithm: "Roxy",
  },
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
assert.equal(nodes.get("overlay-replay-map-time").textContent, "0:03 / 0:04");
assert.equal(nodes.get("overlay-summary-star").attributes["data-star-rating-color"], "rgb(255, 79, 111)");
assert.equal(nodes.get("overlay-summary-star-method").textContent, "Roxy");
assert.match(nodes.get("overlay-summary-star-method").attributes.title, /not osu!'s official star rating/i);

// DAN presentation keeps both estimates available, but only one ladder is
// visually authoritative. Labels resolve to the packaged Mania Hub catalog
// without replacing their full accessible text.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "timeline-map" },
  difficulty: { keys: 4, lnPercent: 20 },
  ranks: [
    { systemId: "rc-dan", value: "Reform 7 mid/high", numericValue: 7.2, isPrimary: true },
    { systemId: "ln-dan", value: "LN 6 mid", isPrimary: false },
  ],
});
assert.equal(nodes.get("overlay-summary-rc-dan").attributes["data-dan-badge"], "7");
assert.equal(nodes.get("overlay-summary-rc-dan").attributes["data-dan-tier"], "+");
assert.equal(nodes.get("overlay-summary-ln-dan").attributes["data-dan-badge"], "6");
assert.equal(nodes.get("overlay-summary-rc-dan").textContent, "Reform 7");
assert.equal(nodes.get("overlay-summary-ln-dan").textContent, "LN 6");
assert.equal(nodes.get("overlay-summary-rc-dan").attributes["aria-label"], "Reform 7 mid/high");
assert.equal(nodes.get("overlay-summary-ln-dan").attributes["aria-label"], "LN 6 mid");
assert.equal(nodes.get("overlay-summary-star-method").textContent, "Roxy");
assert.equal(nodes.get("overlay-summary-rc-dan-value").textContent, "≈ 7.20");
assert.equal(nodes.get("overlay-summary-ln-dan-value").textContent, "");
assert.equal(nodes.get("overlay-summary-rc-dan").attributes["data-dan-image"], "reform/7.svg");
assert.equal(nodes.get("overlay-summary-ln-dan").attributes["data-dan-image"], "ln/6.svg");
assert.equal(nodes.get("overlay-summary-rc-dan").classList.contains("has-dan-image"), true);
assert.equal(nodes.get("overlay-summary-rc-dan-card").classList.contains("is-dan-primary"), true);
assert.equal(nodes.get("overlay-summary-ln-dan-card").classList.contains("is-dan-reference"), true);

publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "timeline-map" },
  difficulty: { keys: 4, lnPercent: 55 },
  ranks: [
    { systemId: "rc-dan", value: "Alpha high", isPrimary: false },
    { systemId: "ln-dan", value: "LN 10 mid", isPrimary: true },
  ],
});
assert.equal(nodes.get("overlay-summary-rc-dan").attributes["data-dan-badge"], "α");
assert.equal(nodes.get("overlay-summary-rc-dan").attributes["data-dan-tier"], "++");
assert.equal(nodes.get("overlay-summary-ln-dan").attributes["data-dan-badge"], "10");
assert.equal(nodes.get("overlay-summary-rc-dan").textContent, "Alpha");
assert.equal(nodes.get("overlay-summary-ln-dan").textContent, "LN 10");
assert.equal(nodes.get("overlay-summary-rc-dan").attributes["data-dan-image"], "reform/alpha.webp");
assert.equal(nodes.get("overlay-summary-ln-dan").attributes["data-dan-image"], "ln/10.svg");
assert.equal(nodes.get("overlay-summary-ln-dan-card").classList.contains("is-dan-primary"), true);
assert.equal(nodes.get("overlay-summary-rc-dan-card").classList.contains("is-dan-reference"), true);

publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "timeline-map" },
  difficulty: { keys: 6, lnPercent: 20 },
  ranks: [
    { systemId: "rc-dan", value: "Regular Terra mid", isPrimary: true },
    { systemId: "ln-dan", value: "LN Finish high", isPrimary: false },
  ],
});
assert.equal(nodes.get("overlay-summary-rc-dan").attributes["data-dan-image"], "6k/terra.svg");
assert.equal(nodes.get("overlay-summary-ln-dan").attributes["data-dan-image"], "6k/ln-finish.svg");

publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "timeline-map" },
  difficulty: { keys: 7, lnPercent: 50 },
  ranks: [
    { systemId: "rc-dan", value: "Regular Gamma mid", isPrimary: false },
    { systemId: "ln-dan", value: "LN Stellium mid/high", isPrimary: true },
  ],
});
assert.equal(nodes.get("overlay-summary-rc-dan").attributes["data-dan-image"], "7k/gamma.svg");
assert.equal(nodes.get("overlay-summary-ln-dan").attributes["data-dan-image"], "7k/ln-stellium.svg");

const lnTimeline = {
  points: [
    { timeMs: 0, value: 1.2 },
    { timeMs: 1000, value: 2.4 },
    { timeMs: 2500, value: 1.8 },
    { timeMs: 4000, value: 3.1 },
  ],
};
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "timeline-map" },
  difficulty: { riceTimeline: timeline, lnTimeline: lnTimeline },
  replay: { mapProgressMs: 3500 },
});
assert.match(line.attributes.d, /^M /);
assert.match(nodes.get("overlay-difficulty-timeline-ln-line").attributes.d, /^M /);
assert.match(nodes.get("overlay-difficulty-timeline-ln-area").attributes.d, /^M /);
assert.equal(nodes.get("overlay-difficulty-timeline-ln-line").getAttribute("hidden"), null);
assert.equal(nodes.get("overlay-difficulty-timeline-ln-area").getAttribute("hidden"), null);
assert.equal(nodes.get("overlay-difficulty-timeline-rice-legend").textContent, "Rice");
assert.equal(nodes.get("overlay-difficulty-timeline-ln-legend").textContent, "LN");
assert.equal(cursor.attributes.x1, "875.00");
assert.equal(nodes.get("overlay-difficulty-timeline-current").textContent, "0:04 · Rice 3.73 · LN 2.67");
const firstDualLinePath = line.attributes.d;

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
assert.deepEqual(window.__overlayLatestAnalysisSnapshot.difficulty.riceTimeline, timeline);
assert.deepEqual(window.__overlayLatestAnalysisSnapshot.difficulty.lnTimeline, lnTimeline);
assert.equal(line.attributes.d, firstDualLinePath);
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

// Match the non-linear osu! difficulty spectrum at the values shown in the
// lazer song-select reference. Interpolation uses RGB gamma 2.2, not equal
// spacing over a generic 0..10 gradient.
[
  [3.09, "rgb(222, 244, 89)"],
  [3.19, "rgb(234, 242, 90)"],
  [3.96, "rgb(253, 167, 101)"],
  [4.13, "rgb(254, 141, 103)"],
  [5.13, "rgb(242, 76, 134)"],
  [5.74, "rgb(202, 70, 180)"],
  [8.78, "rgb(11, 9, 63)"],
].forEach(function ([starRating, expectedColor]) {
  publish({
    schemaVersion: 1,
    sourceId: "mania-map-analyser-headless",
    beatmap: { id: `star-colour-${starRating}` },
    difficulty: { starRating: starRating, starLabel: `${starRating} SR` },
  });
  assert.equal(nodes.get("overlay-summary-star").attributes["data-star-rating-color"], expectedColor);
});
assert.equal(nodes.get("overlay-summary-star").attributes["data-star-rating-contrast"], "light-outline");

publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "star-colour-readable" },
  difficulty: { starRating: 5.74, starLabel: "5.74 SR" },
});
assert.equal(nodes.get("overlay-summary-star").attributes["data-star-rating-contrast"], undefined);

// The Radar preset is presentation-only: it converts the canonical skill
// array into an SVG profile without calculating or changing analyzer values.
document.documentElement.classList.add("overlay-layout-companella-radar");
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "radar-map" },
  difficulty: { starRating: 6.2 },
  skills: [
    { id: "skills.overall", label: "Overall", value: 17.72, normalizedValue: 17.72 },
    { id: "skills.stream", label: "Stream", value: 15.2, normalizedValue: 60 },
    { id: "skills.jumpstream", label: "Jumpstream", value: 18.1, normalizedValue: 72 },
    { id: "skills.handstream", label: "Handstream", value: 13.5, normalizedValue: 54 },
    { id: "skills.stamina", label: "Stamina", value: 22.6, normalizedValue: 90 },
    { id: "skills.jackspeed", label: "JackSpeed", value: 10.8, normalizedValue: 43 },
    { id: "skills.chordjack", label: "Chordjack", value: 19.1, normalizedValue: 76 },
    { id: "skills.technical", label: "Technical", value: 14.6, normalizedValue: 58 },
  ],
});
const radarChart = nodes.get("overlay-comp-chart");
const radar = radarChart.children.find(child => child.attributes.class === "overlay-comp-radar");
const overallCard = radarChart.children.find(child => child.className === "overlay-comp-radar-overall");
assert.ok(radar);
assert.ok(overallCard);
assert.equal(overallCard.children.find(child => child.className === "overlay-comp-radar-overall-value").textContent, "17.72");
assert.equal(overallCard.children.find(child => child.className === "overlay-comp-radar-overall-unit").textContent, "MSD");
assert.equal(radar.attributes["aria-label"], "Map skill profile");
assert.equal(radar.attributes.viewBox, "0 0 500 390");
assert.equal(radar.attributes["data-radar-scale"], "relative-peak");
assert.equal(radar.attributes["data-radar-peak"], "90.00");
assert.equal(radar.children.filter(child => child.attributes.class === "overlay-comp-radar-grid").length, 4);
assert.equal(radar.children.filter(child => child.attributes.class === "overlay-comp-radar-spoke").length, 7);
assert.equal(radar.children.filter(child => child.attributes.class === "overlay-comp-radar-point").length, 7);
assert.equal(radar.children.filter(child => child.attributes.class === "overlay-comp-radar-label").length, 7);
assert.equal(radar.children.filter(child => child.attributes.class === "overlay-comp-radar-value").length, 7);
assert.equal(radar.children.find(child => child.attributes.class === "overlay-comp-radar-label").textContent, "Stream");
assert.equal(radar.children.find(child => child.attributes.class === "overlay-comp-radar-value").textContent, "15.2");
for (const skillId of ["skills.handstream", "skills.chordjack"]) {
  const label = radar.children.find(child =>
    child.attributes.class === "overlay-comp-radar-label" && child.attributes["data-radar-skill"] === skillId);
  const value = radar.children.find(child =>
    child.attributes.class === "overlay-comp-radar-value" && child.attributes["data-radar-skill"] === skillId);
  assert.ok(label && value);
  assert.ok(
    Number(value.attributes.y) - Number(label.attributes.y) >= 20,
    `${skillId} label and value must be vertically separated on a lateral axis`);
}

// Low absolute analyzer values must still produce a readable relative shape.
// Labels retain the original values, while geometry is scaled to the local
// peak instead of collapsing into the centre of the chart.
publish({
  schemaVersion: 1,
  sourceId: "mania-map-analyser-headless",
  beatmap: { id: "low-star-radar-map" },
  difficulty: { starRating: 2.1 },
  skills: [
    { label: "Jack", normalizedValue: 4 },
    { label: "Stream", normalizedValue: 8 },
    { label: "Jumpstream", normalizedValue: 12 },
    { label: "Handstream", normalizedValue: 16 },
  ],
});
const lowRadar = radarChart.children.filter(child => child.attributes.class === "overlay-comp-radar").at(-1);
const lowRadarPoints = lowRadar.children.filter(child => child.attributes.class === "overlay-comp-radar-point");
const lowRadarValues = lowRadar.children.filter(child => child.attributes.class === "overlay-comp-radar-value");
assert.equal(lowRadar.attributes["data-radar-peak"], "16.00");
assert.equal(lowRadarValues[0].textContent, "4%");
assert.ok(Number(lowRadarPoints[0].attributes.cy) < 140, "low values should not collapse into the radar centre");

assert.ok(renderCount >= 4);

console.log("difficulty-timeline-renderer.test.js: all assertions passed");
