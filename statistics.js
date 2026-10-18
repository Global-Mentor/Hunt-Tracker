(() => {
  'use strict';
  const element = id => document.getElementById(id);
  const panel = element('statistics');
  const mapNames = { cemetery: 'Stillwater', civilwar: 'Lawson', creek: 'DeSalle', colorado: 'Mammon' };
  const fields = [...document.querySelectorAll('[data-field]')];
  const number = value => Number.isFinite(value) ? Math.max(0, value) : 0;
  const mapRows = Object.entries(mapNames).map(([key, name]) => {
    const row = document.createElement('div'); row.className = 'map-row'; row.dataset.map = key;
    const label = document.createElement('span'); label.textContent = name;
    const track = document.createElement('div'); track.className = 'track';
    const fill = document.createElement('div'); fill.className = 'fill'; track.append(fill);
    const percent = document.createElement('span'); percent.className = 'percent';
    row.append(label, track, percent); element('maps').append(row);
    return { key, row, fill, percent };
  });
  let lastState = null, loading = false, matchSignature = null, mapOrder = null;
  let lastRevision = null, renderedSession = null, refreshMs = 1000, refreshTimer = null;
  function renderMatch(match) {
    const signature = JSON.stringify(match && [match.id, match.map, match.kills, match.deaths, match.combat_events]);
    if (signature === matchSignature) return;
    matchSignature = signature;
    const row = element('match-events');
    row.replaceChildren();
    const limit = 128;
    const events = (Array.isArray(match?.combat_events) ? match.combat_events.slice(-limit) : []).filter(kind => kind === 'kill' || kind === 'death');
    const missingKills = Math.max(0, Math.floor(number(match?.kills)) - events.filter(kind => kind === 'kill').length);
    const missingDeaths = Math.max(0, Math.floor(number(match?.deaths)) - events.filter(kind => kind === 'death').length);
    const remaining = limit - events.length;
    const older = [...Array(Math.min(remaining, missingKills)).fill('kill'), ...Array(Math.min(Math.max(0, remaining - missingKills), missingDeaths)).fill('death')];
    const sequence = [...older, ...events];
    row.title = number(match?.kills) + number(match?.deaths) > limit ? 'Показано не более ' + limit + ' событий. Общие счётчики сохранены.' : '';
    sequence.forEach((kind, index) => {
      const item = document.createElement('span'); item.setAttribute('role', 'listitem');
      const icon = document.createElement('img');
      icon.src = element('kill-icon').src; icon.className = 'event-skull ' + kind;
      icon.alt = kind === 'kill' ? 'Убийство' : 'Смерть';
      item.title = index < older.length ? icon.alt + ': порядок старых событий не сохранён' : icon.alt;
      item.append(icon); row.append(item);
    });
    row.hidden = !sequence.length;
  }
  function render(state) {
    lastState = state;
    const interval = state.statistics_enabled === false || state.statistics_refresh_ms === 5000 ? 5000 : 1000;
    if (refreshTimer !== null && interval !== refreshMs) { clearInterval(refreshTimer); refreshMs = interval; refreshTimer = setInterval(tick, refreshMs); }
    const age = Date.now() - Date.parse(state.updated_at);
    const active = state.running && Number.isFinite(age) && age >= -2000 && age < 10000;
    const stats = state.statistics, display = state.display;
    const selected = fields.filter(node => display?.fields?.includes(node.dataset.field));
    panel.hidden = state.statistics_enabled === false || !active || !stats || !selected.length;
    if (panel.hidden) return;
    if (Number.isSafeInteger(state.revision) && state.revision === lastRevision && state.session === renderedSession) return;
    lastRevision = state.revision; renderedSession = state.session;
    panel.style.setProperty('--panel-opacity', display.opacity / 100);
    fields.forEach(node => { node.hidden = !display.fields.includes(node.dataset.field); });
    const counters = fields.filter(node => ['kills', 'deaths', 'kd', 'games', 'survival'].includes(node.dataset.field));
    element('counters').hidden = counters.every(node => node.hidden);
    const combat = counters.filter(node => ['kills', 'deaths'].includes(node.dataset.field));
    const combatCount = combat.filter(node => !node.hidden).length;
    for (const node of combat) node.style.gridColumn = combatCount === 1 ? '1 / -1' : '';
    const statusText = state.game_pid ? (state.game_readable && state.sdk_readable ? 'Сбор идёт' : 'Нет части логов') : 'Ожидание игры';
    element('status').textContent = state.game_pid && state.game_readable && state.sdk_readable ? '●' : '○';
    element('status').title = statusText;
    panel.title = statusText + '. Статистика по логам, возможны ошибки.';
    for (const key of ['kills', 'deaths', 'games']) element(key).textContent = number(stats[key]);
    const kills = number(stats.kills), deaths = number(stats.deaths);
    element('kd').textContent = deaths ? (kills / deaths).toFixed(2) : kills ? '∞' : '0.00';
    const known = number(stats.survived) + number(stats.lost);
    element('survival').textContent = known ? Math.round(100 * stats.survived / known) + '%' : 'Нет данных';
    element('survived').textContent = number(stats.survived);
    element('lost').textContent = number(stats.lost);
    if (display.fields.includes('maps')) {
      const total = mapRows.reduce((sum, row) => sum + number(stats.maps[row.key]), 0);
      const sortedMaps = [...mapRows].sort((a, b) => number(stats.maps[b.key]) - number(stats.maps[a.key]));
      const order = sortedMaps.map(item => item.key).join(',');
      if (order !== mapOrder) {
        element('maps').replaceChildren(...sortedMaps.map(item => item.row));
        mapOrder = order;
      }
      for (const row of mapRows) {
        const value = total ? Math.round(100 * number(stats.maps[row.key]) / total) : 0;
        row.fill.style.width = value + '%'; row.percent.textContent = total ? value + '%' : '?';
      }
    }
    if (display.fields.includes('current')) renderMatch(stats.current);
  }
  function poll() {
    if (loading) return;
    loading = true;
    const script = document.createElement('script');
    let finished = false;
    const finish = () => {
      if (finished) return;
      finished = true; clearTimeout(timer); script.onload = script.onerror = null; script.remove(); loading = false;
    };
    const timer = setTimeout(finish, 2000);
    script.src = '_runtime/statistics.js?v=' + Date.now();
    script.onload = () => { if (finished) return; try { if (window.HuntKillState) render(window.HuntKillState); } finally { finish(); } };
    script.onerror = finish;
    document.head.append(script);
  }
  function tick() { if (lastState) render(lastState); poll(); }
  refreshTimer = setInterval(tick, refreshMs);
  poll();
})();
