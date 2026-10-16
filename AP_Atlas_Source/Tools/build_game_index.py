r"""Atlas's own game index, built from the pack lab: Scripts/Core/Engine/Data/apworld_sources.json (the list Atlas ships
and "Update game list" fetches) rebuilt from the corpus manifest (every apworld and pack the lab fetched, with its project,
release and SHA-256), the lab's name tables (which worlds load with the engine's Archipelago) and the corpus check's baseline
(how well each pack fits its game). The old list's entries (home links, display names, games the lab didn't fetch) are kept.

Usage: python Tools/build_game_index.py --corpus <folder> [--baseline <packs baseline json>] [--findings <json>] [--out <json>]
  --corpus    the lab's corpus folder (names/_load_report.json and names/*.json under it, the packs in packs/)
  --manifest  the corpus manifest (default: Tests/corpus_manifest.json)
  --baseline  a packs baseline the corpus check recorded with every game's names (pack fit per game)
  --findings  the corpus check's findings (ATLAS_SELFTEST_PACKS_FINDINGS) for the packs' notes
  --out       where to write (default: the bundled list, Scripts/Core/Engine/Data/apworld_sources.json)
A developer tool, run by hand after a lab round; nothing in Atlas runs it.

Each pack carries what the lab measured: "fit" and "checks" (its pins linked to the game's checks, refreshed every build), "size" (bytes, as the lab downloaded it),
"notes" (short codes Atlas turns into sentences: other_version, other_game, scripts_stop, no_pictures). A pack whose mapping
holds another game's ids is filed twice: under its manifest's game with "other_game" and "for_game", and under the game its
ids are for with "named_game" (unmeasured there).

Every version and pack the corpus holds gets "size": the bytes of the file the lab downloaded (the corpus copy, else the
manifest's record), matched by SHA-256, so Atlas can say what adding a game downloads without asking GitHub. A project's
current files (asset "zipball", which GitHub lists without a size) are measured the same way. A version only the old list
knows keeps the size it had, or has none.
"""
import argparse
import json
import os
import re
import sys
import zipfile
from datetime import date

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
DATA = os.path.join(PROJECT, 'Scripts', 'Core', 'Engine', 'Data')
FORMAT = 'atlas-apworld-sources/1'
ARCHIPELAGO = '0.6.7'
# A pack is for another game when this share of its mapping's ids are that game's and few are its own.
FOR_GAME_SHARE = 0.9
OWN_GAME_SHARE = 0.1
FOR_GAME_MIN_IDS = 20
# Another version of the apworld: this share of the mapping's ids aren't the game's, and the pack fits less than this.
OTHER_VERSION_SHARE = 0.5
OTHER_VERSION_FIT = 60
# Some maps have no picture: this share of the pack's maps.
NO_PICTURES_SHARE = 0.25
MAPPING_KEY = re.compile(r'^\s*\[\s*(0x[0-9A-Fa-f]+|\d+)\s*\]\s*=\s*\{+\s*"([^"]*)"', re.M)
# The game it's for: its ids also cover this share of that game's locations, and this share of the paths read as their names.
FOR_GAME_COVER = 0.5
FOR_GAME_NAMES = 0.25


def version_of(tag, asset):
    """A release tag as a version: "v0.5.1" → "0.5.1", "PSS_v0.0.4" → "0.0.4"; the asset's own version when the tag has none."""
    for text in (tag or '', asset or ''):
        m = re.search(r'(\d+(?:\.\d+)+(?:[-.][A-Za-z0-9.]+)?)', text)
        if m:
            return m.group(1)
    return tag or ''


def asset_url(repo, tag, asset):
    return f'https://github.com/{repo}/releases/download/{tag}/{asset}'


def loose(text):
    return re.sub(r'0+(\d)', r'\1', ''.join(c.lower() for c in text or '' if c.isalnum()))


def corpus_sizes(corpus, manifest):
    """Each corpus file's size in bytes, by its SHA-256 (lowercase): the copy on disk, else the manifest's record. A copy
    whose size differs from the record is left out (said), since it isn't the file the record describes."""
    sizes = {}
    for item in manifest.get('files', []):
        sha = (item.get('sha256') or '').lower()
        if not sha:
            continue
        name = item.get('file') or ''
        recorded = item.get('size') if isinstance(item.get('size'), int) and item.get('size') > 0 else None
        path = os.path.join(corpus, name)
        on_disk = os.path.getsize(path) if name and os.path.isfile(path) else None
        if on_disk is not None and recorded is not None and on_disk != recorded:
            print(f'{name}: {on_disk} bytes on disk, {recorded} in the manifest; no size recorded for it')
            continue
        size = on_disk or recorded
        if size:
            sizes[sha] = size
    return sizes


