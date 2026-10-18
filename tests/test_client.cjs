const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict'), path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../client.js'), 'utf8');
assert.doesNotMatch(fs.readFileSync(path.join(__dirname, '../Overlay.html'), 'utf8'), /event-label|alt="[^"]+"/, 'alert markup must not contain captions or fallback text');
async function fixture({ mute = false, test = false, blocked = false, staleQueue = false, versioned = false } = {}) {
  let now = Date.now();
  let state = { session: 'one', running: true, updated_at: new Date(now).toISOString(), volume: .5, duration_ms: 1800,
    events: [{ id: 1, type: 'kill', at: new Date(now).toISOString() }],
    kills: 1, deaths: 0, game_pid: 42, game_readable: true, sdk_readable: true,
    game_log: 'game/USER/Game.log', sdk_log: '%LOCALAPPDATA%/NVIDIA Corporation/GfeSDK/HuntGame_42.log' };
  let defaultSound = 'source/sounds/kill/one.mp3';
  const animations = [], sounds = [], intervals = [], commands = [], timers = new Map(), nodes = new Map();
  let timerId = 0, pickerCalls = 0, playAttempts = 0;
  const element = id => {
    assert.notEqual(id, 'event-label', 'alerts must not request a visible caption');
    if (!nodes.has(id)) nodes.set(id, { style: {}, checked: true, textContent: '', classList: { toggle() {} } });
    return nodes.get(id);
  };
  element('alert-visual').animate = (frames, options) => {
    const animation = { frames, options, cancel() { this.cancelled = true; } };
    animations.push(animation); return animation;
  };
  const runtime = { async getFileHandle() { return { async getFile() { return { async text() { return JSON.stringify(state); } }; } }; },
    async getDirectoryHandle() { return { async getFileHandle() { return { async createWritable() { return { async write(text) { commands.push(JSON.parse(text)); }, async close() {} }; } }; } }; } };
  const context = {
    document: { body: { dataset: { page: test ? 'test' : 'overlay' } }, getElementById: element,
      createElement: () => ({ remove() {} }), head: { appendChild(script) {
        assert.match(script.src, /^_runtime\/alerts.js\?v=/);
        const snapshot = structuredClone(state);
        for (const event of snapshot.events) {
          if (!Object.hasOwn(event, 'image')) event.image = `source/images/${{ kill: 'kill', death: 'death', trade: 'trade' }[event.type]}/chosen%20%231.png`;
          if (!Object.hasOwn(event, 'sound')) event.sound = defaultSound;
        }
        context.window.HuntKillState = snapshot;
        queueMicrotask(() => script.onload?.());
      } } },
    location: { protocol: 'file:', search: mute ? '?mute=1' : '' },
    window: { async showDirectoryPicker() { pickerCalls++; return { async getDirectoryHandle() { return runtime; } }; } },
    URLSearchParams, Date: class extends Date { static now() { return now; } }, crypto: { randomUUID: () => 'command-' + (commands.length + 1) },
    Audio: class { constructor(file) { this.file = file; sounds.push(this); } play() { playAttempts++; return blocked && playAttempts === 1 ? Promise.reject({ name: 'NotAllowedError' }) : Promise.resolve(); } pause() { this.paused = true; } },
    setInterval: cb => intervals.push(cb), setTimeout: cb => { timers.set(++timerId, cb); return timerId; }, clearTimeout: id => timers.delete(id), clearInterval() {}
  };
  const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
  vm.runInNewContext(source, context); await flush();
  let published = '', publishedSession = null;
  const poll = async () => {
    if (versioned) {
      const content = JSON.stringify({ ...state, revision: undefined });
      if (content !== published) {
        state.revision = publishedSession === state.session ? state.revision + 1 : 1;
        published = content; publishedSession = state.session;
      }
    }
    intervals[0](); await flush();
  };
  const image = element('skull');
  assert.equal(sounds.length, 0, 'historical baseline is silent');
  state.events.push({ id: 2, type: 'death', at: new Date(now).toISOString() });
  await poll(); assert.equal(sounds.length, 0, 'death ignored');
  state.events.push({ id: 3, type: 'kill', at: new Date(now).toISOString(), sound: 'source/sounds/kill/chosen%20%231.mp3' });
  await poll(); assert.match(image.src, /^source\/images\/kill\/chosen%20%231.png\?v=/);
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
  if (!mute) assert.match(sounds[0].file, /^source\/sounds\/kill\/chosen%20%231.mp3\?v=/, 'all views use the event sound');
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
  defaultSound = null; state.events.push({ id: 2, type: 'kill', at: new Date(now).toISOString() });
  await poll(); image.onerror(); assert.equal(sounds.length, mute ? 0 : 2);
  state.running = false; state.events.push({ id: 3, type: 'kill', at: new Date(now).toISOString() });
  await poll(); assert.equal(animations.length, 2, 'stop prevents alerts');
  state.running = true; state.events = []; defaultSound = null;
  state.alert_mode = 'kills-deaths';
  state.events.push({ id: 10, type: 'death', at: new Date(now).toISOString() });
  await poll(); image.onload();
  assert.equal(element('trade-skull').hidden, true);
  assert.equal(animations.at(-1).frames[0].transform, 'scale(1.12)');
  assert.match(image.src, /^source\/images\/death\/chosen%20%231.png\?v=/);
  assert.equal(image.className, 'death');
  animations.at(-1).onfinish();
  const count = animations.length;
  state.events.push({ id: 11, type: 'trade', at: new Date(now).toISOString() });
  await poll(); assert.equal(animations.length, count, 'trade hidden outside trade mode');
  state.alert_mode = 'trades';
  await poll(); assert.equal(animations.length, count, 'changing mode does not replay events');
  state.events.push({ id: 12, type: 'trade', at: new Date(now).toISOString() });
  await poll(); image.onload();
  assert.equal(element('trade-skull').hidden, false);
  assert.equal(element('trade-skull').src, image.src);
  element('trade-skull').onload();
  assert.equal(animations.at(-1).frames[0].transform, 'scale(.88)');
  assert.match(image.src, /^source\/images\/trade\/chosen%20%231.png\?v=/);
  assert.equal(image.className, 'trade');
  state.events.push({ id: 13, type: 'death', at: new Date(now).toISOString() });
  await poll();
  state.alert_mode = 'kills'; await poll();
  assert(animations.at(-1).cancelled, 'changing mode cancels active alert and queue');

  const beforeEmpty = animations.length;
  state.events.push({ id: 14, type: 'kill', at: new Date(now).toISOString(), image: null, sound: null });
  await poll();
  assert.equal(animations.length, beforeEmpty, 'empty folders must not reuse the previous image');

  const beforeSoundOnly = sounds.length;
  state.events.push({ id: 15, type: 'kill', at: new Date(now).toISOString(), image: null, sound: 'source/sounds/kill/only.mp3' });
  await poll();
  assert.equal(animations.length, beforeEmpty);
  assert.equal(sounds.length, beforeSoundOnly + (mute ? 0 : 1), 'missing PNG must not suppress audio');
  if (!mute) sounds.at(-1).onended();
  state.events.push({ id: 16, type: 'kill', at: new Date(now).toISOString(), image: 'source/images/kill/next.png', sound: null });
  await poll(); image.onload();
  assert.equal(animations.length, beforeEmpty + 1, 'empty media must not block the next event');
  animations.at(-1).onfinish();
  state.display = { alerts: false };
  state.events.push({ id: 17, type: 'death', test: true, at: new Date(now).toISOString(), sound: null });
  await poll(); image.onload();
  assert.equal(image.className, 'death', 'test death must bypass kill-only mode and old visibility flag');
  animations.at(-1).onfinish();
  state.events.push({ id: 18, type: 'trade', test: true, at: new Date(now).toISOString(), sound: null });
  await poll(); image.onload(); element('trade-skull').onload();
  assert.equal(image.className, 'trade', 'test trade must bypass kill-only mode');
  animations.at(-1).onfinish();
  state.volume = .5;
  state.events.push({ id: 181, type: 'kill', at: new Date(now).toISOString(), sound: 'source/sounds/kill/test.mp3' });
  await poll(); image.onload();
  const activeSound = mute ? null : sounds.at(-1);
  state.events.push({ id: 182, type: 'kill', at: new Date(now).toISOString(), sound: 'source/sounds/kill/test.mp3' });
  await poll();
  state.volume = .25; await poll();
  if (activeSound) assert.equal(activeSound.volume, .25, 'live volume did not change');
  state.volume = 0; await poll();
  if (activeSound) assert(activeSound.paused, 'muting did not stop active audio');
  const countBeforeQueued = sounds.length;
  animations.at(-1).onfinish(); image.onload(); animations.at(-1).onfinish();
  assert.equal(sounds.length, countBeforeQueued, 'queued alert retained old volume');
  const soundsBeforeMute = sounds.length;
  state.events.push({ id: 19, type: 'kill', at: new Date(now).toISOString(), sound: 'source/sounds/kill/test.mp3' });
  await poll(); image.onload(); animations.at(-1).onfinish();
  assert.equal(sounds.length, soundsBeforeMute, 'zero volume must not instantiate Audio');
  state.alerts_enabled = false;
  const disabledCount = animations.length;
  state.events.push({ id: 20, type: 'kill', at: new Date(now).toISOString() });
  await poll(); assert.equal(animations.length, disabledCount, 'disabled module played a game alert');
  state.events.push({ id: 21, type: 'death', test: true, at: new Date(now).toISOString() });
  await poll(); image.onload(); assert.equal(animations.length, disabledCount + 1, 'disabled module blocked test');
  animations.at(-1).onfinish();
  state.alerts_enabled = true; await poll();
  assert.equal(animations.length, disabledCount + 1, 'reenable replayed old game events');
  state.volume = .5;
  const safeImage = image.src, safeSounds = sounds.length;
  for (const [index, source] of ['https://invalid.example/media.png', 'source/images/kill/%2e%2e%2fsecret.png', 'source/images/kill/%ZZ.png'].entries()) {
    state.events.push({ id: 1000 + index, type: 'kill', at: new Date(now).toISOString(), image: source, sound: 'https://invalid.example/media.mp3' });
    await poll();
  }
  assert.equal(image.src, safeImage, 'untrusted media path reached the image element');
  assert.equal(sounds.length, safeSounds, 'remote audio was loaded');
  now += 11000; await poll();
  assert.equal(element('alert-visual').style.opacity, 0, 'stale alert hidden');
  state.running = false; await poll();
  assert.equal(element('alert-visual').style.opacity, 0, 'stopped alert hidden');
}
(async () => {
  await fixture(); await fixture({ versioned: true }); await fixture({ mute: true }); await fixture({ blocked: true }); await fixture({ staleQueue: true });
  console.log('PASS: local script polling, three alert modes, mode changes, baseline, dedup, queue, MP3 paths, mute, stale events, stop.');
})().catch(e => { console.error(e); process.exitCode = 1; });
