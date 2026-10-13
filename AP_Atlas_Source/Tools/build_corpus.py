r"""Builds the pack lab's corpus: every game's PopTracker packs and apworlds from GitHub, downloaded once into a folder
of their own (outside the repository: they're third-party files), with a manifest the repository keeps so the corpus
can be fetched again exactly.

A developer tool, never run by Atlas or CI. It talks to GitHub through the authenticated `gh` CLI (so it isn't held to
the anonymous limits) and never runs anything it downloads: the files are data for the corpus check, the names run and
the gallery.

Usage:
  python Tools/build_corpus.py --corpus "C:\Users\me\Documents\Archipelago\Pack Corpus" [--manifest AP_Atlas_Source/Tests/corpus_manifest.json]
                               [--games "A Link to the Past" ...] [--only packs|apworlds] [--limit N] [--seed-from <folder>]

What it does, per game (official games from Scripts/Core/Engine/Data/archipelago_worlds.json, community games from
Scripts/Core/Engine/Data/apworld_sources.json):
  packs:    the searches Atlas makes (MapPackManagerControl.SearchQueries: "<game>" poptracker fork:true, then a looser
            form), the first query with results; for each project found (at most 6), its newest release with a .zip
            (the rule of ReleasesWithPacks), else the project's current files (zipball).
  apworlds: the index's project's newest release with an .apworld, else the index's newest version by its URL.
Searches are paced at GitHub's authenticated search limit (30 a minute); a game searched before is skipped (state.json
in the corpus folder), and a file whose SHA-256 the manifest already has isn't downloaded again.
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
import time
import urllib.request
import zipfile
from datetime import date

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.dirname(HERE)
DATA = os.path.join(SOURCE, 'Scripts', 'Core', 'Engine', 'Data')
GH = os.environ.get('GH', r'C:\Program Files\GitHub CLI\gh.exe')
SEARCH_PACE = 3.0   # seconds between searches: under GitHub's 30 a minute, with room for its sliding window
CALL_PACE = 0.35    # seconds between other calls
MOST_PROJECTS = 4


def log(*parts):
    line = ' '.join(str(p) for p in parts)
    print(line, flush=True)
    with open(LOG, 'a', encoding='utf-8') as f:
        f.write(line + '\n')


def gh(args, binary_to=None):
    cmd = [GH] + args
    if binary_to:
        with open(binary_to, 'wb') as out:
            r = subprocess.run(cmd, stdout=out, stderr=subprocess.PIPE)
        return r.returncode == 0, r.stderr.decode('utf-8', 'replace')
    r = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    return r.returncode == 0, (r.stdout if r.returncode == 0 else r.stderr)


def wait_for_limit(kind):
    # GitHub's limit reached: sleep until it resets (the limit's own clock), never recording a failed call as an answer.
    ok, out = gh(['api', 'rate_limit', '--jq', f'.resources.{kind}.reset'])
    try:
        reset = int(out.strip())
    except ValueError:
        reset = int(time.time()) + 60
    wait = max(5, reset - int(time.time()) + 3)
    log(f'  {kind} limit reached: waiting {wait} s')
    time.sleep(wait)


def gh_json(path, params=None, pace=CALL_PACE):
    args = ['api', '-X', 'GET', path]
    for k, v in (params or {}).items():
        args += ['-f', f'{k}={v}']
    for attempt in range(4):
        ok, out = gh(args)
        time.sleep(pace)
        if ok:
            try:
                return json.loads(out)
            except json.JSONDecodeError:
                return None
        if 'rate limit' in out.lower():
            wait_for_limit('search' if path.startswith('search/') else 'core')
            continue
        log('  gh failed:', path, out.strip()[:200])
        return None
    raise RuntimeError('GitHub kept refusing ' + path)


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def safe_name(text):
    return re.sub(r'[^A-Za-z0-9._-]+', '_', text).strip('_')


def search_queries(game):
    # MapPackManagerControl.SearchQueries, as Atlas asks.
    queries = [f'"{game}" poptracker fork:true']
    loose = game
    for roman, digit in (('VIII', '8'), ('VII', '7'), ('VI', '6'), ('IV', '4'), ('V', '5'), ('III', '3'), ('II', '2')):
        loose = re.sub(rf'\b{roman}\b', digit, loose)
    if ':' in loose:
        loose = loose.split(':', 1)[1].strip()
    queries.append(f'"{loose}" poptracker fork:true' if loose and loose.lower() != game.lower() else f'{game} poptracker fork:true')
    return queries


def search_packs(game, module=None):
    # Every query's results together (GitHub's search reads a project's name and description, and "A Short Hike" is
    # "shorthike-archipelago-poptracker": the name without spaces and the world's module name find what the game's name doesn't).
    queries = search_queries(game)
    squeezed = re.sub(r'[^a-z0-9]+', '', game.lower())
    if squeezed and squeezed != game.lower():
        queries.append(f'{squeezed} poptracker fork:true')
    if module and module.lower() != squeezed:
        queries.append(f'{module} poptracker fork:true')
    found = {}
    for query in queries:
        answer = gh_json('search/repositories', {'q': query, 'per_page': '8'}, pace=SEARCH_PACE)
        for i in (answer or {}).get('items') or []:
            found.setdefault(i['full_name'], {'repo': i['full_name'], 'stars': i.get('stargazers_count', 0), 'fork': bool(i.get('fork')), 'pushed': i.get('pushed_at')})
    return sorted(found.values(), key=lambda p: (-p['stars'], p['repo']))


def releases_of(repo):
    answer = gh_json(f'repos/{repo}/releases', {'per_page': '12'})
    return answer if isinstance(answer, list) else []


def first_asset(release, suffix):
    for asset in release.get('assets') or []:
        if (asset.get('name') or '').lower().endswith(suffix):
            return asset
    return None


def download_asset(repo, tag, asset_name, dest):
    tmp = tempfile.mkdtemp(prefix='corpus_')
    try:
        ok, out = gh(['release', 'download', tag, '-R', repo, '--pattern', asset_name, '-D', tmp, '--clobber'])
        time.sleep(CALL_PACE)
        if not ok:
            log('  download failed:', repo, tag, asset_name, out.strip()[:200])
            return False
        got = [os.path.join(tmp, f) for f in os.listdir(tmp)]
        if not got:
            return False
        shutil.move(got[0], dest)
        return True
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def download_zipball(repo, dest):
    ok, err = gh(['api', f'repos/{repo}/zipball'], binary_to=dest)
    time.sleep(CALL_PACE)
    if not ok or os.path.getsize(dest) == 0:
        log('  zipball failed:', repo, err.strip()[:200])
        return False
    return True


def download_url(url, dest):
    if not url.startswith('https://github.com/') and not url.startswith('https://objects.githubusercontent.com/'):
        log('  not a GitHub address, skipped:', url)
        return False
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'AtlasPackLab/1 (+https://github.com/Lt-Tuttle/archipelago-atlas)'}), timeout=120) as r, open(dest, 'wb') as f:
            shutil.copyfileobj(r, f)
        time.sleep(CALL_PACE)
        return True
    except Exception as e:
        log('  download failed:', url, e)
        return False


def is_zip(path):
    try:
        return zipfile.is_zipfile(path)
    except OSError:
        return False


def load_games():
    with open(os.path.join(DATA, 'archipelago_worlds.json'), encoding='utf-8-sig') as f:
        worlds = json.load(f)
    official = []
    for folder, info in worlds.get('worlds', {}).items():
        for game in info.get('games', []):
            official.append({'game': game, 'official': True, 'world': folder})
    with open(os.path.join(DATA, 'apworld_sources.json'), encoding='utf-8-sig') as f:
        sources = json.load(f)
    community = []
    seen = {g['game'].lower() for g in official}
    for entry in sources.get('games', []):
        name = entry.get('game')
        if not name or name.lower() in seen:
            continue
        seen.add(name.lower())
        community.append({'game': name, 'official': False, 'repo': entry.get('repo'), 'versions': entry.get('versions') or [], 'home': entry.get('home'), 'world': entry.get('apworld')})
    return official + community


def main():
    global LOG
    ap = argparse.ArgumentParser()
    ap.add_argument('--corpus', required=True)
    ap.add_argument('--manifest', default=os.path.join(SOURCE, 'Tests', 'corpus_manifest.json'))
    ap.add_argument('--games', nargs='*')
    ap.add_argument('--only', choices=['packs', 'apworlds'])
    ap.add_argument('--limit', type=int, default=0)
    ap.add_argument('--seed-from', help='a folder of packs (and its corpus_manifest.json) copied in first')
    args = ap.parse_args()

    corpus = args.corpus
    packs_dir, apworlds_dir = os.path.join(corpus, 'packs'), os.path.join(corpus, 'apworlds')
    for d in (packs_dir, apworlds_dir):
        os.makedirs(d, exist_ok=True)
    LOG = os.path.join(corpus, 'build_corpus.log')
    state_path = os.path.join(corpus, 'state.json')
    state = json.load(open(state_path, encoding='utf-8')) if os.path.exists(state_path) else {'searched': {}}
    manifest = json.load(open(args.manifest, encoding='utf-8-sig')) if os.path.exists(args.manifest) else {'format': 'atlas-corpus/1', 'built': None, 'files': []}
    files = manifest.setdefault('files', [])
    by_file = {f['file']: f for f in files}

    def save():
        manifest['built'] = date.today().isoformat()
        with open(args.manifest, 'w', encoding='utf-8') as f:
            json.dump(manifest, f, indent=1)
        with open(state_path, 'w', encoding='utf-8') as f:
            json.dump(state, f, indent=1)

    def record(kind, game, repo, release, asset, path):
        entry = {'kind': kind, 'game': game, 'repo': repo, 'release': release, 'asset': asset, 'file': os.path.relpath(path, corpus).replace('\\', '/'),
                 'sha256': sha256_of(path), 'size': os.path.getsize(path), 'fetched': date.today().isoformat()}
        if entry['file'] in by_file:
            files[files.index(by_file[entry['file']])] = entry
        else:
            files.append(entry)
        by_file[entry['file']] = entry

    if args.seed_from:
        seed_manifest = os.path.join(args.seed_from, 'corpus_manifest.json')
        seeds = json.load(open(seed_manifest, encoding='utf-8-sig')) if os.path.exists(seed_manifest) else []
        for s in seeds:
            src = os.path.join(args.seed_from, s['file'])
            if not os.path.exists(src):
                continue
            dest = os.path.join(packs_dir, s['file'])
            if not os.path.exists(dest):
                shutil.copy(src, dest)
            record('pack', s.get('game', ''), s['repo'], s.get('release', ''), s.get('asset', ''), dest)
            log('seeded', s['file'])
        save()

    games = load_games()
    if args.games:
        wanted = {g.lower() for g in args.games}
        games = [g for g in games if g['game'].lower() in wanted]
    if args.limit:
        games = games[:args.limit]
    log(f'== {len(games)} games, corpus {corpus}')

    for n, g in enumerate(games, 1):
        game = g['game']
        log(f'[{n}/{len(games)}] {game}')
        key = game.lower()
        done = state['searched'].setdefault(key, {})
        if args.only != 'apworlds' and 'packs' not in done:
            projects = search_packs(game, g.get('world'))
            done['packs'] = {'date': date.today().isoformat(), 'projects': projects}
            taken = 0
            for p in projects:
                if taken >= MOST_PROJECTS:
                    break
                repo = p['repo']
                releases = [r for r in releases_of(repo) if not r.get('draft')]
                with_zip = [(r, first_asset(r, '.zip')) for r in releases]
                with_zip = [(r, a) for r, a in with_zip if a]
                # A fork without a release of its own is a copy of its original (the originals come first, by stars).
                if not with_zip and p.get('fork'):
                    log('  fork without a release, skipped:', repo)
                    continue
                taken += 1
                if with_zip:
                    release, asset = with_zip[0]
                    tag = release.get('tag_name') or ''
                    dest = os.path.join(packs_dir, safe_name(repo.replace('/', '_') + '_' + tag) + '.zip')
                    if os.path.exists(dest) and os.path.relpath(dest, corpus).replace('\\', '/') in by_file:
                        log('  have', os.path.basename(dest))
                        continue
                    if download_asset(repo, tag, asset['name'], dest) and is_zip(dest):
                        record('pack', game, repo, tag, asset['name'], dest)
                        log('  pack', os.path.basename(dest), f"{os.path.getsize(dest) // 1024} KB")
                    elif os.path.exists(dest):
                        os.remove(dest)
                else:
                    dest = os.path.join(packs_dir, safe_name(repo.replace('/', '_')) + '_current.zip')
                    if os.path.exists(dest) and os.path.relpath(dest, corpus).replace('\\', '/') in by_file:
                        log('  have', os.path.basename(dest))
                        continue
                    if download_zipball(repo, dest) and is_zip(dest):
                        record('pack', game, repo, 'current files', 'zipball', dest)
                        log('  pack (current files)', os.path.basename(dest), f"{os.path.getsize(dest) // 1024} KB")
                    elif os.path.exists(dest):
                        os.remove(dest)
            save()
        if args.only != 'packs' and not g['official'] and 'apworld' not in done:
            repo = g.get('repo')
            got = False
            if repo:
                releases = [r for r in releases_of(repo) if not r.get('draft')]
                with_world = [(r, first_asset(r, '.apworld')) for r in releases]
                with_world = [(r, a) for r, a in with_world if a]
                if with_world:
                    release, asset = with_world[0]
                    tag = release.get('tag_name') or ''
                    dest = os.path.join(apworlds_dir, safe_name(repo.replace('/', '_') + '_' + tag + '_' + asset['name']))
                    if not dest.lower().endswith('.apworld'):
                        dest += '.apworld'
                    if download_asset(repo, tag, asset['name'], dest) and is_zip(dest):
                        record('apworld', game, repo, tag, asset['name'], dest)
                        log('  apworld', os.path.basename(dest), f"{os.path.getsize(dest) // 1024} KB")
                        got = True
                    elif os.path.exists(dest):
                        os.remove(dest)
            if not got and g.get('versions'):
                v = g['versions'][-1]
                url = v.get('url') or ''
                dest = os.path.join(apworlds_dir, safe_name((repo or 'index').replace('/', '_') + '_' + (v.get('version') or '') + '_' + os.path.basename(url)))
                if url and download_url(url, dest) and is_zip(dest):
                    sha = sha256_of(dest)
                    if v.get('sha256') and v['sha256'].lower() != sha:
                        log('  index hash differs, file dropped:', os.path.basename(dest))
                        os.remove(dest)
                    else:
                        record('apworld', game, repo or '', v.get('version') or '', os.path.basename(url), dest)
                        log('  apworld (index)', os.path.basename(dest))
                        got = True
                elif os.path.exists(dest):
                    os.remove(dest)
            done['apworld'] = {'date': date.today().isoformat(), 'got': got}
            save()
    save()
    log(f'== done: {len(files)} files in the manifest')


if __name__ == '__main__':
    main()
