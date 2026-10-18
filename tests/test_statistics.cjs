const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const assert = require('node:assert/strict');

const markup = fs.readFileSync(path.join(__dirname, '../Statistics.html'), 'utf8');
assert.match(markup, /id="kill-icon"[^>]*src="data:image\/png;base64,/);
assert.doesNotMatch(markup, /(?:src|href)="source\//, 'statistics icons depend on removable alert media');

const nodes = new Map(), intervals = [], rates = [];
const createNode = () => ({
  hidden: false, textContent: '', children: [], dataset: {},
  style: { setProperty(key, value) { this[key] = value; } },
  append(...items) { this.children.push(...items); },
  replaceChildren(...items) { this.children = items; }, setAttribute(key, value) { this[key] = value; }, remove() {}
});
const element = id => { assert(markup.includes('id="' + id + '"'), 'Script requested an absent HTML element: ' + id); if (!nodes.has(id)) nodes.set(id, createNode()); return nodes.get(id); };
element('kill-icon').src = 'data:image/png;base64,built-in';
assert.doesNotMatch(markup, /id="(?:current|match-empty)"/, 'Last match must contain only its heading and skull row');
const fields = ['kills', 'deaths', 'kd', 'games', 'survival', 'maps', 'current'].map(field => Object.assign(createNode(), { dataset: { field } }));
let now = Date.now();
const state = {
  running: true, updated_at: new Date(now).toISOString(), game_pid: 42, game_readable: true, sdk_readable: true,
  display: { stats: true, alerts: false, fields: fields.map(n => n.dataset.field), scale: 100, opacity: 90 },
  statistics: { kills: 8, deaths: 2, games: 4, survived: 2, lost: 1, unresolved: 1,
    maps: { cemetery: 1, civilwar: 1, creek: 1, colorado: 1 }, current: { id: 'match-one', map: 'creek', status: 'active', kills: 2, deaths: 1, combat_events: ['kill', 'death', 'kill'] } }
};
const context = {
  Date: class extends Date { static now() { return now; } }, window: { HuntKillState: state },
  document: { getElementById: element, querySelectorAll: () => fields, createElement: createNode,
    head: { append(script) { assert.match(script.src, /^_runtime\/statistics.js\?v=/); queueMicrotask(() => script.onload?.()); } } },
  setInterval: (cb, ms) => { rates.push(ms); return intervals.push(cb); }, setTimeout: () => 1, clearTimeout() {}, clearInterval() {}
};
const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
(async () => {
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../statistics.js'), 'utf8'), context);
  await flush();
  assert.equal(element('statistics').hidden, false);
  assert.equal(rates.at(-1), 1000, 'statistics default must be one second');
  state.statistics_refresh_ms = 5000; intervals[0](); await flush();
  assert.equal(rates.at(-1), 5000, 'five second refresh missing');
  state.statistics_enabled = false; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true, 'disabled statistics visible');
  state.statistics_enabled = true; state.statistics_refresh_ms = 1000; intervals[0](); await flush();
  assert.equal(rates.at(-1), 1000);
  assert.equal(element('kd').textContent, '4.00');
  assert.equal(element('survival').textContent, '67%');
  assert.equal(element('survived').textContent, 2);
  assert.equal(element('lost').textContent, 1);
  assert.equal(element('maps').children[0].children[2].textContent, '25%');
  state.statistics.maps = { cemetery: 2, civilwar: 5, creek: 2, colorado: 0 };
  intervals[0](); await flush();
  assert.deepEqual(element('maps').children.map(node => node.dataset.map), ['civilwar', 'cemetery', 'creek', 'colorado'], 'maps sorted by frequency with stable ties');
  state.statistics.maps.colorado = 8; intervals[0](); await flush();
  assert.equal(element('maps').children[0].dataset.map, 'colorado', 'map order updates live');
  state.display.fields = ['kills']; state.display.scale = 150; state.display.opacity = 25;
  intervals[0](); await flush();
  assert.equal(fields[0].hidden, false);
  assert(fields.slice(1).every(n => n.hidden));
  assert.equal(element('statistics').style.transform, undefined, 'legacy scale must not change widget size');
  assert.equal(element('statistics').style['--panel-opacity'], .25);
  assert.equal(fields[0].style.gridColumn, '1 / -1');
  state.display.fields = ['kd', 'games']; intervals[0](); await flush();
  assert.equal(fields[0].hidden, true); assert.equal(fields[1].hidden, true);
  assert.equal(fields[2].hidden, false); assert.equal(fields[3].hidden, false);
  state.statistics.kills = 9; intervals[0](); await flush();
  assert.equal(element('kd').textContent, '4.50', 'hidden kills continue contributing to K/D');
  state.display.fields = ['kills', 'kd', 'games']; intervals[0](); await flush();
  assert.equal(element('kills').textContent, 9, 'restored field shows current counter');
  assert.equal(fields[0].style.gridColumn, '1 / -1', 'only visible combat counter fills the row');
  state.display.fields = ['survival']; intervals[0](); await flush();
  assert.equal(element('counters').hidden, false, 'survival counters remain visible');
  state.display.fields = ['unknown']; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true, 'removed field cannot leave an empty frame');
  state.display.fields = ['kills', 'unknown']; intervals[0](); await flush();
  assert.equal(fields[0].style.gridColumn, '1 / -1', 'removed field cannot inflate the layout');

  state.display.stats = false; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, false, 'legacy visibility flag must not hide the widget');
  assert.equal(state.statistics.kills, 9, 'display changed collected statistics');
  state.display.stats = true; state.display.fields = []; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true, 'empty panel should disappear');
  state.display.fields = ['kd']; state.statistics.deaths = 0; intervals[0](); await flush();
  assert.equal(element('kd').textContent, '∞');
  state.display.fields = ['current']; intervals[0](); await flush();
  assert.equal(element('counters').hidden, true, 'no blank space for disabled counters');
  const row = element('match-events');
  assert.deepEqual(row.children.map(item => item.children[0].className), ['event-skull kill', 'event-skull death', 'event-skull kill']);
  assert(row.children.every(item => item.children[0].src === element('kill-icon').src), 'match icons must use the built-in PNG');
  const firstIcon = row.children[0]; intervals[0](); await flush();
  assert.equal(row.children[0], firstIcon, 'unchanged history must not rebuild on every poll');
  state.statistics.current.deaths++;
  state.statistics.current.combat_events.push('death'); intervals[0](); await flush();
  assert.equal(row.children.length, 4); assert.equal(row.children[3].children[0].className, 'event-skull death');
  delete state.statistics.current.combat_events; intervals[0](); await flush();
  assert.equal(row.children.length, 4, 'older matches retain all skull counts');
  assert(row.children.every(item => item.title.includes('порядок старых событий не сохранён')));
  state.statistics.current = { id: 'large', kills: Number.MAX_SAFE_INTEGER, deaths: 1000000, combat_events: null };
  intervals[0](); await flush();
  assert.equal(row.children.length, 128, 'large counters must not allocate one DOM node per event');
  assert(row.title.includes('128'));
  state.statistics.current = { id: 'empty', map: 'cemetery', kills: 0, deaths: 0, combat_events: [] };
  intervals[0](); await flush();
  assert.equal(row.children.length, 0); assert.equal(row.hidden, true);
  state.statistics.current = null; intervals[0](); await flush();
  state.running = false; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true, 'stopped tracker visible');
  state.running = true; now += 11000; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true, 'stale tracker visible');
  state.updated_at = new Date(now).toISOString(); state.session = 'revision-test'; state.revision = 1;
  state.display.fields = ['kills']; intervals[0](); await flush();
  let writes = 0, value = element('kills').textContent;
  Object.defineProperty(element('kills'), 'textContent', { configurable: true, get: () => value, set: next => { value = next; writes++; } });
  intervals[0](); await flush();
  assert.equal(writes, 0, 'unchanged publication rewrote the DOM');
  state.statistics.kills++; state.revision++; intervals[0](); await flush();
  assert.equal(value, state.statistics.kills, 'new revision did not update counters');
  assert.equal(writes, 1, 'new revision rendered more than once');
  state.session = 'restarted'; state.revision = 1; state.statistics.kills = 0;
  intervals[0](); await flush(); assert.equal(value, 0, 'restart revision was treated as an old publication');
  now += 11000; intervals[0](); await flush();
  assert.equal(element('statistics').hidden, true, 'revision cache prevented stale hiding');
  console.log('PASS: statistics values, map frequencies, chosen fields, fixed scale, opacity, empty display, stopped and stale states.');
})().catch(error => { console.error(error); process.exitCode = 1; });
