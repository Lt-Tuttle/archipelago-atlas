r"""The pack lab's name tables: every game's item and location names, from the engine, offline.

A developer tool. It makes an engine data folder of the lab's own (never the user's): every official world unpacked
from Archipelago's release zip, every community apworld the corpus holds, Atlas's bridge; then it asks the engine which
games load (atlas_run.py --atlas-games) and the AtlasNames component for their names, and writes one file per game in
GameNames' shape (<corpus>\names\<Game> (local install).json), plus names\_load_report.json saying which worlds didn't
load and why, and which apworld file (by its SHA-256) each community game's names came from: the version the packs are
measured on (build_game_index.py records it per pack). Nothing downloaded; nothing the corpus holds is run except as
Archipelago loads a world.

Usage:
  python Tools/build_corpus_names.py --corpus "...\Pack Corpus" --lab "...\Pack Corpus\lab"
      [--core Builds\engine-core] [--archipelago-zip Builds\engine-data\engine\downloads\Archipelago-0.6.7.zip] [--skip-unpack]
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.dirname(HERE)
ROOT = os.path.dirname(SOURCE)
PY_DIR = os.path.join(SOURCE, 'Scripts', 'Core', 'Engine', 'Python')


def unpack_worlds(archipelago_zip, worlds_dir):
    with zipfile.ZipFile(archipelago_zip) as z:
        names = z.namelist()
        prefix = names[0].split('/')[0] + '/' if '/' in names[0] else ''
        count = 0
        for name in names:
            if not name.startswith(prefix + 'worlds/') or name.endswith('/'):
                continue
            rel = name[len(prefix + 'worlds/'):]
            folder = rel.split('/', 1)[0]
            if '/' not in rel or folder.endswith('.apworld'):
                if rel.endswith('.apworld'):
                    dest = os.path.join(worlds_dir, rel)
                else:
                    continue
            else:
                dest = os.path.join(worlds_dir, rel.replace('/', os.sep))
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with z.open(name) as src, open(dest, 'wb') as out:
                shutil.copyfileobj(src, out)
            count += 1
    return count


def run_engine(python, core, lab, component, request=None, timeout=1800):
    env = dict(os.environ)
    env.update(ATLAS_ENGINE_DATA=lab, APPDATA=os.path.join(lab, 'home', 'AppData'), LOCALAPPDATA=os.path.join(lab, 'home', 'Local'),
               TEMP=os.path.join(lab, 'home', 'Temp'), TMP=os.path.join(lab, 'home', 'Temp'), PYTHONPYCACHEPREFIX=os.path.join(lab, 'pycache'))
    for d in ('home/AppData', 'home/Local', 'home/Temp'):
        os.makedirs(os.path.join(lab, d), exist_ok=True)
    args = [python, '-I', os.path.join(PY_DIR, 'atlas_run.py'), os.path.join(core, 'archipelago'), component]
    r = subprocess.run(args, input=(json.dumps(request) + '\n') if request is not None else None, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=timeout, cwd=lab, env=env)
    with open(os.path.join(lab, f'{component}.stderr.txt'), 'w', encoding='utf-8') as f:
        f.write(r.stderr)
    lines = [l for l in r.stdout.splitlines() if l.strip()]
    return json.loads(lines[-1]) if lines else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--corpus', required=True)
    ap.add_argument('--lab', required=True)
    ap.add_argument('--core', default=os.path.join(ROOT, 'Builds', 'engine-core'))
    ap.add_argument('--archipelago-zip', default=os.path.join(ROOT, 'Builds', 'engine-data', 'engine', 'downloads', 'Archipelago-0.6.7.zip'))
    ap.add_argument('--skip-unpack', action='store_true')
    args = ap.parse_args()

    lab = os.path.abspath(args.lab)
    worlds = os.path.join(lab, 'ap', 'worlds')
    os.makedirs(worlds, exist_ok=True)
    os.makedirs(os.path.join(lab, 'ap', 'Players'), exist_ok=True)
    python = os.path.join(args.core, 'python', 'python.exe')
    if not args.skip_unpack:
        n = unpack_worlds(args.archipelago_zip, worlds)
        print(f'unpacked {n} files of the official worlds', flush=True)
        copied = 0
        apworlds = os.path.join(args.corpus, 'apworlds')
        for name in sorted(os.listdir(apworlds)) if os.path.isdir(apworlds) else []:
            if not name.lower().endswith('.apworld'):
                continue
            # Archipelago loads a .apworld only when the file is named after the module folder inside it.
            src = os.path.join(apworlds, name)
            try:
                with zipfile.ZipFile(src) as z:
                    inner = next((n.split('/', 1)[0] for n in z.namelist() if '/' in n and n.endswith('__init__.py') and n.count('/') == 1), None)
            except zipfile.BadZipFile:
                inner = None
            if not inner:
                print('  not an apworld:', name, flush=True)
                continue
            shutil.copy(src, os.path.join(worlds, inner + '.apworld'))
            copied += 1
        print(f'copied {copied} community apworlds', flush=True)
        with zipfile.ZipFile(os.path.join(worlds, 'UltimateBridge.apworld'), 'w') as z:
            z.write(os.path.join(PY_DIR, 'atlas_bridge.py'), 'UltimateBridge/__init__.py')

    listing = run_engine(python, args.core, lab, '--atlas-games') or {}
    games = listing.get('games', {})
    errors = listing.get('errors', {})
    failed = listing.get('failed', [])
    print(f'{len(games)} games load; {len(failed)} worlds failed', flush=True)
    names_dir = os.path.join(args.corpus, 'names')
    os.makedirs(names_dir, exist_ok=True)
    wanted = sorted(g for g in games if g not in ('Archipelago', 'Universal Tracker'))
    answer = run_engine(python, args.core, lab, 'AtlasNames', {'games': wanted}) or {}
    tables = answer.get('games', {})
    now = datetime.now(timezone.utc).isoformat()
    written = 0
    for game, table in tables.items():
        safe = ''.join('_' if c in '<>:"/\\|?*' else c for c in game)
        path = os.path.join(names_dir, f'{safe} (local install).json')
        with open(path, 'w', encoding='utf-8') as f:
            json.dump({'Game': game, 'Source': 'local install', 'Fetched': now, 'Version': table.get('version', ''),
                       'Items': table.get('items', {}), 'Locations': table.get('locations', {})}, f)
        written += 1
    # Each community game's apworld as the engine loaded it (the module's .apworld in the lab's worlds folder; the last copy of
    # a module wins), by its SHA-256: the corpus manifest says which release that is.
    apworlds = {}
    for game, module in games.items():
        path = os.path.join(worlds, f'{module}.apworld') if module else ''
        if game in tables and path and os.path.isfile(path):
            with open(path, 'rb') as src:
                apworlds[game] = {'module': module, 'sha256': hashlib.sha256(src.read()).hexdigest()}
    with open(os.path.join(names_dir, '_load_report.json'), 'w', encoding='utf-8') as f:
        json.dump({'written': written, 'games': games, 'failed': failed, 'errors': errors, 'names_error': answer.get('error'), 'apworlds': apworlds}, f, indent=1)
    print(f'wrote {written} name tables; load report in {names_dir}', flush=True)
    for name, why in errors.items():
        print('  failed:', name, '-', (why.get('error') or '')[:140], flush=True)


if __name__ == '__main__':
    main()
