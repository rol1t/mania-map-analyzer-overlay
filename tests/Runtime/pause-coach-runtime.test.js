"use strict";

// Direct tests for the shipped WebView runtime. These intentionally use the
// shapes emitted by Tosu v2 rather than the simplified C# test model.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "assets/overlay/runtime/pause-coach.js"), "utf8");
const tosuFixture = JSON.parse(fs.readFileSync(path.join(root, "tests/fixtures/tosu-v2-pause-coach.json"), "utf8"));
const context = { window: {}, console, Date };
vm.createContext(context);
vm.runInContext(source, context, { filename: "pause-coach.js" });
const createRuntime = context.window.__createRealtimePauseCoachRuntime;
assert.equal(typeof createRuntime, "function");
assert.equal(tosuFixture.play.combo.current, 123);
assert.equal(tosuFixture.play.hits.geki, 4);

function fixture(timeMs, receivedAt, counts, extra = {}) {
  const state = extra.state || { name: "Playing", isPlaying: true };
  return {
    receivedAt,
    mapTimeMs: timeMs,
    gameplay: state,
    beatmap: extra.identity === false ? undefined : { id: "map-1", hash: "hash-1" },
    play: {
      score: counts.score ?? counts.hits * 100,
      accuracy: extra.accuracy ?? 98.5,
      hits: {
        "0": counts.misses,
        "50": counts.fifty,
        "100": counts.hundred,
        "300": counts.threeHundred,
        geki: counts.geki,
        katu: counts.katu,
      },
      combo: { current: counts.combo ?? counts.hits, max: counts.maxCombo ?? counts.hits },
      healthBar: { normal: 0.82, smooth: 0.8 },
      mods: extra.mods ?? { array: [{ acronym: "DT" }, { acronym: "NC" }] },
      hitErrorArray: extra.offsets || [],
      unstableRate: extra.unstableRate ?? 32.1,
    },
  };
}

function counts(hits, misses, offsetCount, extra = {}) {
  return {
    hits,
    misses,
    threeHundred: hits,
    geki: 0,
    katu: 0,
    hundred: 0,
    fifty: 0,
    combo: hits,
    maxCombo: hits,
    ...extra,
  };
}

function runWindow(pollTimes) {
  const runtime = createRuntime({ minimumTimingSamples: 1 });
  let last = null;
  pollTimes.forEach((time, index) => {
    const value = counts(Math.floor(time / 5_000), time >= 30_000 ? 3 : time >= 20_000 ? 1 : 0, index + 1);
    value.threeHundred = value.hits;
    value.geki = 1;
    value.katu = 1;
    last = runtime.process(fixture(time, time, value, { offsets: Array.from({ length: index + 1 }, (_, i) => 10 + i) }));
  });
  return last;
}

// Real nested Tosu values are normalized once at the runtime boundary.
{
  const runtime = createRuntime({ minimumTimingSamples: 1 });
  runtime.process({ ...tosuFixture, receivedAt: 0, mapTimeMs: 0, gameplay: tosuFixture.state });
  const paused = runtime.process(fixture(20_000, 20_000, counts(60, 2, 2, { geki: 5, katu: 6, combo: 123, maxCombo: 456 }), {
    offsets: [4, 5],
    state: { name: "Pause", isPlaying: false, isPaused: true },
  }));
  assert.equal(paused.state, "Paused");
  assert.equal(paused.combo, 123);
  assert.equal(paused.maxCombo, 456);
  assert.equal(paused.health, 0.82);
  assert.deepEqual(Array.from(paused.mods), ["DT", "NC"]);
  assert.equal(paused.overall.accuracy, 0.985);
  assert.equal(paused.overall.unstableRate, 32.1);
  assert.equal(paused.overall.judgements.countGeki, 5);
  assert.equal(paused.overall.judgements.countKatu, 6);
}

for (const mods of ["dt nc", ["dt", "nc"], { array: [{ acronym: "dt" }, { acronym: "nc" }] }, { DT: true, NC: true }]) {
  const runtime = createRuntime({ minimumTimingSamples: 1 });
  runtime.process(fixture(0, 0, counts(1, 0, 1), { mods }));
  const result = runtime.process(fixture(20_000, 20_000, counts(2, 0, 2), { mods, state: { name: "Pause", isPaused: true } }));
  assert.deepEqual(Array.from(result.mods), ["DT", "NC"]);
}

