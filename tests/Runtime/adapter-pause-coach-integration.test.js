// Adapter-level regression test for the real osu!lazer pause transition.
// Raw Tosu payloads enter through applyTosuPayload, exactly as websocket and
// /json/v2 polling updates do; the test never calls the runtime directly.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");
const pauseCoachSource = fs.readFileSync(path.join(root, "assets/overlay/runtime/pause-coach.js"), "utf8");
const adapterSource = fs.readFileSync(path.join(root, "assets/analyzers/mania-map-analyser/adapter.js"), "utf8");

function nodeStub() {
  return {
    classList: { contains: () => false, add() {}, remove() {} },
    style: { getPropertyValue: () => "", setProperty() {} },
    children: [],
    firstChild: null,
    hidden: false,
    textContent: "",
    parentNode: null,
    getAttribute: () => null,
    setAttribute() {},
    removeAttribute() {},
    querySelector: () => null,
    querySelectorAll: () => [],
    appendChild(child) { this.children.push(child); child.parentNode = this; return child; },
    insertBefore(child) { this.children.push(child); child.parentNode = this; return child; },
    remove() {},
  };
}

function createScenario(source) {
  const listeners = new Map();
  const snapshots = [];
  const hostMessages = [];
  const httpPayloads = [];
  const card = nodeStub();
  const documentElement = nodeStub();
  const document = {
    body: nodeStub(),
    documentElement,
    createElement: () => nodeStub(),
    getElementById: () => null,
    querySelector: selector => selector === ".main-card" ? card : null,
  };
  const window = {
    __overlayAdapterTestMode: true,
    COUNTER_PATH: "/",
    __overlayHostSend: message => hostMessages.push(message),
    setTimeout: () => 1,
    clearTimeout() {},
    setInterval: () => 1,
    clearInterval() {},
    dispatchEvent(event) {
      if (event && event.type === "analysis:snapshot" && event.detail) snapshots.push(event.detail);
      for (const callback of listeners.get(event.type) || []) callback(event);
      return true;
    },
    addEventListener(type, callback) {
      const callbacks = listeners.get(type) || [];
      callbacks.push(callback);
      listeners.set(type, callbacks);
    },
    removeEventListener() {},
  };
  class CustomEventStub {
    constructor(type, init) { this.type = type; this.detail = init && init.detail; }
  }
  class MutationObserverStub {
    observe() {}
    disconnect() {}
  }
  class WebSocketStub {
    addEventListener() {}
    send() {}
    close() {}
  }
  const context = {
    window,
    document,
    location: { host: "127.0.0.1:24050", pathname: "/" },
    console,
    Date,
    JSON,
    Math,
    Number,
    String,
    Object,
    Array,
    Boolean,
    CustomEvent: CustomEventStub,
    MutationObserver: MutationObserverStub,
    WebSocket: WebSocketStub,
    getComputedStyle: () => ({ getPropertyValue: () => "" }),
    requestAnimationFrame: callback => { callback(); return 0; },
    cancelAnimationFrame() {},
    fetch: async () => {
      const payload = httpPayloads.shift();
      return payload === undefined ? { ok: false } : { ok: true, json: async () => payload };
    },
  };
  vm.createContext(context);
  vm.runInContext(pauseCoachSource, context, { filename: "pause-coach.js" });
  vm.runInContext(adapterSource, context, { filename: "adapter.js" });
  assert.equal(typeof window.__overlayAnalyzerAdapterTest?.applyTosuPayload, "function");

  return {
    snapshots,
    hostMessages,
    apply(payload) {
      window.__overlayAnalyzerAdapterTest.applyTosuPayload(payload, source);
      window.__overlayAnalyzerAdapterTest.publish();
    },
    async poll(payload) {
      httpPayloads.push(payload);
      await window.__overlayAnalyzerAdapterTest.pollState();
      window.__overlayAnalyzerAdapterTest.publish();
    },
    dispose() { window.__overlayAnalyzerAdapter.dispose(); },
  };
}

