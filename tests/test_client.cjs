const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict'), path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../client.js'), 'utf8');
async function fixture({ mute = false, test = false, blocked = false, staleQueue = false } = {}) {
  let now = Date.now();
  let state = { session: 'one', running: true, updated_at: new Date(now).toISOString(), volume: .5, duration_ms: 1800,
    sounds: ['Sounds/one.mp3'], events: [{ id: 1, type: 'kill', at: new Date(now).toISOString() }],
    kills: 1, deaths: 0, unknown: 0, game_pid: 42, game_readable: true, sdk_readable: true,
    game_log: 'game/USER/Game.log', sdk_log: '%LOCALAPPDATA%/NVIDIA Corporation/GfeSDK/HuntGame_42.log', image_found: true };
  const animations = [], sounds = [], intervals = [], commands = [], timers = new Map(), nodes = new Map();
  let timerId = 0, pickerCalls = 0, playAttempts = 0;
  const element = id => {
    if (!nodes.has(id)) nodes.set(id, { style: {}, checked: true, textContent: '', classList: { toggle() {} } });
    return nodes.get(id);
  };
  element('skull').animate = (frames, options) => {
    const animation = { frames, options, cancel() { this.cancelled = true; } };
    animations.push(animation); return animation;
  };
  const runtime = { async getFileHandle() { return { async getFile() { return { async text() { return JSON.stringify(state); } }; } }; },
    async getDirectoryHandle() { return { async getFileHandle() { return { async createWritable() { return { async write(text) { commands.push(JSON.parse(text)); }, async close() {} }; } }; } }; } };
  const context = {
    document: { body: { dataset: { page: test ? 'test' : 'overlay' } }, getElementById: element,
      createElement: () => ({ remove() {} }), head: { appendChild(script) {
        assert.match(script.src, /^_runtime\/state.js\?v=/);
        context.window.HuntKillState = structuredClone(state);
        queueMicrotask(() => script.onload?.());
      } } },
    location: { protocol: 'file:', search: mute ? '?mute=1' : '' },
    window: { async showDirectoryPicker() { pickerCalls++; return { async getDirectoryHandle() { return runtime; } }; } },
    URLSearchParams, Date: class extends Date { static now() { return now; } }, crypto: { randomUUID: () => 'command-' + (commands.length + 1) },
    Audio: class { constructor(file) { this.file = file; sounds.push(this); } play() { playAttempts++; return blocked && playAttempts === 1 ? Promise.reject({ name: 'NotAllowedError' }) : Promise.resolve(); } pause() { this.paused = true; } },
    setInterval: cb => intervals.push(cb), setTimeout: cb => { timers.set(++timerId, cb); return timerId; }, clearTimeout: id => timers.delete(id)
  };
  const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
  vm.runInNewContext(source, context); await flush();
  const poll = async () => { intervals[0](); await flush(); };
  const image = element('skull');
  assert.equal(sounds.length, 0, 'historical baseline is silent');
  state.events.push({ id: 2, type: 'death', at: new Date(now).toISOString() });
  await poll(); assert.equal(sounds.length, 0, 'death ignored');
  state.events.push({ id: 3, type: 'kill', at: new Date(now).toISOString(), sound: 'Sounds/chosen.mp3' });
  await poll(); assert.match(image.src, /^Images\/kill.png\?v=/);
  if (blocked) {
    assert.equal(element('audio-unlock').hidden, false, 'browser autoplay block has a recovery button');
    element('audio-unlock').onclick(); await flush();
    assert.equal(playAttempts, 2); assert.equal(element('audio-unlock').hidden, true);
  }
  image.onload(); assert.equal(animations.length, 1); assert.equal(sounds.length, mute ? 0 : 1);
  assert.deepEqual(JSON.parse(JSON.stringify(animations[0].frames)), [
    { opacity: 1, transform: 'scale(1.04)' },
    { opacity: 1, transform: 'scale(1)', offset: .7 },
    { opacity: 0, transform: 'scale(.94)' }
  ], 'preserve the Standalone skull animation');
  assert.equal(animations[0].options.duration, 1800);
  assert.equal(animations[0].options.easing, 'ease-out');
  if (!mute) assert.equal(sounds[0].volume, .5);
  if (!mute) assert.match(sounds[0].file, /^Sounds\/chosen.mp3\?v=/, 'all views use the event sound');
  await poll(); assert.equal(animations.length, 1, 'poll dedup');
  state.events.push({ id: 4, type: 'kill', at: new Date(now).toISOString() });
  await poll(); assert.equal(animations.length, 1, 'burst queued');
  if (staleQueue) now += 11000;
  animations[0].onfinish();
  if (!mute) sounds[0].onended();
  if (staleQueue) {
    assert.equal(animations.length, 1); assert.equal(sounds.length, 1, 'expired queued alerts are discarded');
    return;
  }
  image.onload(); assert.equal(animations.length, 2);
  if (!mute) sounds[1].onerror();
  animations[1].onfinish();
  state.events.push({ id: 5, type: 'kill', at: new Date(now - 20000).toISOString() });
  await poll(); assert.equal(animations.length, 2, 'stale ignored');
  state.session = 'two'; state.events = [{ id: 1, type: 'kill', at: new Date(now).toISOString() }];
  await poll(); assert.equal(animations.length, 2, 'restart baseline');
  state.sounds = []; state.events.push({ id: 2, type: 'kill', at: new Date(now).toISOString() });
  await poll(); image.onerror(); assert.equal(sounds.length, mute ? 0 : 2);
  state.display = { alerts: false };
  state.events.push({ id: 3, type: 'kill', at: new Date(now).toISOString() });
  await poll(); assert.equal(animations.length, 2, 'disabled alerts stay silent');
  state.display.alerts = true;
  await poll(); assert.equal(animations.length, 2, 'enabling alerts does not replay hidden events');
  state.running = false; state.events.push({ id: 3, type: 'kill', at: new Date(now).toISOString() });
  await poll(); assert.equal(animations.length, 2, 'stop prevents alerts');
  state.running = true; state.events = []; state.sounds = [];
  state.alert_mode = 'kills-deaths';
  state.events.push({ id: 10, type: 'death', at: new Date(now).toISOString() });
  await poll(); image.onload();
  assert.equal(image.className, 'death'); assert.equal(element('event-label').textContent, 'Смерть');
  animations.at(-1).onfinish(); assert.equal(element('event-label').textContent, '');
  const count = animations.length;
  state.events.push({ id: 11, type: 'trade', at: new Date(now).toISOString() });
  await poll(); assert.equal(animations.length, count, 'trade hidden outside trade mode');
  state.alert_mode = 'trades';
  await poll(); assert.equal(animations.length, count, 'changing mode does not replay events');
  state.events.push({ id: 12, type: 'trade', at: new Date(now).toISOString() });
  await poll(); image.onload();
  assert.equal(image.className, 'trade'); assert.equal(element('event-label').textContent, 'Размен');
  state.events.push({ id: 13, type: 'death', at: new Date(now).toISOString() });
  await poll();
  state.alert_mode = 'kills'; await poll();
  assert(animations.at(-1).cancelled, 'changing mode cancels active alert and queue');
  assert.equal(element('event-label').textContent, '');
  state.running = false; await poll();
  assert.equal(element('event-label').textContent, '', 'stopped alert label hidden');

}
(async () => {
  await fixture(); await fixture({ mute: true }); await fixture({ blocked: true }); await fixture({ staleQueue: true });
  console.log('PASS: local script polling, three alert modes, mode changes, baseline, dedup, queue, MP3 paths, mute, stale events, stop.');
})().catch(e => { console.error(e); process.exitCode = 1; });
