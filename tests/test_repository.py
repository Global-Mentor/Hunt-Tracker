from pathlib import Path
import io
import re
import subprocess
import tokenize

ROOT = Path(__file__).resolve().parents[1]


def ignored(path):
    result = subprocess.run(
        ['git', 'check-ignore', '--no-index', '-q', path], cwd=ROOT,
        capture_output=True, text=True,
    )
    if result.returncode not in (0, 1):
        raise RuntimeError(result.stderr)
    return result.returncode == 0


required = [
    'config.example.json', 'HuntTracker.slnx', 'Directory.Build.props',
    'src/hunt-tracker-core/packages.lock.json',
    'src/hunt-tracker-desktop/packages.win-x64.lock.json',
    'source/images/kill/skull.png', 'source/images/death/skull.png',
    'source/images/trade/skull.png', 'source/sounds/kill/kill.mp3',
    'source/sounds/death/death.wav', 'source/sounds/trade/trade.wav',
    'src/hunt-tracker-desktop/assets/app-icon.ico',
    'src/hunt-tracker-desktop/assets/game-ui/panel.png',
]
private = [
    'config.json', 'data/statistics.json', 'data/sessions/session.json',
    'logs/events.jsonl', '_runtime/state.json', '.local/preview.png',
    'dist/HuntTracker.zip', 'personal-backup.zip', 'personal-backup.7z',
    'AGENTS.md', '.codex/settings.json', '.agents/notes.md',
    'source/sounds/kill/custom.mp3', 'source/images/death/custom.png',
    'source/images/trade/alternate.png', 'source/sounds/trade/custom.wav',
    'src/hunt-tracker-desktop/bin/HuntTracker.exe',
    'src/hunt-tracker-desktop/obj/cache.json', 'Game.log', 'events.jsonl',
]
for path in required:
    assert (ROOT / path).is_file(), f'Missing source asset: {path}'
    assert not ignored(path), f'Source asset excluded from Git: {path}'
for path in private:
    assert ignored(path), f'Local data can enter Git: {path}'
tracked = subprocess.check_output(
    ['git', 'ls-files', '-ci', '--exclude-standard'], cwd=ROOT, text=True,
).splitlines()
assert not tracked, f'Ignored files already tracked: {tracked}'
sources = subprocess.check_output(
    ['git', 'ls-files', '--cached', '--others', '--exclude-standard'], cwd=ROOT, text=True,
).splitlines()
code_extensions = {'.cs', '.js', '.cjs', '.py', '.ps1', '.bat', '.html', '.css', '.xaml'}
for name in sources:
    path = ROOT / name
    if not path.is_file() or path.suffix not in code_extensions:
        continue
    text = path.read_text(encoding='utf-8-sig')
    assert not re.search('[\u2013\u2014\u2015]', text), f'Long dash in source: {name}'
    assert not re.search(r'\b[A-Za-z]:[\\/]', text), f'Absolute drive path in source: {name}'
    if path.suffix == '.py':
        assert not any(t.type == tokenize.COMMENT for t in tokenize.generate_tokens(io.StringIO(text).readline)), f'Comment in source: {name}'
    elif path.suffix == '.ps1':
        assert not re.search(r'^\s*#|<#', text, re.MULTILINE), f'Comment in source: {name}'
    else:
        assert not re.search(r'^\s*//|^\s*/\*|<!--', text, re.MULTILINE), f'Comment in source: {name}'
print('PASS: bundled assets and dependency locks included; personal media, data and artifacts excluded.')
print('PASS: source style, comments and absolute drive paths.')
