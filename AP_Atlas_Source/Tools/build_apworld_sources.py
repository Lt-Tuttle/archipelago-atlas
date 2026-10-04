"""Builds Atlas's bundled apworld source list from the (archived) community Archipelago-index.

Usage: python Tools/build_apworld_sources.py <Archipelago-index main.zip> <output json>

The index (github.com/Eijebong/Archipelago-index) lists, per apworld, its game name, home page and versions with
download URLs; index.lock pins each version's SHA-256. Atlas verifies every download against that hash and asks the
user before downloading anything. Re-run this when a newer index (or a fork of it) is available.
"""
import json
import re
import sys
import tomllib
import zipfile
from datetime import date

INDEX_RAW = 'https://github.com/Eijebong/Archipelago-index/raw/main/'


def main(index_zip, out_path):
    z = zipfile.ZipFile(index_zip)
    names = z.namelist()
    prefix = names[0].split('/')[0] + '/'
    lock = tomllib.loads(z.read(prefix + 'index.lock').decode('utf-8'))
    games = []
    for name in sorted(n for n in names if n.startswith(prefix + 'index/') and n.endswith('.toml')):
        stem = name.rsplit('/', 1)[1][:-5]
        try:
            entry = tomllib.loads(z.read(name).decode('utf-8'))
        except Exception as e:
            print('skipped', stem, e)
            continue
        default_url = entry.get('default_url')
        versions = []
        for version, src in (entry.get('versions') or {}).items():
            src = src or {}
            url = src.get('url')
            if not url and src.get('local'):
                url = INDEX_RAW + src['local'].replace('../', '')
            if not url and default_url:
                url = default_url.replace('{{version}}', version)
            if not url:
                continue
            versions.append({'version': version, 'url': url, 'sha256': (lock.get(stem) or {}).get(version)})
        if not versions:
            continue
        repos = {m.group(1) for v in versions for m in [re.match(r'https://github\.com/([^/]+/[^/]+)/releases/', v['url'])] if m}
        games.append({
            'game': entry.get('name'),
            'display_name': entry.get('display_name'),
            'apworld': stem,
            'home': entry.get('home'),
            'repo': sorted(repos)[0] if len(repos) == 1 else None,
            'versions': versions,
        })
    out = {'format': 'atlas-apworld-sources/1', 'built': date.today().isoformat(),
           'source': 'github.com/Eijebong/Archipelago-index (archived)', 'games': games}
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1, ensure_ascii=False)
    hashed = sum(1 for g in games for v in g['versions'] if v['sha256'])
    total = sum(len(g['versions']) for g in games)
    print(f'{len(games)} games, {total} versions ({hashed} with SHA-256) -> {out_path}')


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
