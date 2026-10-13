r"""The pack lab's kinds summary: every finding the corpus check made (ATLAS_SELFTEST_PACKS_FINDINGS=<file>), grouped by
kind across packs, with how many packs and rows each touches and a few examples; and the scripts' errors from the run's
output, grouped by message. A kind touching two or more packs is a rule to write; one pack's is the Doctor's to suggest.

Usage: python Tools/corpus_kinds.py <findings.json> [<corpus run output .txt>] [--top N] [--kind <kind>]
"""
import argparse
import json
import re
import sys
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8')

# FindingCatalog.KindOf, without the catalog: the key's first two parts when the second isn't a name.
def kind_of(key):
    parts = key.split(':')
    if len(parts) >= 2 and parts[1] and not parts[1][0].isupper() and len(parts[1]) <= 16 and ' ' not in parts[1]:
        return parts[0] + ':' + parts[1]
    return parts[0]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('findings')
    ap.add_argument('output', nargs='?')
    ap.add_argument('--top', type=int, default=6)
    ap.add_argument('--kind')
    args = ap.parse_args()
    findings = json.load(open(args.findings, encoding='utf-8-sig'))
    by_kind = defaultdict(list)
    for f in findings:
        if f.get('ignored'):
            continue
        by_kind[(kind_of(f['key']), f['severity'])].append(f)
    rows = sorted(by_kind.items(), key=lambda kv: (-len({f['pack'] for f in kv[1]}), -len(kv[1])))
    print(f'{len(findings)} findings in {len({f["pack"] for f in findings})} packs\n')
    print(f'{"kind":28} {"sev":9} {"packs":>5} {"rows":>6}  examples')
    for (kind, sev), items in rows:
        if args.kind and kind != args.kind:
            continue
        packs = {f['pack'] for f in items}
        examples = []
        seen = set()
        for f in items:
            t = f['title'][:90]
            if t in seen:
                continue
            seen.add(t)
            examples.append(f"{f['pack'][:28]}: {t}" + (f" → {f['best']} ({f['score']})" if f.get('best') and f.get('score') else ''))
            if len(examples) >= args.top:
                break
        print(f'{kind:28} {sev:9} {len(packs):5} {len(items):6}')
        for e in examples:
            print(f'{"":45}  {e}')
    if args.output:
        errors = defaultdict(set)
        # The clear handler runs here with an empty slot_data (no slot is connected), so its errors on slot_data's fields
        # are the lab's, not the pack's or Atlas's: the solo batch, with a real seed's data, judges them.
        no_slot = defaultdict(set)
        pack = None
        for line in open(args.output, encoding='utf-8-sig', errors='replace'):
            m = re.match(r'^  ([^ :]+\.zip): scripts ran', line)
            if m:
                pack = m.group(1)
            m = re.match(r'^\s+script error: (.*)$', line)
            if m and pack:
                raw = m.group(1).strip()
                message = re.sub(r'\(\d+,[\d,-]+\)', '(L)', raw)
                key = re.sub(r'^.*?:\(L\): ', '', message)[:120]
                (no_slot if raw.startswith('clear handler:') else errors)[key].add(pack)
        print('\nscript errors by message (packs):')
        for message, packs in sorted(errors.items(), key=lambda kv: -len(kv[1])):
            print(f'{len(packs):4}  {message}')
        print("\nclear handler errors, run without a slot's data (the solo batch judges these, not the lab):")
        for message, packs in sorted(no_slot.items(), key=lambda kv: -len(kv[1])):
            print(f'{len(packs):4}  {message}')


if __name__ == '__main__':
    main()
