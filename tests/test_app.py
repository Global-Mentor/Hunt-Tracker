"""Exercise the tracker with an isolated game process and synthetic logs."""
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
    for name in ('app', 'Images', 'Sounds'):
        shutil.copytree(SOURCE / name, app / name)
    shutil.copyfile(SOURCE / 'config.example.json', app / 'config.json')
    (app / 'Sounds' / 'fixture.mp3').write_bytes(b'fixture audio')
    game_folder = folder / "game"
    (game_folder / "USER").mkdir(parents=True)
    sdk_folder = folder / "sdk"
    sdk_folder.mkdir()
    game_log = game_folder / "USER" / "Game.log"
    game_log.write_text("Starting UI state machine 'death/StateMachine'\n", encoding="utf-8")
    fixture_exe = folder / "HuntCombatFixture.exe"
    build = folder / "fixture.ps1"
    build.write_text("Add-Type -TypeDefinition 'public static class Fixture { public static void Main() { System.Threading.Thread.Sleep(120000); } }' -OutputAssembly '" + str(fixture_exe).replace("'", "''") + "' -OutputType WindowsApplication", encoding="utf-8-sig")
    subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(build)], check=True, capture_output=True)
    config = json.loads((app / "config.json").read_text())
    config.update(gameFolder=str(game_folder), nvidiaLogs=str(sdk_folder))
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
        tracker = subprocess.Popen(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(app / 'app' / 'Tracker.ps1'), '-GameProcessName', 'HuntCombatFixture'])
        first = wait(lambda: state(), "tracker ready")
        wait(lambda: state()["sdk_readable"], "logs attached")
        time.sleep(.4)
        assert state()["kills"] == 0 and not state()["events"], "old events replayed"
        started = time.monotonic()
        sdkline(sdk_log, "a1")
        time.sleep(.12)
        assert not state()["events"], "kill before gate"
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
        assert state()["kills"] == 1 and len(state()["events"]) == 1
        append(game_log, "Stopping UI state machine 'death/StateMachine'")
        time.sleep(.5)
        # Two nearby highlights: the screen belongs to the closest pending one.
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
        assert len(state()["events"]) == before, "test death caused alert"
        post("/api/test/kill", first["session"])
        wait(lambda: len(state()["events"]) == before + 1, "test kill reaches overlay")
        assert state()["events"][-1]["test"] and state()["kills"] == 2
        assert 'sound' in state()['events'][-1]
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
        initial_sounds = len(state()['sounds'])
        source_sound = app / 'Sounds' / 'fixture.mp3'
        (app / "Sounds" / "new voice #1.mp3").write_bytes(source_sound.read_bytes())
        wait(lambda: len(state()["sounds"]) == initial_sounds + 1, "live MP3 discovery")
        assert any('new%20voice%20%231.mp3' in s for s in state()['sounds'])
        (app / "Sounds" / "new voice #1.mp3").unlink()
        wait(lambda: len(state()["sounds"]) == initial_sounds, "live sound removal")
        game_log.unlink()
        wait(lambda: not state()["game_readable"], "missing game log")
        sdkline(sdk_log, "unknown")
        wait(lambda: state()["unknown"] == 1, "unknown suppressed")
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
        # Partial UTF-8 and lines are retained until a full newline arrives.
        with sdk_log.open("ab") as stream:
            stream.write(b"Posted Request HLSetVideo[split")
        time.sleep(.4)
        assert state()["kills"] == 3
        with sdk_log.open("ab") as stream:
            stream.write(b"]\n")
        wait(lambda: state()["kills"] == 4, "partial line completes")
        # Same file rewritten to a larger size must reset the cursor.
        sdk_log.write_text("#" * 5000 + "\nPosted Request HLSetVideo[rewritten]\n", encoding="utf-8")
        wait(lambda: state()["kills"] == 5, "in-place rewrite")
        # A rewrite containing an already seen request must not replay that kill.
        sdk_log.write_text('#' * 7000 + '\nPosted Request HLSetVideo[rewritten]\n', encoding='utf-8')
        time.sleep(.5)
        assert state()['kills'] == 5
        # A brief read failure during the wait must survive until classification.
        sdkline(sdk_log, 'gap')
        time.sleep(.07)
        moved = game_log.with_suffix('.held')
        game_log.rename(moved)
        time.sleep(.10)
        moved.rename(game_log)
        wait(lambda: state()['unknown'] == 2, 'temporary read failure suppresses kill')
        assert state()['kills'] == 5
        # Same-batch highlights cannot be distinguished by read timing alone.
        append(sdk_log, 'Posted Request HLSetVideo[batch1]\nPosted Request HLSetVideo[batch2]')
        time.sleep(.10)
        append(game_log, "Starting UI state machine 'death/StateMachine'")
        wait(lambda: state()['deaths'] == 3 and state()['unknown'] == 3, 'ambiguous batch is conservative')
        assert state()['kills'] == 5
        # A replacement larger than one read batch must not replay old matches.
        before_games = state()['statistics']['games']
        old_match = "Starting UI state machine 'loading/StateMachine'\nPrepareLevel 'levels/creek' (client)\n"
        game_log.write_text('old line\n' * 140000 + old_match, encoding='utf-8')
        wait(lambda: state()['game_readable'], 'large rewritten game log')
        time.sleep(2)
        assert state()['statistics']['games'] == before_games, 'replayed match after first read batch'
        duplicate = subprocess.Popen(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(app / 'app' / 'Tracker.ps1'), '-GameProcessName', 'HuntCombatFixture'])
        duplicate.wait(timeout=3)
        assert state()["session"] == first["session"], "duplicate start replaced process"
        post("/api/stop", first["session"])
        tracker.wait(timeout=5)
        records = [json.loads(line) for log in (app / "Logs").glob("*.jsonl") for line in log.read_text(encoding="utf-8").splitlines()]
        assert any(r["type"] == "death" and 300 <= r["data"]["wait_ms"] for r in records)
        assert any(r["type"] == "death_screen_opened" for r in records)
        assert any(r["type"] == "unknown" for r in records)
        assert records[-1]["type"] == "stopped"
        assert not state()['running']
        readable = '\n'.join(log.read_text(encoding='utf-8-sig') for log in (app / 'Logs').glob('*.log'))
        assert 'Алерт подавлен' in readable and 'Звук:' in readable
        assert all('message' in r for r in records)
        saved = json.loads((app / 'data' / 'statistics.json').read_text(encoding='utf-8'))
        assert (saved['kills'], saved['deaths'], saved['unknown']) == (5, 3, 3), saved
        assert not state()['display']['alerts'] and not state()['display']['stats']
        last_state = None
        tracker = subprocess.Popen(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(app / 'app' / 'Tracker.ps1'), '-GameProcessName', 'HuntCombatFixture'])
        restarted = wait(lambda: state() and state()['running'] and state()['session'] != first['session'] and state(), 'restart with saved statistics')
        assert restarted['statistics']['id'] == saved['id']
        assert (restarted['kills'], restarted['deaths'], restarted['unknown']) == (5, 3, 3)
        post('/api/stop', restarted['session'])
        tracker.wait(timeout=5)
        for mode in ('kills-deaths', 'trades'):
            old_session = state()['session']
            config['alertMode'] = mode
            (app / 'config.json').write_text(json.dumps(config), encoding='utf-8')
            tracker = subprocess.Popen(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(app / 'app' / 'Tracker.ps1'), '-GameProcessName', 'HuntCombatFixture'])
            wait(lambda: state()['running'] and state()['session'] != old_session and state()['sdk_readable'], 'mode restart')
            initial_kills, initial_deaths = state()['kills'], state()['deaths']
            assert state()['delay_ms'] == (500 if mode == 'trades' else 300)
            sdkline(sdk_log, mode.replace('-', '') + 'death')
            time.sleep(.12)
            append(game_log, "Starting UI state machine 'death/StateMachine'")
            wait(lambda: state()['deaths'] == initial_deaths + 1, 'death alert mode')
            assert state()['events'][-1]['type'] == 'death'
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
                assert state()['kills'] == initial_kills + 1 and state()['deaths'] == initial_deaths + 2
                assert state()['statistics']['kills'] == state()['kills'] and state()['statistics']['deaths'] == state()['deaths']
                # Three simultaneous requests must remain ambiguous after the first is removed.
                append(game_log, "Stopping UI state machine 'death/StateMachine'")
                time.sleep(.2)
                append(sdk_log, 'Posted Request HLSetVideo[triple1]\nPosted Request HLSetVideo[triple2]\nPosted Request HLSetVideo[triple3]')
                time.sleep(.1)
                append(game_log, "Starting UI state machine 'death/StateMachine'")
                time.sleep(.7)
                assert state()['statistics']['trades'] == 1, 'ambiguous burst became a trade'
                # Tests emit alerts but do not alter counters.
                counters = {key: state()['statistics'][key] for key in ('kills', 'deaths', 'trades', 'unknown')}
                before = len(state()['events'])
                post('/api/trade', state()['session'])
                wait(lambda: len(state()['events']) == before + 1, 'trade test command')
                assert state()['events'][-1]['test'] and state()['events'][-1]['type'] == 'trade'
                assert counters == {key: state()['statistics'][key] for key in counters}
            assert not state()['display']['alerts'] and not state()['display']['stats'], 'display independence'
            post('/api/stop', state()['session'])
            tracker.wait(timeout=5)
        print('PASS: death alerts, 500ms trade gate, combined event, accurate counters, ambiguous burst, test isolation, headless modes.')
        print("PASS: PowerShell startup, full gameFolder path, 300ms gate (%.0fms publication), kill-only alerts, deaths, nearby events, dedup, file commands, stale sessions, hot MP3, missing/recreated log, PID restart, partial lines, rewrites, single instance, graceful stop, redacted paths and journals." % latency)
    finally:
        if tracker and tracker.poll() is None:
            tracker.terminate(); tracker.wait(timeout=5)
        if fixture and fixture.poll() is None:
            fixture.terminate(); fixture.wait(timeout=5)


if __name__ == "__main__":
    run()