function payload(stateName, paused, mapTimeMs, score, accuracy, hitCount, offsets) {
  return {
    state: { name: stateName, number: 2 },
    game: { focused: true, paused },
    beatmap: {
      id: "lazer-map-1",
      hash: "lazer-hash-1",
      time: { live: mapTimeMs },
      metadata: { artist: "Test", title: "Lazer", version: "Mania" },
    },
    play: {
      score,
      accuracy,
      combo: { current: hitCount, max: hitCount },
      healthBar: { normal: 0.9 },
      hits: { "300": hitCount, "200": 0, "100": 0, "50": 0, geki: 0, katu: 0, "0": 0 },
      hitErrorArray: offsets,
      unstableRate: 28,
      mods: { array: [] },
    },
  };
}

async function run(source) {
  const scenario = createScenario(source);
  // Adapter startup performs one background HTTP probe. Let that probe settle
  // before the deterministic sequence begins so it cannot consume the first
  // fixture or hold the in-flight guard.
  await new Promise(resolve => setImmediate(resolve));
  const sequence = [
    payload("SelectPlay", false, 0, 0, 100, 0, []),
    payload("Play", false, 0, 1000, 99.5, 2, [0, 1]),
    payload("Play", false, 5_000, 5000, 99, 8, [0, 1, -1, 2, 0, 1, -2, 1]),
    payload("Play", false, 10_000, 10000, 98.5, 16, [0, 1, -1, 2, 0, 1, -2, 1, 2, -1, 0, 1]),
    payload("Play", false, 20_000, 20000, 98, 25, [0, 1, -1, 2, 0, 1, -2, 1, 2, -1, 0, 1, 3, -2, 1, 0]),
    payload("Play", false, 25_000, 25000, 97.5, 34, [0, 1, -1, 2, 0, 1, -2, 1, 2, -1, 0, 1, 3, -2, 1, 0, 2, -1]),
    // This is the normal lazer pause representation. The state remains Play/2.
    payload("Play", true, 25_000, 25000, 97.5, 34, [0, 1, -1, 2, 0, 1, -2, 1, 2, -1, 0, 1, 3, -2, 1, 0, 2, -1]),
  ];
  for (const item of sequence) {
    if (source === "browser-http") await scenario.poll(item);
    else scenario.apply(item);
  }
  const playing = scenario.snapshots.filter(snapshot => snapshot.pauseCoach && snapshot.pauseCoach.state === "Playing").at(-1);
  const paused = scenario.snapshots.at(-1);
  assert.ok(playing, `${source}: session must start during Play`);
  assert.equal(playing.gameplay.isPlaying, true);
  assert.equal(playing.gameplay.isPaused, false);
  assert.equal(playing.pauseCoach.mapProgressMs, 25_000);
  assert.ok((playing.pauseCoach.overall.hits || 0) > 0, `${source}: telemetry must accumulate while playing`);
  assert.ok(paused && paused.pauseCoach, `${source}: final snapshot must contain Pause Coach`);
  assert.notEqual(paused.pauseCoach.state, "WaitingForGame");
  assert.ok(["Paused", "InsufficientData"].includes(paused.pauseCoach.state));
  assert.equal(paused.gameplay.isPlaying, true);
  assert.equal(paused.gameplay.isPaused, true);
  assert.equal(paused.pauseCoach.sessionId, playing.pauseCoach.sessionId, `${source}: pause must retain session`);
  assert.ok((paused.pauseCoach.timing.sampleCount || 0) > 0, `${source}: recent timing window must retain data`);
  assert.ok(scenario.hostMessages.some(message => message.startsWith("overlay:pause-coach-debug:")));
  scenario.dispose();
}

Promise.all([run("websocket"), run("browser-http")]).then(() => {
  console.log("adapter-pause-coach-integration.test.js: all assertions passed");
}).catch(error => {
  console.error(error);
  process.exitCode = 1;
});
