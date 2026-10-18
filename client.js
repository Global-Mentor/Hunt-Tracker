(() => {
  'use strict';
  const bounded = (value, minimum, maximum, fallback) => Number.isFinite(value) ? Math.min(maximum, Math.max(minimum, value)) : fallback;
  function localMedia(value, category) {
    if (typeof value !== 'string' || value.length > 2048) return null;
    const parts = value.split('/');
    if (parts.length !== 4 || parts[0] !== 'source' || parts[1] !== category || !['kill', 'death', 'trade'].includes(parts[2])) return null;
    try {
      const name = decodeURIComponent(parts[3]);
      return name && name !== '.' && name !== '..' && !/[\\/:*?"<>|\u0000-\u001f]/.test(name) && !/[?#]/.test(parts[3]) ? value : null;
    } catch { return null; }
  }
  class Player {
    constructor(image, muted) {
      this.image = image; this.visual = document.getElementById('alert-visual'); this.second = document.getElementById('trade-skull'); this.muted = muted; this.queue = [];
      this.busy = false; this.cancel = null; this.animation = null; this.changeVolume = null;
    }
    stop() {
      this.queue = [];
      if (this.cancel) this.cancel();
      this.animation?.cancel(); this.animation = null;
      this.image.onload = this.image.onerror = null;
      this.visual.style.opacity = 0;
      this.second.hidden = true; this.second.onload = this.second.onerror = null;
      const unlock = document.getElementById('audio-unlock');
      if (unlock) { unlock.hidden = true; unlock.onclick = null; }
    }
    setVolume(value) {
      value = bounded(value, 0, 1, 0);
      for (const queued of this.queue) queued.volume = value;
      this.changeVolume?.(value);
    }
    push(state, createdAt, event) {
      if (this.queue.length >= 16 || !['kill', 'death', 'trade'].includes(event.type)) return;
      this.queue.push({ type: event.type, sound: localMedia(event.sound, 'sounds'), image: localMedia(event.image, 'images'), duration_ms: bounded(state.duration_ms, 300, 10000, 1800), volume: bounded(state.volume, 0, 1, 0), createdAt });
      this.next();
    }
    next() {
      if (this.busy || !this.queue.length) return;
      while (this.queue.length && Date.now() - this.queue[0].createdAt > 10000) this.queue.shift();
      if (!this.queue.length) return;
      const state = this.queue.shift();
      this.visual.className = state.type;
      this.image.className = state.type;
      this.second.hidden = state.type !== 'trade';
      this.visual.ariaLabel = { kill: 'Убийство', death: 'Смерть', trade: 'Размен' }[state.type];
      this.busy = true;
      let done = false, sound = null, visualDone = false;
      let audioDone = this.muted || !state.sound || Number(state.volume) === 0;
      let imageTimer, soundTimer;
      const finish = () => {
        if (done || !visualDone || !audioDone) return;
        done = true; clearTimeout(imageTimer); clearTimeout(soundTimer);
        if (sound) { sound.pause(); sound.onended = sound.onerror = null; }
        const unlock = document.getElementById('audio-unlock');
        if (unlock) { unlock.hidden = true; unlock.onclick = null; }
        this.image.onload = this.image.onerror = null;
        this.busy = false; this.cancel = null; this.changeVolume = null; this.next();
      };
      this.cancel = () => { visualDone = audioDone = true; finish(); };
      const hide = () => {
        this.image.onload = this.image.onerror = this.second.onload = this.second.onerror = null;
        this.visual.style.opacity = 0;
        visualDone = true; finish();
      };
      let remaining = state.type === 'trade' ? 2 : 1;
      const loaded = () => {
        if (done || visualDone || --remaining > 0) return;
        clearTimeout(imageTimer); this.animation?.cancel();
        this.animation = this.visual.animate([
          { opacity: 1, transform: state.type === 'trade' ? 'scale(.88)' : state.type === 'death' ? 'scale(1.12)' : 'scale(1.04)' },
          { opacity: 1, transform: 'scale(1)', offset: .7 },
          { opacity: 0, transform: 'scale(.94)' }
        ], { duration: state.duration_ms, easing: 'ease-out' });
        this.animation.onfinish = hide;
      };
      if (state.image) {
        this.image.onload = loaded; this.image.onerror = hide;
        if (state.type === 'trade') { this.second.onload = loaded; this.second.onerror = hide; }
        imageTimer = setTimeout(hide, 5000);
        this.image.src = state.image + '?v=' + Date.now();
        if (state.type === 'trade') this.second.src = this.image.src;
      } else {
        hide();
      }
      if (!audioDone) {
        sound = new Audio(state.sound + '?v=' + Date.now());
        sound.volume = state.volume;
        const audioEnd = () => { sound.pause(); audioDone = true; finish(); };
        sound.onended = sound.onerror = audioEnd;
        this.changeVolume = value => {
          sound.volume = value;
          if (Number(value) === 0) audioEnd();
        };
        soundTimer = setTimeout(audioEnd, 60000);
        const attempt = () => sound.play().catch(error => {
          const unlock = document.getElementById('audio-unlock');
          if (error.name === 'NotAllowedError' && unlock && !done) {
            unlock.hidden = false;
            unlock.onclick = () => { unlock.hidden = true; attempt(); };
          } else audioEnd();
        });
        attempt();
      }
    }
  }
  const player = new Player(document.getElementById('skull'), new URLSearchParams(location.search).has('mute'));
  let session = null, seen = new Set(), loading = false, alertMode = 'kills';
  let lastGood = 0, lastRevision = null, alertsEnabled = true, refreshTimer = null;
  function consume(state) {
    const age = Date.now() - Date.parse(state.updated_at);
    const active = state.running && Number.isFinite(age) && age >= -2000 && age < 10000;
    const baseline = session !== state.session;
    if (baseline) { session = state.session; seen.clear(); player?.stop(); }
    const mode = state.alert_mode || 'kills';
    if (mode !== alertMode) { player.stop(); alertMode = mode; }
    const enabled = state.alerts_enabled !== false;
    if (enabled !== alertsEnabled) { player.stop(); alertsEnabled = enabled; clearInterval(refreshTimer); refreshTimer = setInterval(tick, enabled ? 50 : 1000); }
    player.setVolume(state.volume);
    if (!active) player.stop();
    if (!baseline && Number.isSafeInteger(state.revision) && state.revision === lastRevision) return;
    lastRevision = state.revision;
    for (const event of (Array.isArray(state.events) ? state.events.slice(-64) : [])) {
      if (!event || !Number.isSafeInteger(event.id)) continue;
      if (seen.has(event.id)) continue;
      seen.add(event.id);
      const eventAge = Date.now() - Date.parse(event.at);
      const allowed = event.test === true || (alertsEnabled && (event.type === 'kill' || (event.type === 'death' && ['kills-deaths', 'trades'].includes(mode)) || (event.type === 'trade' && mode === 'trades')));
      if (!baseline && active && allowed && eventAge >= -2000 && eventAge <= 10000) player.push(state, Date.parse(event.at), event);
    }
    if (seen.size > 128) seen = new Set([...seen].slice(-64));
  }
  function poll() {
    if (loading) return;
    loading = true;
    const script = document.createElement('script');
    let finished = false;
    const finish = () => {
      if (finished) return;
      finished = true; clearTimeout(timer); loading = false;
      script.onload = script.onerror = null; script.remove();
    };
    const timer = setTimeout(finish, 2000);
    script.src = '_runtime/alerts.js?v=' + Date.now();
    script.onload = () => {
      if (finished) return;
      try { if (window.HuntKillState) { consume(window.HuntKillState); lastGood = Date.now(); } }
      finally { finish(); }
    };
    script.onerror = finish;
    document.head.appendChild(script);
  }
  function tick() {
    if (Date.now() - lastGood > 10000) {
      player?.stop();
    }
    poll();
  }
  refreshTimer = setInterval(tick, 50);
  poll();
})();
