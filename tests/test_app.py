import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT


def wait(check, description, timeout=8):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        result = check()
        if result:
            return result
        time.sleep(.02)
    raise AssertionError("Timed out: " + description)


def run():
    if not os.environ.get('HUNT_TEST_ROOT'):
        raise RuntimeError('Run tests through scripts/Test.ps1.')
    folder = Path(tempfile.mkdtemp(prefix="tracker-", dir=os.environ['HUNT_TEST_ROOT']))
    app = folder / "app"
    app.mkdir()
    shutil.copytree(SOURCE / 'source', app / 'source')
    binaries = Path(os.environ.get('HUNT_BUILD_DIR', SOURCE / 'src/hunt-tracker-desktop/bin/Release/net10.0-windows'))
    shutil.copytree(binaries, app / 'app')
    shutil.copyfile(SOURCE / 'config.example.json', app / 'config.json')
    (app / 'source' / 'sounds' / 'kill' / 'fixture.mp3').write_bytes(b'fixture audio')
    game_folder = folder / "game"
    (game_folder / "USER").mkdir(parents=True)
    sdk_folder = folder / "sdk"
    sdk_folder.mkdir()
    game_log = game_folder / "USER" / "Game.log"
    game_log.write_text("Starting UI state machine 'death/StateMachine'\n", encoding="utf-8")
    fixture_exe = folder / "HuntCombatFixture.exe"
    build = folder / "fixture.ps1"
    build.write_text("Add-Type -TypeDefinition 'public static class Fixture { public static void Main() { System.Threading.Thread.Sleep(180000); } }' -OutputAssembly '" + str(fixture_exe).replace("'", "''") + "' -OutputType WindowsApplication", encoding="utf-8-sig")
    subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(build)], check=True, capture_output=True)
    config = json.loads((app / "config.json").read_text())
    config.update(gameFolder=str(game_folder), nvidiaLogs=str(sdk_folder), detectTrades=False)
    config['display']['alerts'] = False
    config['display']['stats'] = False
    (app / "config.json").write_text(json.dumps(config), encoding="utf-8")
    fixture = tracker = duplicate = None
    last_state = None

    def state():
        nonlocal last_state
        try:
            last_state = json.loads((app / '_runtime' / 'state.json').read_text(encoding='utf-8'))
            return last_state
        except (OSError, ValueError):
            return last_state

    def suppressed():
        records = [json.loads(line) for log in (app / 'logs').glob('*.jsonl')
                   for line in log.read_text(encoding='utf-8').splitlines() if line.endswith('}')]
        return sum(record['type'] == 'unknown' for record in records)

    def post(path, session, command_id=None):
        command = {'id': command_id or str(uuid.uuid4()), 'session': session, 'type': path.rsplit('/', 1)[-1]}
        if path == '/api/stop':
            target = app / '_runtime' / 'stop.json'
        else:
            target = app / '_runtime' / 'commands' / (str(time.time_ns()) + '.json')
        temporary = target.with_suffix('.tmp')
        temporary.write_text(json.dumps(command), encoding='utf-8')
        temporary.replace(target)

    def append(file, line):
        with file.open("a", encoding="utf-8") as stream:
            stream.write(line + "\n")

    def sdkline(file, event):
        append(file, time.strftime("%Y-%m-%dT%H:%M:%S") + ".000 Posted Request HLSetVideo[" + event + "]")

    try:
        fixture = subprocess.Popen([str(fixture_exe)])
        sdk_log = sdk_folder / ("HuntGame_" + str(fixture.pid) + ".log")
        sdkline(sdk_log, "old")
        tracker = subprocess.Popen([str(app / 'app' / 'HuntTracker.exe'), '--root', str(app), '--headless', '--game-process', 'HuntCombatFixture'])
        first = wait(lambda: state(), "tracker ready")
        wait(lambda: state()["sdk_readable"], "logs attached")
        assert 'recent' not in first['statistics'], 'overlay payload includes full session history'
        assert first['revision'] > 0 and first['statistics_revision'] > 0
        for log in (app / 'logs').glob('events-*'):
            log.rename(log.with_name(log.stem + '-moved' + log.suffix))
        time.sleep(.4)
        assert state()["kills"] == 0 and not state()["events"], "old events replayed"
        started = time.monotonic()
        sdkline(sdk_log, "a1")
        early = wait(lambda: state() if state()["events"] else None, "fast alert")
        assert early["delay_ms"] == 0, "kill-only alert must have no artificial delay"
        assert early["revision"] > first["revision"], "alert publication revision did not advance"
        assert early["kills"] == 0, "fast alert must precede classification"
        wait(lambda: state()["kills"] == 1, "kill publication")
        latency = (time.monotonic() - started) * 1000
        assert 300 <= latency < 1500, latency
        sdkline(sdk_log, "a1")
        time.sleep(.4)
        assert state()["kills"] == 1, "duplicate highlight"
        sdkline(sdk_log, "d1")
        time.sleep(.18)
        append(game_log, "Starting UI state machine 'death/StateMachine'")
        wait(lambda: state()["deaths"] == 1, "death filtered")
        assert state()["kills"] == 1 and len(state()["events"]) == 2, "fast death signal must alert once but count as death"
        append(game_log, "Stopping UI state machine 'death/StateMachine'")
        time.sleep(.5)
        sdkline(sdk_log, "a2")
        time.sleep(.13)
        sdkline(sdk_log, "d2")
        time.sleep(.10)
        append(game_log, "Starting UI state machine 'death/StateMachine'")
        wait(lambda: state()["deaths"] == 2 and state()["kills"] == 2, "nearby kill and death")
        append(game_log, "Stopping UI state machine 'death/StateMachine'")
        before = len(state()["events"])
        post("/api/test/death", first["session"])
        time.sleep(.4)
        assert len(state()["events"]) == before + 1 and state()["events"][-1]["type"] == "death", "test death must bypass game mode"
        before += 1
        post("/api/test/kill", first["session"])
        wait(lambda: len(state()["events"]) == before + 1, "test kill reaches overlay")
        assert state()["events"][-1]["test"] and state()["kills"] == 2
        assert 'sound' in state()['events'][-1]
        assert state()['events'][-1]['image'] == 'source/images/kill/skull.png'
        replay_id = str(uuid.uuid4())
        post('/api/test/kill', first['session'], replay_id)
        wait(lambda: len(state()['events']) == before + 2, 'first unique command')
        post('/api/test/kill', first['session'], replay_id)
        time.sleep(.6)
        assert len(state()['events']) == before + 2, 'duplicate test command replayed'
        before = len(state()['events'])
        post('/api/test/kill', 'wrong')
        time.sleep(.5)
        assert len(state()['events']) == before, 'stale command accepted'
        initial_sounds = len(state()['media']['kill']['sounds'])
        source_sound = app / 'source' / 'sounds' / 'kill' / 'fixture.mp3'
        (app / "source" / "sounds" / "kill" / "new voice #1.mp3").write_bytes(source_sound.read_bytes())
        wait(lambda: len(state()["media"]["kill"]["sounds"]) == initial_sounds + 1, "live MP3 discovery")
        assert any('new%20voice%20%231.mp3' in s for s in state()['media']['kill']['sounds'])
        (app / "source" / "sounds" / "kill" / "new voice #1.mp3").unlink()
        wait(lambda: len(state()["media"]["kill"]["sounds"]) == initial_sounds, "live sound removal")
        for kind, folder_name in (('kill', 'kill'), ('death', 'death'), ('trade', 'trade')):
            added = app / 'source' / 'images' / folder_name / 'череп # 100%.PNG'
            initial = len(state()['media'][kind]['images'])
            added.write_bytes((app / 'source' / 'images' / folder_name / 'skull.png').read_bytes())
            wait(lambda: len(state()['media'][kind]['images']) == initial + 1, 'live PNG discovery: ' + kind)
            assert any('%23%20100%25.PNG' in name for name in state()['media'][kind]['images'])
            added.unlink()
            wait(lambda: len(state()['media'][kind]['images']) == initial, 'live PNG removal: ' + kind)
        game_log.unlink()
        wait(lambda: not state()["game_readable"], "missing game log")
        sdkline(sdk_log, "unknown")
        wait(lambda: suppressed() == 1, "unknown suppressed")
        assert state()["kills"] == 2
        game_log.write_text("Starting UI state machine 'loading/StateMachine'\n", encoding="utf-8")
        wait(lambda: state()["game_readable"], "recreated game log")
        fixture.terminate(); fixture.wait(timeout=3)
        wait(lambda: state()["game_pid"] == 0, "game stop")
        fixture = subprocess.Popen([str(fixture_exe)])
        sdk_log = sdk_folder / ("HuntGame_" + str(fixture.pid) + ".log")
        sdk_log.write_text("", encoding="utf-8")
        wait(lambda: state()["game_pid"] == fixture.pid and state()["sdk_readable"], "new PID attached")
        sdkline(sdk_log, "fresh")
        wait(lambda: state()["kills"] == 3, "new game kill")
        with sdk_log.open("ab") as stream:
            stream.write(b"Posted Request HLSetVideo[split")
        time.sleep(.4)
        assert state()["kills"] == 3
        with sdk_log.open("ab") as stream:
            stream.write(b"]\n")
        wait(lambda: state()["kills"] == 4, "partial line completes")
        sdk_log.write_text("#" * 5000 + "\nPosted Request HLSetVideo[rewritten]\n", encoding="utf-8")
        wait(lambda: state()["kills"] == 5, "in-place rewrite")
        sdk_log.write_text('#' * 7000 + '\nPosted Request HLSetVideo[rewritten]\n', encoding='utf-8')
        time.sleep(.5)
        assert state()['kills'] == 5
        sdkline(sdk_log, 'gap')
        time.sleep(.07)
        moved = game_log.with_suffix('.held')
        game_log.rename(moved)
        time.sleep(.10)
        moved.rename(game_log)
        wait(lambda: suppressed() == 2, 'temporary read failure suppresses kill')
        assert state()['kills'] == 5
        append(sdk_log, 'Posted Request HLSetVideo[batch1]\nPosted Request HLSetVideo[batch2]')
        time.sleep(.10)
        append(game_log, "Starting UI state machine 'death/StateMachine'")
        wait(lambda: state()['deaths'] == 3 and suppressed() == 3, 'ambiguous batch is conservative')
        assert state()['kills'] == 5
        before_games = state()['statistics']['games']
        old_match = "Starting UI state machine 'loading/StateMachine'\nPrepareLevel 'levels/creek' (client)\n"
        game_log.write_text('old line\n' * 140000 + old_match, encoding='utf-8')
        wait(lambda: state()['game_readable'], 'large rewritten game log')
        time.sleep(2)
        assert state()['statistics']['games'] == before_games, 'replayed match after first read batch'
        duplicate = subprocess.Popen([str(app / 'app' / 'HuntTracker.exe'), '--root', str(app), '--headless', '--game-process', 'HuntCombatFixture'])
        duplicate.wait(timeout=3)
        assert state()["session"] == first["session"], "duplicate start replaced process"
        post("/api/stop", first["session"])
        tracker.wait(timeout=5)
        records = [json.loads(line) for log in (app / "logs").glob("*.jsonl") for line in log.read_text(encoding="utf-8").splitlines()]
        assert any(r["type"] == "death" and 300 <= r["data"]["wait_ms"] for r in records)
        assert not any(r['type'] in ('highlight', 'death_screen_opened') for r in records), 'normal diagnostics logged raw signals'
        assert any(r["type"] == "unknown" for r in records)
        assert records[-1]["type"] == "stopped"
        assert 'unknown' not in state() and 'unknown' not in state()['statistics']
        assert not state()['running']
        assert not list((app / 'logs').glob('*.log')), 'duplicate readable journal written continuously'
        readable = '\n'.join(r['message'] for r in records)
        assert 'Счётчики не изменены' in readable and 'Звук:' in readable
        assert all('message' in r for r in records)
        saved = json.loads((app / 'data' / 'statistics.json').read_text(encoding='utf-8'))
        assert (saved['kills'], saved['deaths']) == (5, 3), saved
        assert 'alerts' not in state()['display'] and 'stats' not in state()['display']
        last_state = None
        tracker = subprocess.Popen([str(app / 'app' / 'HuntTracker.exe'), '--root', str(app), '--headless', '--game-process', 'HuntCombatFixture'])
        restarted = wait(lambda: state() and state()['running'] and state()['session'] != first['session'] and state(), 'restart with saved statistics')
        assert restarted['statistics']['id'] == saved['id']
        assert (restarted['kills'], restarted['deaths']) == (5, 3)
        post('/api/stop', restarted['session'])
        tracker.wait(timeout=5)
        for mode in ('kills-deaths', 'trades'):
            old_session = state()['session']
            config['alertMode'] = mode
            config['detectTrades'] = mode == 'trades'
            (app / 'config.json').write_text(json.dumps(config), encoding='utf-8')
            tracker = subprocess.Popen([str(app / 'app' / 'HuntTracker.exe'), '--root', str(app), '--headless', '--game-process', 'HuntCombatFixture'])
            wait(lambda: state()['running'] and state()['session'] != old_session and state()['sdk_readable'], 'mode restart')
            append(game_log, "Starting UI state machine 'loading/StateMachine'")
            wait(lambda: (state()['statistics']['current'] or {}).get('status') == 'loading', 'fresh match history')
            initial_kills, initial_deaths = state()['kills'], state()['deaths']
            assert state()['delay_ms'] == (500 if mode == 'trades' else 300)
            sdkline(sdk_log, mode.replace('-', '') + 'death')
            time.sleep(.12)
            append(game_log, "Starting UI state machine 'death/StateMachine'")
            wait(lambda: state()['deaths'] == initial_deaths + 1, 'death alert mode')
            assert state()['events'][-1]['type'] == 'death'
            assert state()['events'][-1]['sound'] == 'source/sounds/death/death.wav'
            assert state()['events'][-1]['image'] == 'source/images/death/skull.png'
            assert state()['statistics']['current']['combat_events'] == ['death']
            assert state()['kills'] == initial_kills and state()['statistics']['trades'] == 0
            if mode == 'trades':
                append(game_log, "Stopping UI state machine 'death/StateMachine'")
                time.sleep(.15)
                before = len(state()['events'])
                started_trade = time.monotonic()
                append(sdk_log, 'Posted Request HLSetVideo[trade1]\nPosted Request HLSetVideo[trade2]')
                time.sleep(.12)
                append(game_log, "Starting UI state machine 'death/StateMachine'")
                time.sleep(.20)
                assert state()['statistics']['trades'] == 0, 'trade before 500ms gate'
                wait(lambda: state()['statistics']['trades'] == 1, 'one combined trade')
                assert 500 <= (time.monotonic() - started_trade) * 1000 < 1600
                assert len(state()['events']) == before + 1 and state()['events'][-1]['type'] == 'trade'
                assert state()['events'][-1]['sound'] == 'source/sounds/trade/trade.wav'
                assert state()['events'][-1]['image'] == 'source/images/trade/skull.png'
                assert state()['kills'] == initial_kills + 1 and state()['deaths'] == initial_deaths + 2
                assert state()['statistics']['kills'] == state()['kills'] and state()['statistics']['deaths'] == state()['deaths']
                assert state()['statistics']['current']['combat_events'] == ['death', 'kill', 'death']
                append(game_log, "Stopping UI state machine 'death/StateMachine'")
                time.sleep(.2)
                append(sdk_log, 'Posted Request HLSetVideo[triple1]\nPosted Request HLSetVideo[triple2]\nPosted Request HLSetVideo[triple3]')
                time.sleep(.1)
                append(game_log, "Starting UI state machine 'death/StateMachine'")
                time.sleep(.7)
                assert state()['statistics']['trades'] == 1, 'ambiguous burst became a trade'
                counters = {key: state()['statistics'][key] for key in ('kills', 'deaths', 'trades')}
                history = state()['statistics']['current']['combat_events'][:]
                before = len(state()['events'])
                post('/api/trade', state()['session'])
                wait(lambda: len(state()['events']) == before + 1, 'trade test command')
                assert state()['events'][-1]['test'] and state()['events'][-1]['type'] == 'trade'
                assert counters == {key: state()['statistics'][key] for key in counters}
                assert state()['statistics']['current']['combat_events'] == history
            assert 'alerts' not in state()['display'] and 'stats' not in state()['display'], 'display independence'
            post('/api/stop', state()['session'])
            tracker.wait(timeout=5)
        for alert_mode, detect in [('kills', True), ('trades', False)]:
            old_session = state()['session']
            config.update(alertMode=alert_mode, detectTrades=detect)
            (app / 'config.json').write_text(json.dumps(config), encoding='utf-8')
            tracker = subprocess.Popen([str(app / 'app' / 'HuntTracker.exe'), '--root', str(app), '--headless', '--game-process', 'HuntCombatFixture'])
            wait(lambda: state()['running'] and state()['session'] != old_session and state()['sdk_readable'], 'independent modes startup')
            append(game_log, "Starting UI state machine 'loading/StateMachine'")
            wait(lambda: state()['statistics']['current']['status'] == 'loading', 'independent modes match')
            before_trade = state()['statistics']['trades']
            before_kills, before_deaths = state()['kills'], state()['deaths']
            append(sdk_log, 'Posted Request HLSetVideo[independent' + alert_mode + '1]\nPosted Request HLSetVideo[independent' + alert_mode + '2]')
            if detect:
                wait(lambda: len(state()['events']) == 2, 'immediate kill alerts with statistical trades enabled')
                assert state()['statistics']['trades'] == before_trade
                assert all(event['type'] == 'kill' for event in state()['events'])
            time.sleep(.08)
            append(game_log, "Starting UI state machine 'death/StateMachine'")
            if detect:
                wait(lambda: state()['statistics']['trades'] == before_trade + 1, 'trade counted independently of kill-only alerts')
                assert (state()['kills'], state()['deaths']) == (before_kills + 1, before_deaths + 1)
            else:
                wait(lambda: state()['events'] and state()['events'][-1]['type'] == 'trade', 'trade alert with statistical trades disabled')
                assert state()['statistics']['trades'] == before_trade, 'alert mode enabled statistical trades'
            post('/api/stop', state()['session']); tracker.wait(timeout=5)
        old_session = state()['session']
        config.update(alertsEnabled=False, statisticsEnabled=False, autoRefreshMedia=False, detectTrades=False, alertMode='kills', statisticsRefreshMs=5000)
        (app / 'config.json').write_text(json.dumps(config), encoding='utf-8')
        tracker = subprocess.Popen([str(app / 'app' / 'HuntTracker.exe'), '--root', str(app), '--headless', '--game-process', 'HuntCombatFixture'])
        wait(lambda: state()['running'] and state()['session'] != old_session and state()['sdk_readable'], 'disabled modules startup')
        initial_kills = state()['kills']
        assert not state()['media']['kill']['images'], 'disabled alert module scanned media'
        sdkline(sdk_log, 'disabledmoduleskill')
        wait(lambda: state()['kills'] == initial_kills + 1, 'collection with both modules disabled')
        assert not state()['events'], 'disabled alerts emitted a game event'
        def browser_state(name):
            try:
                text = (app / '_runtime' / (name + '.js')).read_text(encoding='utf-8')
                return json.loads(text.removeprefix('window.HuntKillState=').removesuffix(';'))
            except (OSError, ValueError):
                return None
        widget = wait(lambda: browser_state('statistics'), 'statistics browser state')
        alerts = wait(lambda: browser_state('alerts'), 'alerts browser state')
        assert widget['statistics'] is None
        assert 'statistics' not in alerts, 'alerts carry statistics payload'
        assert isinstance(alerts['events'], list), 'empty alert queue must serialize as an array'
        counters = (state()['kills'], state()['deaths'])
        post('/api/death', state()['session'])
        wait(lambda: state()['events'] and state()['events'][-1]['test'], 'test bypasses disabled module')
        assert counters == (state()['kills'], state()['deaths'])
        assert state()['media']['kill']['images'], 'test did not load media on demand'
        alerts = wait(lambda: value['events'] if (value := browser_state('alerts')) else None, 'browser receives test')
        assert isinstance(alerts, list) and len(alerts) == 1, 'one alert must serialize as an array'
        (app / 'source' / 'images' / 'kill' / 'manual-only.png').write_bytes(b'fixture')
        time.sleep(2.2)
        assert 'source/images/kill/manual-only.png' not in state()['media']['kill']['images'], 'disabled auto refresh scanned files'
        stats_path = app / 'data' / 'statistics.json'
        saved_path = stats_path.with_suffix('.saved')
        stats_path.rename(saved_path); stats_path.mkdir()
        output_path = app / '_runtime' / 'statistics.js'
        output_path.unlink(); output_path.mkdir()
        sdkline(sdk_log, 'storagefailurekill')
        wait(lambda: state()['kills'] == counters[0] + 1, 'collection survives storage and overlay failures')
        wait(lambda: 'не сохранена' in state()['warning'], 'persistence warning')
        assert tracker.poll() is None
        stats_path.rmdir(); saved_path.rename(stats_path); output_path.rmdir()
        def saved_kills():
            try:
                return json.loads(stats_path.read_text(encoding='utf-8'))['kills']
            except (OSError, ValueError):
                return -1
        wait(lambda: saved_kills() == counters[0] + 1, 'persistence retry', 10)
        wait(lambda: output_path.is_file(), 'overlay recovery')
        commands = app / '_runtime' / 'commands'
        shutil.rmtree(commands); commands.write_text('blocked', encoding='utf-8')
        sdkline(sdk_log, 'commandsfailurekill')
        wait(lambda: state()['kills'] == counters[0] + 2, 'collection survives inaccessible command folder')
        commands.unlink()
        wait(lambda: commands.is_dir(), 'command folder recreated')
        wait(lambda: saved_kills() == counters[0] + 2, 'save before shutdown failure')
        stats_path.rename(saved_path); stats_path.mkdir()
        sdkline(sdk_log, 'shutdownfailurekill')
        wait(lambda: state()['kills'] == counters[0] + 3, 'unsaved final kill')
        post('/api/stop', state()['session']); tracker.wait(timeout=5)
        recovery_path = app / '_runtime' / 'statistics-recovery.json'
        recovery = json.loads(recovery_path.read_text(encoding='utf-8'))
        assert recovery['kills'] == counters[0] + 3, 'shutdown lost unsaved counters'
        stats_path.rmdir(); saved_path.rename(stats_path)
        old_session = state()['session']
        tracker = subprocess.Popen([str(app / 'app' / 'HuntTracker.exe'), '--root', str(app), '--headless', '--game-process', 'HuntCombatFixture'])
        wait(lambda: state()['running'] and state()['session'] != old_session, 'recovery restart')
        assert state()['kills'] == counters[0] + 3, 'restart ignored newest recovery'
        wait(lambda: not recovery_path.exists(), 'recovery committed')
        post('/api/stop', state()['session']); tracker.wait(timeout=5)
        old_session = state()['session']
        game_log.unlink()
        config.update(alertsEnabled=True, autoRefreshMedia=True)
        (app / 'config.json').write_text(json.dumps(config), encoding='utf-8')
        image_folder = app / 'source' / 'images' / 'kill'
        shutil.rmtree(image_folder); image_folder.write_text('blocked', encoding='utf-8')
        tracker = subprocess.Popen([str(app / 'app' / 'HuntTracker.exe'), '--root', str(app), '--headless', '--game-process', 'HuntCombatFixture'])
        wait(lambda: state()['running'] and state()['session'] != old_session and state()['sdk_readable'], 'startup without game log')
        games_before = state()['statistics']['games']
        append(game_log, "Starting UI state machine 'loading/StateMachine'")
        wait(lambda: state()['statistics']['games'] == games_before + 1, 'first match from newly created game log')
        time.sleep(2.3)
        scan_count = sum(1 for log in (app / 'logs').glob('*.jsonl') for line in log.read_text(encoding='utf-8').splitlines() if (entry := json.loads(line)).get('data', {}) and entry['data'].get('category') == 'media')
        assert 2 <= scan_count <= 3, f'media errors caused a busy retry loop: {scan_count}'
        kills_before = state()['kills']
        sdkline(sdk_log, 'closewhilepending')
        wait(lambda: state()['events'] and state()['events'][-1]['type'] == 'kill', 'immediate alert before closing')
        post('/api/stop', state()['session']); tracker.wait(timeout=5)
        assert state()['kills'] == kills_before + 1, 'closing dropped a read event awaiting classification'
        assert saved_kills() == kills_before + 1, 'last classified event was not saved on close'
        print('PASS: first log created after startup and bounded media retries.')
        print('PASS: command folder failure, final-save failure and automatic recovery after restart.')
        print('PASS: independent modules, lazy media, manual refresh mode, separate browser payloads, storage retry and overlay failure isolation.')
        print('PASS: death alerts, 500ms trade gate, combined event, accurate counters, ambiguous burst, test isolation, headless modes.')
        print("PASS: .NET startup, full gameFolder path, immediate alerts with 300ms classification (%.0fms publication), fast kill-mode alerts, deaths, nearby events, dedup, file commands, stale sessions, hot MP3, missing/recreated log, PID restart, partial lines, rewrites, single instance, graceful stop, redacted paths and journals." % latency)
    finally:
        if tracker and tracker.poll() is None:
            tracker.terminate(); tracker.wait(timeout=5)
        if fixture and fixture.poll() is None:
            fixture.terminate(); fixture.wait(timeout=5)


if __name__ == "__main__":
    run()
