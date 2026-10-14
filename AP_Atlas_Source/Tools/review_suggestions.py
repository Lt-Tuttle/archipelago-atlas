"""Atlas's pack fix suggestions: fetch them from Sentry, then look for the patterns that could become general rules.

Users send the fixes they made by hand from the Pack Doctor ("Suggest them to Atlas"); each arrives on Sentry's
atlas-pack-fixes project as an event with the fixes attached (fixes.json, format atlas-fix-suggestion/1). This tool is for
the developer's own PC; nothing here ships with Atlas.

    python Tools/review_suggestions.py fetch --org lt-tuttle --project atlas-pack-fixes --out "<folder>"
        Saves every suggestion not saved yet as <folder>/<event id>.json. Sentry's free plan keeps events 30 days, so fetch
        at least that often. The token is read from SENTRY_AUTH_TOKEN (a Sentry auth token with project:read and
        event:read); it's never written anywhere.

    python Tools/review_suggestions.py review --from "<folder>" [--min-packs 2] [--out summary.md]
        Groups every "the pack wrote X, the user chose Y" by its shape: the user's choice written as the row's own names
        ({section}, {pin}, {group}) and fixed text, numbers as #. A shape seen in two or more packs is a candidate rule
        (PACK_RULES.md: a rule ships only when it helps two or more packs and the held-out games, and makes no pack worse).
        Also: markers people say aren't checks, by shape, and how often Atlas's own guess was already right.

General by design: the review looks for shapes across packs; no pack's fix is ever shipped as such.
"""
import argparse
import collections
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

SENTRY = 'https://sentry.io'
FORMAT = 'atlas-fix-suggestion/1'


# ---------------------------------------------------------------- fetch

def _get(url, token):
    request = urllib.request.Request(url, headers={'Authorization': 'Bearer ' + token, 'User-Agent': 'atlas-review-suggestions'})
    with urllib.request.urlopen(request, timeout=60) as response:
        body = response.read()
        link = response.headers.get('Link', '')
    time.sleep(1.0)  # polite: one request a second
    return body, link


def _next_page(link):
    # Sentry's Link header: <url>; rel="next"; results="true"; cursor="..."
    for part in link.split(','):
        if 'rel="next"' in part and 'results="true"' in part:
            match = re.search(r'<([^>]+)>', part)
            if match:
                return match.group(1)
    return None


def fetch(org, project, out):
    token = os.environ.get('SENTRY_AUTH_TOKEN', '')
    if not token:
        sys.exit('Set SENTRY_AUTH_TOKEN to a Sentry auth token with project:read and event:read.')
    os.makedirs(out, exist_ok=True)
    have = {name[:-5] for name in os.listdir(out) if name.endswith('.json')}
    base = f'{SENTRY}/api/0/projects/{urllib.parse.quote(org)}/{urllib.parse.quote(project)}'
    url = base + '/events/'
    saved = 0
    while url:
        body, link = _get(url, token)
        for event in json.loads(body):
            event_id = event.get('eventID') or event.get('id')
            if not event_id or event_id in have:
                continue
            attachments, _ = _get(f'{base}/events/{event_id}/attachments/', token)
            for attachment in json.loads(attachments):
                if attachment.get('name') != 'fixes.json':
                    continue
                data, _ = _get(f'{base}/events/{event_id}/attachments/{attachment["id"]}/?download=1', token)
                document = json.loads(data)
                if document.get('format') != FORMAT:
                    print(f'  {event_id}: format {document.get("format")!r}, skipped')
                    continue
                with open(os.path.join(out, event_id + '.json'), 'w', encoding='utf-8') as f:
                    json.dump(document, f, indent=1, ensure_ascii=False)
                saved += 1
                have.add(event_id)
        url = _next_page(link)
    print(f'{saved} new suggestion(s) saved in {out} ({len(have)} in all).')


# ---------------------------------------------------------------- review

def _clean(text):
    return ' '.join((text or '').lower().split())


def _norm(text):
    return ' '.join(re.sub(r'[^0-9a-z]+', ' ', (text or '').lower()).split())


def _split(wrote):
    """'Area/Group/Pin / Section' -> (path parts, section)."""
    if ' / ' in wrote:
        path, section = wrote.rsplit(' / ', 1)
    else:
        path, section = wrote, ''
    return [p.strip() for p in path.split('/') if p.strip()], section.strip()


