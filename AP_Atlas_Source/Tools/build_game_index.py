r"""Atlas's own game index, built from the pack lab: Scripts/Core/Engine/Data/apworld_sources.json (the list Atlas ships
and "Update game list" fetches) rebuilt from the corpus manifest (every apworld and pack the lab fetched, with its project,
release and SHA-256), the lab's name tables (which worlds load with the engine's Archipelago) and the corpus check's baseline
(how well each pack fits its game). The old list's entries (home links, display names, games the lab didn't fetch) are kept.

Usage: python Tools/build_game_index.py --corpus <folder> [--baseline <packs baseline json>] [--out <json>]
  --corpus    the lab's corpus folder (names/_load_report.json under it)
  --manifest  the corpus manifest (default: Tests/corpus_manifest.json)
  --baseline  a packs baseline the corpus check recorded with every game's names (pack fit per game)
  --out       where to write (default: the bundled list, Scripts/Core/Engine/Data/apworld_sources.json)
A developer tool, run by hand after a lab round; nothing in Atlas runs it.
"""
import argparse
import json
import os
import re
import sys
from datetime import date

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
DATA = os.path.join(PROJECT, 'Scripts', 'Core', 'Engine', 'Data')
FORMAT = 'atlas-apworld-sources/1'
ARCHIPELAGO = '0.6.7'


def version_of(tag, asset):
    """A release tag as a version: "v0.5.1" → "0.5.1", "PSS_v0.0.4" → "0.0.4"; the asset's own version when the tag has none."""
    for text in (tag or '', asset or ''):
        m = re.search(r'(\d+(?:\.\d+)+(?:[-.][A-Za-z0-9.]+)?)', text)
        if m:
            return m.group(1)
    return tag or ''


def asset_url(repo, tag, asset):
    return f'https://github.com/{repo}/releases/download/{tag}/{asset}'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--corpus', required=True)
    ap.add_argument('--manifest', default=os.path.join(PROJECT, 'Tests', 'corpus_manifest.json'))
    ap.add_argument('--baseline')
    ap.add_argument('--out', default=os.path.join(DATA, 'apworld_sources.json'))
    args = ap.parse_args()

    with open(os.path.join(DATA, 'apworld_sources.json'), encoding='utf-8-sig') as f:
        old = json.load(f)
    with open(args.manifest, encoding='utf-8-sig') as f:
        manifest = json.load(f)
    report_path = os.path.join(args.corpus, 'names', '_load_report.json')
    report = json.load(open(report_path, encoding='utf-8-sig')) if os.path.exists(report_path) else {}
    loaded_games = {g.lower() for g in (report.get('games') or {})}
    baseline = json.load(open(args.baseline, encoding='utf-8-sig')) if args.baseline else {}
    with open(os.path.join(DATA, 'archipelago_worlds.json'), encoding='utf-8-sig') as f:
        official = {g.lower() for info in json.load(f).get('worlds', {}).values() for g in info.get('games', [])}

    # Pack fit per pack file, from the corpus check (pin sections linked, of all), by the file's name.
    fit_by_file = {}
    for key, entry in baseline.items():
        if entry.get('SectionsTotal', -1) > 0:
            fit_by_file[key.lower()] = (entry.get('SectionsLinked', 0) + max(0, entry.get('SureLinks', 0)), entry['SectionsTotal'], entry.get('Game') or '')

    games = {g['game'].lower(): dict(g) for g in old.get('games', []) if g.get('game')}
    for g in games.values():
        g.setdefault('versions', [])
    added_games = 0
    for item in manifest.get('files', []):
        kind, game, repo = item.get('kind'), (item.get('game') or '').strip(), item.get('repo') or ''
        if kind == 'apworld':
            if not game or not repo:
                continue
            key = game.lower()
            if key not in games:
                games[key] = {'game': game, 'display_name': None, 'apworld': os.path.splitext(item.get('asset') or '')[0], 'home': f'https://github.com/{repo}', 'repo': repo, 'versions': []}
                added_games += 1
            entry = games[key]
            entry['repo'] = entry.get('repo') or repo
            version = {'version': version_of(item.get('release'), item.get('asset')), 'url': asset_url(repo, item.get('release'), item.get('asset')), 'sha256': item.get('sha256')}
            if not any(v.get('sha256') == version['sha256'] or v.get('url') == version['url'] for v in entry['versions']):
                entry['versions'].insert(0, version)
            if key in loaded_games:
                entry['loads_with'] = ARCHIPELAGO
        elif kind == 'pack':
            # A pack's game comes from the corpus check (the manifest's game is the search's, often blank).
            fit = fit_by_file.get(os.path.basename(item.get('file') or '').lower())
            pack_game = (fit[2] if fit else '') or game
            if not pack_game or not repo:
                continue
            key = pack_game.lower()
            if key not in games:
                if key not in official:
                    continue
                games[key] = {'game': pack_game, 'display_name': None, 'apworld': None, 'home': None, 'repo': None, 'versions': [], 'official': True}
            entry = games[key]
            packs = entry.setdefault('packs', [])
            pack = {'repo': repo, 'release': item.get('release') or '', 'asset': item.get('asset') or '', 'sha256': item.get('sha256')}
            if fit:
                pack['fit'] = round(100.0 * fit[0] / fit[1])
                pack['checks'] = f'{fit[0]} of {fit[1]}'
            if not any(p.get('sha256') == pack['sha256'] for p in packs):
                packs.append(pack)
    for entry in games.values():
        if 'packs' in entry:
            entry['packs'].sort(key=lambda p: -(p.get('fit') or -1))

    out = {'format': FORMAT, 'built': date.today().isoformat(), 'source': 'the Atlas pack lab (github.com/Lt-Tuttle/archipelago-atlas), seeded from Eijebong/Archipelago-index (archived)',
           'archipelago': ARCHIPELAGO, 'games': sorted(games.values(), key=lambda g: g['game'].lower())}
    with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=1, ensure_ascii=False)
        f.write('\n')
    with_packs = sum(1 for g in out['games'] if g.get('packs'))
    loads = sum(1 for g in out['games'] if g.get('loads_with'))
    print(f'wrote {args.out}: {len(out["games"])} games ({added_games} new), {with_packs} with packs, {loads} known to load with Archipelago {ARCHIPELAGO}')


if __name__ == '__main__':
    main()
