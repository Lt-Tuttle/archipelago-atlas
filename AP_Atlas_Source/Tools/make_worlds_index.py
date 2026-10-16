r"""Makes the index of Archipelago's game worlds the Atlas Engine unpacks on demand.

Atlas ships Archipelago's core (and the few worlds its health check needs) and unpacks a game's world from Archipelago's
own release zip when a slot first needs it. The index says which folder holds each game, which other world folders it
imports, and whether it declares packages of its own. Make it once per Archipelago version, with the full engine's
Python (its packages, and the packages worlds declare, installed):

    <engine>\python\python.exe -I make_worlds_index.py --zip Archipelago-0.6.7.zip --sha256 <pinned> \
        --runner ..\Scripts\Core\Engine\Python\atlas_run.py --out ..\Scripts\Core\Engine\Data\archipelago_worlds.json --verify

--verify loads every world the way Atlas does (its folder and what it imports unpacked in the engine's user folder, the
rest of the worlds absent) and fails when one doesn't load. It takes a few minutes. Nothing here goes online.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

# Kept in the core: the generic world ("Archipelago" itself), the health check's smoke-test game, and the libraries
# worlds share (folders starting with "_" aren't worlds).
CORE_WORLDS = {'generic', 'checksfinder'}
IMPORT = re.compile(r'(?:^|\s)(?:from|import)\s+worlds\.([A-Za-z_][A-Za-z0-9_]*)', re.MULTILINE)
PARENT_IMPORT = re.compile(r'(?:^|\s)from\s+\.\.([A-Za-z_][A-Za-z0-9_]*)', re.MULTILINE)


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def run_games(python, runner, root, data):
    env = dict(os.environ)
    env['ATLAS_ENGINE_DATA'] = data
    env['PYTHONPYCACHEPREFIX'] = os.path.join(data, 'pycache')
    env['PYTHONNOUSERSITE'] = '1'
    done = subprocess.run([python, '-u', '-X', 'utf8', runner, root, '--atlas-games'], capture_output=True, text=True, encoding='utf-8', env=env, timeout=600)
    for line in done.stdout.splitlines():
        if line.startswith('{'):
            return json.loads(line), done.stderr
    raise RuntimeError('The runner reported nothing:\n' + done.stderr[-3000:])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--zip', required=True)
    parser.add_argument('--sha256', required=True)
    parser.add_argument('--runner', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    if sha256(args.zip) != args.sha256.lower():
        sys.exit('The zip is not the pinned one.')
    python = sys.executable
    work = tempfile.mkdtemp(prefix='atlas-worlds-')
    try:
        with zipfile.ZipFile(args.zip) as z:
            names = z.namelist()
            top = names[0].split('/')[0] + '/'
            version = top.rstrip('/').split('-', 1)[1]
            z.extractall(work)
            sizes = {}
            for info in z.infolist():
                parts = info.filename[len(top):].split('/')
                if len(parts) > 2 and parts[0] == 'worlds' and not info.is_dir():
                    sizes[parts[1]] = sizes.get(parts[1], 0) + info.file_size
        full = os.path.join(work, top.rstrip('/'))
        worlds_dir = os.path.join(full, 'worlds')
        folders = sorted(e.name for e in os.scandir(worlds_dir) if e.is_dir() and not e.name.startswith(('_', '.')) and e.name != '__pycache__')
        libraries = sorted(e.name for e in os.scandir(worlds_dir) if e.is_dir() and e.name.startswith('_') and e.name != '__pycache__')
        on_demand = [f for f in folders if f not in CORE_WORLDS]

        # Which games each folder registers: every world loaded once, as Archipelago does.
        data = os.path.join(work, 'data-all')
        loaded, _ = run_games(python, args.runner, full, data)
        games_of = {}
        for game, folder in loaded['games'].items():
            games_of.setdefault(folder, []).append(game)
        if loaded['failed']:
            print('Worlds that did not load with the full engine:', loaded['failed'])

        # What each folder imports from other world folders (any mention counts: a world may import another only when
        # it builds a seed).
        def imports_of(folder):
            found = set()
            for dirpath, _, files in os.walk(os.path.join(worlds_dir, folder)):
                for name in files:
                    if not name.endswith('.py'):
                        continue
                    with open(os.path.join(dirpath, name), encoding='utf-8', errors='replace') as f:
                        text = f.read()
                    found.update(IMPORT.findall(text))
                    if dirpath == os.path.join(worlds_dir, folder):
                        found.update(PARENT_IMPORT.findall(text))
            return sorted(x for x in found if x in on_demand and x != folder)

        index = {
            'archipelago': version,
            'zip_sha256': args.sha256.lower(),
            'zip_root': top,
            'zip_bytes': os.path.getsize(args.zip),
            'core_worlds': sorted(CORE_WORLDS) + libraries,
            'worlds': {},
        }
        for folder in on_demand:
            index['worlds'][folder] = {
                'games': sorted(games_of.get(folder, [])),
                'needs': imports_of(folder),
                'requirements': os.path.isfile(os.path.join(worlds_dir, folder, 'requirements.txt')),
                'bytes': sizes.get(folder, 0),
            }
        missing = [f for f in on_demand if not games_of.get(f)]
        if missing:
            print('Folders that registered no game (not listed for any game):', missing)

        if args.verify:
            core = os.path.join(work, 'core')
            shutil.copytree(full, core)
            for folder in on_demand:
                shutil.rmtree(os.path.join(core, 'worlds', folder))
            core_games, _ = run_games(python, args.runner, core, os.path.join(work, 'data-core'))
            print('The core alone loads:', sorted(core_games['games']))
            failures = []

            def closure(folder):
                seen, todo = set(), [folder]
                while todo:
                    f = todo.pop()
                    if f in seen:
                        continue
                    seen.add(f)
                    todo.extend(index['worlds'][f]['needs'])
                return seen

            for folder in on_demand:
                if not index['worlds'][folder]['games']:
                    continue
                data = os.path.join(work, 'data-' + folder)
                for f in closure(folder):
                    shutil.copytree(os.path.join(worlds_dir, f), os.path.join(data, 'ap', 'worlds', f))
                result, err = run_games(python, args.runner, core, data)
                absent = [g for g in index['worlds'][folder]['games'] if g not in result['games']]
                if absent or result['failed']:
                    failures.append((folder, absent, result['failed'], result.get('errors')))
                    print('FAIL', folder, absent, result['failed'], result.get('errors'))
                else:
                    print('ok', folder, len(closure(folder)), 'folder(s)')
            if failures:
                sys.exit(f'{len(failures)} world(s) did not load on demand; the index was not written.')

        with open(args.out, 'w', encoding='utf-8', newline='\r\n') as f:
            json.dump(index, f, indent=2, sort_keys=True)
            f.write('\n')
        print(f'Wrote {args.out}: {len(on_demand)} world folders on demand, core {index["core_worlds"]}.')
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == '__main__':
    main()