def shape(wrote, chose):
    """The user's choice as the row's own names and fixed text: '{pin} - {section}', numbers as #."""
    parts, section = _split(wrote)
    target = _clean(chose)
    names = [('{section}', _clean(section)), ('{pin}', _clean(parts[-1]) if parts else ''),
             ('{group}', _clean(parts[-2]) if len(parts) >= 2 else ''), ('{area}', _clean(parts[0]) if len(parts) >= 3 else '')]
    for mark, value in sorted(names, key=lambda x: -len(x[1])):
        if len(value) >= 2 and value in target:
            target = target.replace(value, mark, 1)
    return re.sub(r'\d+', '#', target)


def review(folder, min_packs, out):
    documents = []
    for name in sorted(os.listdir(folder)):
        if name.endswith('.json'):
            with open(os.path.join(folder, name), encoding='utf-8') as f:
                documents.append(json.load(f))
    shapes = collections.defaultdict(lambda: {'packs': set(), 'games': set(), 'count': 0, 'examples': []})
    markers = collections.defaultdict(lambda: {'packs': set(), 'count': 0, 'examples': []})
    guessed = right = 0
    for doc in documents:
        pack = (doc.get('pack') or {}).get('sha256') or (doc.get('pack') or {}).get('name')
        game = doc.get('game')
        for fix in doc.get('fixes', []):
            wrote, chose = fix.get('pack_wrote', ''), fix.get('chose', '')
            if fix.get('atlas_guess') is not None:
                guessed += 1
                if _norm(fix['atlas_guess']) == _norm(chose):
                    right += 1
            if fix.get('kind') != 'location':
                continue
            if chose.startswith('(not a check'):
                _, section = _split(wrote)
                key = re.sub(r'\d+', '#', _clean(section or wrote))
                entry = markers[key]
            else:
                key = shape(wrote, chose)
                entry = shapes[key]
                entry['games'].add(game)
            entry['packs'].add(pack)
            entry['count'] += 1
            if len(entry['examples']) < 3:
                entry['examples'].append(f'{wrote}  ->  {chose}')
    lines = [f'# Pack fix suggestions: {len(documents)} suggestion(s)', '']
    if guessed:
        lines.append(f'Atlas\'s own guess was already right for {right} of {guessed} fixes ({100 * right // guessed}%).')
        lines.append('')
    lines.append(f'## Shapes seen in {min_packs}+ packs (candidate rules)')
    lines.append('')
    lines.append('| Shape | Packs | Fixes | Games | Examples |')
    lines.append('|---|---|---|---|---|')
    for key, entry in sorted(shapes.items(), key=lambda kv: (-len(kv[1]['packs']), -kv[1]['count'])):
        if len(entry['packs']) < min_packs:
            continue
        lines.append(f'| `{key}` | {len(entry["packs"])} | {entry["count"]} | {", ".join(sorted(g for g in entry["games"] if g))} | {"; ".join(entry["examples"])} |')
    lines.append('')
    lines.append(f'## Markers people say aren\'t checks, seen in {min_packs}+ packs')
    lines.append('')
    for key, entry in sorted(markers.items(), key=lambda kv: (-len(kv[1]['packs']), -kv[1]['count'])):
        if len(entry['packs']) >= min_packs:
            lines.append(f'- `{key}`: {len(entry["packs"])} packs, {entry["count"]} sections ({"; ".join(entry["examples"])})')
    text = '\n'.join(lines) + '\n'
    if out:
        with open(out, 'w', encoding='utf-8') as f:
            f.write(text)
        print(f'Written to {out}.')
    else:
        sys.stdout.write(text)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    f = sub.add_parser('fetch')
    f.add_argument('--org', required=True)
    f.add_argument('--project', required=True)
    f.add_argument('--out', required=True)
    r = sub.add_parser('review')
    r.add_argument('--from', dest='folder', required=True)
    r.add_argument('--min-packs', type=int, default=2)
    r.add_argument('--out')
    args = parser.parse_args()
    if args.command == 'fetch':
        fetch(args.org, args.project, args.out)
    else:
        review(args.folder, args.min_packs, args.out)


if __name__ == '__main__':
    main()
