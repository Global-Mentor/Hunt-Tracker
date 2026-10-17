(() => {
  'use strict';
  const element = id => document.getElementById(id);
  const panel = element('statistics');
  const mapNames = { cemetery: 'Stillwater', civilwar: 'Lawson', creek: 'DeSalle', colorado: 'Mammon' };
  const fields = [...document.querySelectorAll('[data-field]')];
  const number = value => Number.isFinite(value) ? Math.max(0, value) : 0;
  const mapRows = Object.entries(mapNames).map(([key, name]) => {
    const row = document.createElement('div'); row.className = 'map-row';
    const label = document.createElement('span'); label.textContent = name;
    const track = document.createElement('div'); track.className = 'track';
    const fill = document.createElement('div'); fill.className = 'fill'; track.append(fill);
    const percent = document.createElement('span'); percent.className = 'percent';
    row.append(label, track, percent); element('maps').append(row);
    return { key, fill, percent };
  });
  let lastState = null, loading = false;
  function render(state) {
    lastState = state;
    const age = Date.now() - Date.parse(state.updated_at);
    const active = state.running && Number.isFinite(age) && age >= -2000 && age < 10000;
    const stats = state.statistics, display = state.display;
    panel.hidden = !active || !stats || !display?.stats || !display.fields.length;
    if (panel.hidden) return;
    panel.style.transform = `scale(${display.scale / 100})`;
    panel.style.setProperty('--panel-opacity', display.opacity / 100);
    fields.forEach(node => { node.hidden = !display.fields.includes(node.dataset.field); });
    element('status').textContent = state.game_pid ? (state.game_readable && state.sdk_readable ? 'Сбор идет' : 'Нет части логов') : 'Ожидание игры';
    for (const key of ['kills', 'deaths', 'games', 'unknown']) element(key).textContent = number(stats[key]);
    const kills = number(stats.kills), deaths = number(stats.deaths);
    element('kd').textContent = deaths ? (kills / deaths).toFixed(2) : kills ? '∞' : '0.00';
    const known = number(stats.survived) + number(stats.lost);
    element('survival').textContent = known ? Math.round(100 * stats.survived / known) + '%' : 'Нет данных';
    element('outcomes').textContent = `С оценкой: ${known} · Без результата: ${number(stats.unresolved)}`;
    const total = mapRows.reduce((sum, row) => sum + number(stats.maps[row.key]), 0);
    for (const row of mapRows) {
      const value = total ? Math.round(100 * number(stats.maps[row.key]) / total) : 0;
      row.fill.style.width = value + '%'; row.percent.textContent = total ? value + '%' : '?';
    }
    const match = stats.current;
    const status = { loading: 'Загрузка', active: 'В игре', complete: 'Завершен' };
    element('current').textContent = match ? `${mapNames[match.map] || match.map || 'Карта неизвестна'} · ${status[match.status] || ''}\nУбийства: ${match.kills} · Смерти: ${match.deaths}` : 'Пока нет матчей';
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
    script.src = '_runtime/state.js?v=' + Date.now();
    script.onload = () => { if (finished) return; try { if (window.HuntKillState) render(window.HuntKillState); } finally { finish(); } };
    script.onerror = finish;
    document.head.append(script);
  }
  poll();
  setInterval(() => { if (lastState) render(lastState); poll(); }, 250);
})();
