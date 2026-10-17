const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const assert = require('node:assert/strict');

const nodes = new Map(), intervals = [];
const createNode = () => ({
  hidden: false, textContent: '', children: [], dataset: {},
  style: { setProperty(key, value) { this[key] = value; } },
  append(...items) { this.children.push(...items); }, remove() {}
});
const element = id => { if (!nodes.has(id)) nodes.set(id, createNode()); return nodes.get(id); };
const fields = ['kills', 'deaths', 'kd', 'games', 'survival', 'maps', 'current', 'unknown'].map(field => Object.assign(createNode(), { dataset: { field } }));
let now = Date.now();
const state = {
  running: true, updated_at: new Date(now).toISOString(), game_pid: 42, game_readable: true, sdk_readable: true,
  display: { stats: true, alerts: false, fields: fields.map(n => n.dataset.field), scale: 100, opacity: 90 },
  statistics: { kills: 8, deaths: 2, games: 4, unknown: 1, survived: 2, lost: 1, unresolved: 1,
    maps: { cemetery: 1, civilwar: 1, creek: 1, colorado: 1 }, current: { map: 'creek', status: 'active', kills: 2, deaths: 0 } }
};
const context = {
  Date: class extends Date { static now() { return now; } }, window: { HuntKillState: state },
  document: { getElementById: element, querySelectorAll: () => fields, createElement: createNode,
    head: { append(script) { assert.match(script.src, /^_runtime\/state.js\?v=/); queueMicrotask(() => script.onload?.()); } } },
  setInterval: cb => intervals.push(cb), setTimeout: () => 1, clearTimeout() {}
};
const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
(async () => {
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../statistics.js'), 'utf8'), context);
  await flush();
  assert.equal(element('statistics').hidden, false);
  assert.equal(element('kd').textContent, '4.00');
  assert.equal(element('survival').textContent, '67%');
  assert.match(element('outcomes').textContent, /3.*1/);
  assert.match(element('current').textContent, /DeSalle/);
  assert.equal(element('maps').children[0].children[2].textContent, '25%');
  state.display.fields = ['kills']; state.display.scale = 150; state.display.opacity = 25;
  intervals[0](); await flush();
  assert.equal(fields[0].hidden, false);
  assert(fields.slice(1).every(n => n.hidden));
  assert.equal(element('statistics').style.transform, 'scale(1.5)');
  assert.equal(element('statistics').style['--panel-opacity'], .25);
  state.display.stats = false; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true);
  assert.equal(state.statistics.kills, 8, 'display changed collected statistics');
  state.display.stats = true; state.display.fields = []; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true, 'empty panel should disappear');
  state.display.fields = ['kd']; state.statistics.deaths = 0; intervals[0](); await flush();
  assert.equal(element('kd').textContent, '∞');
  state.running = false; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true, 'stopped tracker visible');
  state.running = true; now += 11000; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true, 'stale tracker visible');
  console.log('PASS: statistics values, map frequencies, chosen fields, scale, opacity, empty display, stopped and stale states.');
})().catch(error => { console.error(error); process.exitCode = 1; });