// Accuracy accepts either Tosu unit and stores only 0..1.
{
  const a = createRuntime({ minimumTimingSamples: 1 });
  const b = createRuntime({ minimumTimingSamples: 1 });
  const c = counts(10, 0, 1);
  a.process(fixture(0, 0, c, { accuracy: 98.50, offsets: [1] }));
  b.process(fixture(0, 0, c, { accuracy: 0.985, offsets: [1] }));
  assert.equal(a.process(fixture(1_000, 1_000, c, { accuracy: 98.50, offsets: [1], state: { name: "Pause", isPaused: true } })).overall.accuracy, 0.985);
  assert.equal(b.process(fixture(1_000, 1_000, c, { accuracy: 0.985, offsets: [1], state: { name: "Pause", isPaused: true } })).overall.accuracy, 0.985);
}

// Recent misses are the complete map-time window, not the previous packet.
{
  const runtime = createRuntime({ minimumTimingSamples: 1 });
  runtime.process(fixture(0, 0, counts(0, 0, 1), { offsets: [0] }));
  runtime.process(fixture(10_000, 10_000, counts(10, 1, 2), { offsets: [0, 1] }));
  const paused = runtime.process(fixture(40_000, 40_000, counts(40, 4, 3), { offsets: [0, 1, 2], state: { name: "Pause", isPaused: true } }));
  assert.equal(paused.performance.recentMisses, 3);
  assert.equal(paused.recent.windowSeconds, 20);
}

// Diagnosis is updated while the map is still playing, not only after pause.
{
  const runtime = createRuntime({ minimumTimingSamples: 1 });
  const first = runtime.process(fixture(0, 0, counts(0, 0, 1), { offsets: [1] }));
  const live = runtime.process(fixture(20_000, 20_000, counts(20, 3, 2), { offsets: [1, 2], state: { name: "Playing", isPlaying: true } }));
  assert.equal(first.state, "Playing");
  assert.equal(live.state, "Playing");
  assert.ok(live.insights.length > 0);
  assert.equal(live.performance.recentMisses, 3);
}

// Opening the overlay while osu! is already paused bootstraps the active
// attempt from the map/telemetry packet instead of staying in WaitingForGame.
{
  const runtime = createRuntime({ minimumTimingSamples: 1 });
  const paused = runtime.process(fixture(20_000, 20_000, counts(20, 2, 2), {
    offsets: [1, 2],
    state: { name: "Pause", isPlaying: false, isPaused: true },
  }));
  assert.equal(paused.state, "Paused");
  assert.notEqual(paused.sessionId, "");
  assert.equal(paused.performance.wholeMisses, 2);
}

// Sparse and dense publication rates use the same gameplay-time counters.
{
  const sparse = runWindow([0, 10_000, 20_000, 30_000, 40_000]);
  const dense = runWindow([0, 5_000, 10_000, 15_000, 20_000, 25_000, 30_000, 35_000, 40_000]);
  assert.equal(sparse.performance.recentMisses, dense.performance.recentMisses);
  assert.equal(sparse.performance.recentHits, dense.performance.recentHits);
}

// Wall-clock pauses do not age out map-time telemetry.
{
  const runtime = createRuntime({ minimumTimingSamples: 1 });
  runtime.process(fixture(0, 0, counts(0, 0, 1), { offsets: [1] }));
  const firstPause = runtime.process(fixture(20_000, 20_000, counts(20, 1, 2), { offsets: [1, 2], state: { name: "Pause", isPaused: true } }));
  const laterPause = runtime.process(fixture(20_000, 50_000, counts(20, 1, 2), { offsets: [1, 2], state: { name: "Pause", isPaused: true } }));
  assert.equal(laterPause.performance.recentMisses, firstPause.performance.recentMisses);
  assert.equal(laterPause.timing.sampleCount, firstPause.timing.sampleCount);
  const resumed = runtime.process(fixture(21_000, 51_000, counts(21, 1, 3), { offsets: [1, 2, 3] }));
  assert.equal(resumed.sessionId, firstPause.sessionId);
}

