(() => {
  'use strict';
  const message = text => {
    const node = document.getElementById('message');
    if (node) node.textContent = text;
  };
  class Player {
    constructor(image, muted) {
      this.image = image; this.muted = muted; this.queue = [];
      this.busy = false; this.cancel = null; this.animation = null;
    }
    stop() {
      this.queue = [];
      if (this.cancel) this.cancel();
      this.animation?.cancel(); this.animation = null;
      this.image.onload = this.image.onerror = null;
      this.image.style.opacity = 0;
      const label = document.getElementById('event-label');
      if (label) label.textContent = '';
      const unlock = document.getElementById('audio-unlock');
      if (unlock) { unlock.hidden = true; unlock.onclick = null; }
    }
    push(state, createdAt, event) {
      if (this.queue.length >= 16) return;
      const sounds = Object.prototype.hasOwnProperty.call(event, 'sound') ? event.sound ? [event.sound] : [] : state.sounds;
      this.queue.push({ type: event.type, sounds, duration_ms: state.duration_ms, volume: state.volume, image: state.image || 'Images/kill.png', createdAt });
      this.next();
    }
    next() {
      if (this.busy || !this.queue.length) return;
      while (this.queue.length && Date.now() - this.queue[0].createdAt > 10000) this.queue.shift();
      if (!this.queue.length) return;
      const state = this.queue.shift();
      this.image.className = state.type;
      this.image.alt = { kill: 'Убийство', death: 'Смерть', trade: 'Размен' }[state.type];
      const label = document.getElementById('event-label');
      if (label) { label.textContent = state.type === 'kill' ? '' : this.image.alt; label.className = state.type; }
      this.busy = true;
      let done = false, sound = null, visualDone = false;
      let audioDone = this.muted || !state.sounds.length;
      let imageTimer, soundTimer;
      const finish = () => {
        if (done || !visualDone || !audioDone) return;
        done = true; clearTimeout(imageTimer); clearTimeout(soundTimer);
        if (sound) { sound.pause(); sound.onended = sound.onerror = null; }
        const unlock = document.getElementById('audio-unlock');
        if (unlock) { unlock.hidden = true; unlock.onclick = null; }
        this.image.onload = this.image.onerror = null;
        this.busy = false; this.cancel = null; this.next();
      };
      this.cancel = () => { visualDone = audioDone = true; finish(); };
      const hide = () => {
        this.image.onload = this.image.onerror = null;
        if (label) label.textContent = '';
        visualDone = true; finish();
      };
      this.image.onload = () => {
        clearTimeout(imageTimer); this.animation?.cancel();
        this.animation = this.image.animate([
          { opacity: 1, transform: 'scale(1.04)' },
          { opacity: 1, transform: 'scale(1)', offset: .7 },
          { opacity: 0, transform: 'scale(.94)' }
        ], { duration: state.duration_ms, easing: 'ease-out' });
        this.animation.onfinish = hide;
      };
      this.image.onerror = hide;
      imageTimer = setTimeout(hide, 5000);
      this.image.src = state.image.split('/').map(encodeURIComponent).join('/') + '?v=' + Date.now();
      if (!audioDone) {
        const file = state.sounds[Math.floor(Math.random() * state.sounds.length)];
        sound = new Audio(file + '?v=' + Date.now());
        sound.volume = state.volume;
        const audioEnd = () => { sound.pause(); audioDone = true; finish(); };
        sound.onended = sound.onerror = audioEnd;
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
  let lastGood = 0;
  function consume(state) {
    const age = Date.now() - Date.parse(state.updated_at);
    const active = state.running && state.display?.alerts !== false && Number.isFinite(age) && age >= -2000 && age < 10000;
    const baseline = session !== state.session;
    if (baseline) { session = state.session; seen.clear(); player?.stop(); }
    const mode = state.alert_mode || 'kills';
    if (mode !== alertMode) { player.stop(); alertMode = mode; }
    message('');
    if (!active) player.stop();
    for (const event of state.events) {
      if (seen.has(event.id)) continue;
      seen.add(event.id);
      const eventAge = Date.now() - Date.parse(event.at);
      const allowed = event.type === 'kill' || (event.type === 'death' && ['kills-deaths', 'trades'].includes(mode)) || (event.type === 'trade' && mode === 'trades');
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
    script.src = '_runtime/state.js?v=' + Date.now();
    script.onload = () => {
      if (finished) return;
      try { if (window.HuntKillState) { consume(window.HuntKillState); lastGood = Date.now(); } }
      finally { finish(); }
    };
    script.onerror = finish;
    document.head.appendChild(script);
  }
  poll();
  setInterval(() => {
    if (Date.now() - lastGood > 10000) {
      player?.stop();
    }
    poll();
  }, 50);
})();
