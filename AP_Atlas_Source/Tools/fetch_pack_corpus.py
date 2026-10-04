"""Downloads a test corpus of community PopTracker map packs for Atlas's self-test (ATLAS_SELFTEST_PACKS).

Usage: python Tools/fetch_pack_corpus.py <output folder> [count=40]

Takes the most-starred GitHub repositories matching "poptracker archipelago", and saves each one's latest release
.zip (packs are distributed as release zips). Set GITHUB_TOKEN to avoid GitHub's low anonymous rate limits.
Writes corpus_manifest.json with each pack's repository, release and SHA-256, so a run is reproducible.
"""
import hashlib
import json
import os
import sys
import time
import urllib.request


def get(url):
    req = urllib.request.Request(url, headers={'User-Agent': 'AP-Atlas-PackCorpus', 'Accept': 'application/vnd.github+json'})
    token = os.environ.get('GITHUB_TOKEN')
    if token:
        req.add_header('Authorization', 'Bearer ' + token)
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def main(out_dir, count):
    os.makedirs(out_dir, exist_ok=True)
    repos = []
    page = 1
    while len(repos) < count * 2 and page <= 5:
        data = json.loads(get(f'https://api.github.com/search/repositories?q=poptracker+archipelago+in:name,description&sort=stars&per_page=50&page={page}'))
        repos += [r['full_name'] for r in data.get('items', [])]
        if not data.get('items'):
            break
        page += 1
    manifest = []
    for repo in repos:
        if len(manifest) >= count:
            break
        try:
            release = json.loads(get(f'https://api.github.com/repos/{repo}/releases/latest'))
        except Exception:
            continue  # no releases
        asset = next((a for a in release.get('assets', []) if a['name'].lower().endswith('.zip')), None)
        if asset is None:
            continue
        name = repo.replace('/', '_') + '.zip'
        path = os.path.join(out_dir, name)
        try:
            data = get(asset['browser_download_url'])
        except Exception as e:
            print('skipped', repo, e)
            continue
        with open(path, 'wb') as f:
            f.write(data)
        manifest.append({'repo': repo, 'release': release.get('tag_name'), 'asset': asset['name'], 'file': name,
                         'sha256': hashlib.sha256(data).hexdigest()})
        print(f'{len(manifest):3} {repo} {release.get("tag_name")} ({len(data) // 1024} KB)')
        time.sleep(0.3)
    with open(os.path.join(out_dir, 'corpus_manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=1)
    print(f'{len(manifest)} packs saved to {out_dir}')


if __name__ == '__main__':
    main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 40)