def mapping_ids(zip_path):
    """The ids a pack's location mapping is keyed by (plain or hexadecimal numbers) with each one's first path, read
    without running anything."""
    ids = {}
    try:
        with zipfile.ZipFile(zip_path) as z:
            for name in z.namelist():
                if 'location_mapping' in name.lower() and name.lower().endswith('.lua'):
                    text = z.read(name).decode('utf-8', 'replace')
                    for m in MAPPING_KEY.finditer(text):
                        key = int(m.group(1), 16) if m.group(1).lower().startswith('0x') else int(m.group(1))
                        ids.setdefault(key, m.group(2))
    except (OSError, zipfile.BadZipFile):
        pass
    return ids


def load_names(corpus):
    """Every game's location ids, from the lab's name tables."""
    tables = {}
    folder = os.path.join(corpus, 'names')
    if not os.path.isdir(folder):
        return tables
    for name in os.listdir(folder):
        if not name.endswith('.json') or name.startswith('_'):
            continue
        try:
            table = json.load(open(os.path.join(folder, name), encoding='utf-8-sig'))
        except (OSError, ValueError):
            continue
        game = table.get('Game')
        locations = table.get('Locations') or {}
        if game and locations:
            tables[game] = {v: k for k, v in locations.items()}
    return tables


def pack_facts(pack_file, game, corpus, names, findings_by_pack, baseline_entry):
    """(game it's for, notes) for one pack file."""
    notes = []
    for_game = game
    rows = findings_by_pack.get(pack_file.lower(), [])
    paths = mapping_ids(os.path.join(corpus, 'packs', pack_file)) if names else {}
    ids = set(paths)
    own = names.get(game) if game else None
    if len(ids) >= FOR_GAME_MIN_IDS and (own is None or len(ids & own.keys()) < len(ids) * OWN_GAME_SHARE):
        best, best_hits = None, 0
        for other, other_names in names.items():
            if other == game:
                continue
            hits = len(ids & other_names.keys())
            if hits > best_hits:
                best, best_hits = other, hits
        if best and best_hits >= len(ids) * FOR_GAME_SHARE and best_hits >= len(names[best]) * FOR_GAME_COVER:
            # Numbers alone can match another game numbered the same way (small ids, offsets): the paths must read as its names.
            agree = 0
            for key in ids & names[best].keys():
                name = loose(names[best][key])
                segments = [loose(s) for s in paths[key].lstrip('@').split('/')[-2:]]
                if any(len(s) >= 3 and (s in name or name in s) for s in segments):
                    agree += 1
            if agree >= best_hits * FOR_GAME_NAMES:
                for_game = best
    if for_game == game:
        stale = next((r for r in rows if r.get('key') == 'locmap:stale'), None)
        m = re.search(r'lists (\d+) ids', (stale or {}).get('title', ''))
        fit = -1
        if baseline_entry and baseline_entry.get('SectionsTotal', -1) > 0:
            fit = 100.0 * (baseline_entry.get('SectionsLinked', 0) + max(0, baseline_entry.get('SureLinks', 0))) / baseline_entry['SectionsTotal']
        if m and ids and int(m.group(1)) >= len(ids) * OTHER_VERSION_SHARE and 0 <= fit < OTHER_VERSION_FIT:
            notes.append('other_version')
    if baseline_entry and baseline_entry.get('ScriptsStopped'):
        notes.append('scripts_stop')
    maps = (baseline_entry or {}).get('Maps', 0) or 0
    no_picture = sum(1 for r in rows if str(r.get('key', '')).startswith('map:nobg'))
    if maps > 0 and no_picture >= maps * NO_PICTURES_SHARE:
        notes.append('no_pictures')
    return for_game, notes


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--corpus', required=True)
    ap.add_argument('--manifest', default=os.path.join(PROJECT, 'Tests', 'corpus_manifest.json'))
    ap.add_argument('--baseline')
    ap.add_argument('--findings')
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
    findings = json.load(open(args.findings, encoding='utf-8-sig')) if args.findings else []
    findings_by_pack = {}
    for row in findings:
        findings_by_pack.setdefault(str(row.get('pack', '')).lower(), []).append(row)
    names = load_names(args.corpus)
    with open(os.path.join(DATA, 'archipelago_worlds.json'), encoding='utf-8-sig') as f:
        official = {g.lower() for info in json.load(f).get('worlds', {}).values() for g in info.get('games', [])}
    official |= {g.lower() for g in names}

    # Pack fit per pack file, from the corpus check (pin sections linked, of all), by the file's name.
    fit_by_file = {}
    baseline_by_file = {key.lower(): entry for key, entry in baseline.items()}
    for key, entry in baseline.items():
        if entry.get('SectionsTotal', -1) > 0:
            fit_by_file[key.lower()] = (entry.get('SectionsLinked', 0) + max(0, entry.get('SureLinks', 0)), entry['SectionsTotal'], entry.get('Game') or '')

    games = {g['game'].lower(): dict(g) for g in old.get('games', []) if g.get('game')}
    for g in games.values():
        g.setdefault('versions', [])
        # Packs are rebuilt from the lab every time (fit, notes and the game they're for can all change between rounds).
        g.pop('packs', None)
    added_games = 0
    filed_elsewhere = 0
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
            pack_file = os.path.basename(item.get('file') or '')
            fit = fit_by_file.get(pack_file.lower())
            base_entry = baseline_by_file.get(pack_file.lower())
            pack_game = (fit[2] if fit else '') or ((base_entry or {}).get('Game') or '') or game
            if not pack_game or not repo:
                continue
            for_game, notes = pack_facts(pack_file, pack_game, args.corpus, names, findings_by_pack, base_entry)

            def file_under(game_name, extra):
                key = game_name.lower()
                if key not in games:
                    if key not in official:
                        return False
                    games[key] = {'game': game_name, 'display_name': None, 'apworld': None, 'home': None, 'repo': None, 'versions': [], 'official': True}
                packs = games[key].setdefault('packs', [])
                pack = {'repo': repo, 'release': item.get('release') or '', 'asset': item.get('asset') or '', 'sha256': item.get('sha256')}
                pack.update(extra)
                if not any(p.get('sha256') == pack['sha256'] for p in packs):
                    packs.append(pack)
                return True

            measured = {'fit': round(100.0 * fit[0] / fit[1]), 'checks': f'{fit[0]} of {fit[1]}'} if fit else {}
            if for_game == pack_game:
                file_under(pack_game, dict(measured, **({'notes': notes} if notes else {})))
            else:
                # Its mapping holds another game's ids: filed under its manifest's game with that said, and under the game
                # its ids are for, unmeasured there (Atlas measures a release against the game's names before keeping it).
                file_under(pack_game, dict(measured, notes=notes + ['other_game'], for_game=for_game))
                if file_under(for_game, dict({'named_game': pack_game}, **({'notes': notes} if notes else {}))):
                    filed_elsewhere += 1
    # Sizes by SHA-256 (the file's identity): every listing of the same file gets it, including old-list versions.
    sizes = corpus_sizes(args.corpus, manifest)
    for entry in games.values():
        for listed in entry.get('versions', []) + entry.get('packs', []):
            size = sizes.get((listed.get('sha256') or '').lower())
            if size:
                listed['size'] = size
    for entry in games.values():
        if 'packs' in entry:
            entry['packs'].sort(key=lambda p: -(p.get('fit') or -1))

    out = {'format': FORMAT, 'built': date.today().isoformat(), 'source': 'the Atlas pack lab (github.com/Lt-Tuttle/archipelago-atlas), seeded from Eijebong/Archipelago-index (archived)',
           'archipelago': ARCHIPELAGO, 'games': sorted(games.values(), key=lambda g: g['game'].lower())}
    with open(args.out, 'w', encoding='utf-8', newline='\r\n') as f:
        json.dump(out, f, indent=1, ensure_ascii=False)
        f.write('\n')
    with_packs = sum(1 for g in out['games'] if g.get('packs'))
    loads = sum(1 for g in out['games'] if g.get('loads_with'))
    noted = sum(1 for g in out['games'] for p in g.get('packs', []) if p.get('notes'))
    sized = sum(1 for g in out['games'] for v in g.get('versions', []) + g.get('packs', []) if v.get('size'))
    listed = sum(len(g.get('versions', [])) + len(g.get('packs', [])) for g in out['games'])
    print(f'wrote {args.out}: {len(out["games"])} games ({added_games} new), {with_packs} with packs, {loads} known to load with Archipelago {ARCHIPELAGO}; '
          f'{noted} packs with notes, {filed_elsewhere} filed under the game they are for; {sized} of {listed} versions and packs with their size')


if __name__ == '__main__':
    main()