// Identity can be missing from a partial packet without resetting the attempt.
{
  const runtime = createRuntime({ minimumTimingSamples: 1 });
  const first = runtime.process(fixture(0, 0, counts(1, 0, 1), { offsets: [1] }));
  const partial = runtime.process(fixture(1_000, 1_000, counts(2, 0, 2), { identity: false, offsets: [1, 2], state: { name: "Pause", isPaused: true } }));
  assert.equal(partial.sessionId, first.sessionId);
}

// Retry, results and unsupported modes retain their explicit lifecycle rules.
{
  const runtime = createRuntime({ minimumTimingSamples: 1 });
  const first = runtime.process(fixture(10_000, 10_000, counts(20, 0, 1), { offsets: [1] }));
  const retry = runtime.process(fixture(1_000, 11_000, counts(1, 0, 1), { offsets: [2] }));
  assert.notEqual(retry.sessionId, first.sessionId);
  const results = runtime.process(fixture(2_000, 12_000, counts(2, 0, 1), { offsets: [2], state: { name: "Results" } }));
  assert.equal(results.state, "Ready");
  const finalRuntime = createRuntime({ minimumTimingSamples: 1 });
  finalRuntime.process(fixture(0, 0, counts(0, 0, 1), { offsets: [0] }));
  finalRuntime.process(fixture(40_000, 40_000, counts(40, 4, 2), { offsets: [0, 1], state: { name: "Pause", isPaused: true } }));
  const finalResults = finalRuntime.process(fixture(40_000, 41_000, counts(40, 4, 2), { offsets: [0, 1], state: { name: "Results" } }));
  assert.ok(finalResults.insights.length > 0);
  const replay = createRuntime().process(fixture(1_000, 1_000, counts(1, 0, 0), { state: { name: "Watching Replay", isReplay: true } }));
  assert.equal(replay.state, "Unavailable");
}

// Regression guards for reinjection/standalone rendering and responsive CSS.
{
  const adapter = fs.readFileSync(path.join(root, "assets/analyzers/mania-map-analyser/adapter.js"), "utf8");
  const renderer = fs.readFileSync(path.join(root, "assets/overlay/runtime/renderer.js"), "utf8");
  assert.match(adapter, /__overlayPauseCoachRuntime/);
  assert.match(adapter, /function booleanValue/);
  assert.match(adapter, /stateToken === "pause"/);
  assert.match(adapter, /httpPauseState/);
  assert.match(adapter, /function mergePlayPayload/);
  assert.match(adapter, /applyTosuPayload\(await response\.json\(\), "browser-http"\)/);
  assert.doesNotMatch(adapter, /stateOnly:\s*true/);
  assert.match(adapter, /overlay:pause-coach-debug/);
  assert.match(adapter, /__overlayAnalyzerAdapterTest/);
  assert.doesNotMatch(adapter, /pauseCoachRuntime\.dispose\(\)/);
  assert.match(renderer, /overlay-layout-companella-replay/);
  assert.match(renderer, /nativePauseCoach/);
  assert.match(renderer, /formatAccuracyPercentage/);
  assert.match(renderer, /mergeDifficulty/);
  assert.match(renderer, /displaying `0 SR`/);
  assert.match(renderer, /__overlayHostQueueSizeReport/);
  assert.match(renderer, /overlay:pause-coach-render-debug/);
  for (const preset of ["pause-coach-card"]) {
    const css = fs.readFileSync(path.join(root, "assets/overlay/presets", preset, "style.css"), "utf8");
    const manifest = JSON.parse(fs.readFileSync(path.join(root, "assets/overlay/presets", preset, "manifest.json"), "utf8"));
    assert.match(css, /max-width:var\(--overlay-host-width/);
    assert.match(css, /min-width:0/);
    assert.ok(manifest.minWidth > 0 && manifest.minHeight > 0);
  }
}

console.log("pause-coach-runtime.test.js: all assertions passed");
