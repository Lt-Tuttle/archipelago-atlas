r"""Two logic batches side by side: what an Archipelago update (or any change to the core or the bridge) did to each game.
The batches are batch_logic.py's results. A batch run in parts (--part i/n) is merged first, and the merged file is
written (the parts' name without .pN, or --merged) so a control run can take its seeds. Each game is one of:
  regression     done → failed (yaml, generate or score); exact → differs; a game the old load report has and the new one
                 hasn't, or that the new batch didn't run
  improvement    failed → done; differs → exact; a new game that's done; a game only the new load report has
  informational  late, early or unsure counts that moved on a game that differs on both; a removal the update expects
                 (--expected-removed, by game or world module); a new game that fails; a game the new batch didn't run when
                 no new load report says it should have; an old seed scored on the new core (batch_logic.py --score-old)
                 whose world isn't the one the seed was made with (checksum_match false)
  unchanged      the rest
Each regression says whether the control run (the same seeds and YAMLs on the old core, with the current bridge) shows it
too, how the game's names changed (location and item ids added, removed or renamed), and whether its traceback runs
through Atlas's own code (atlas_bridge.py, atlas_run.py, or the bridge as the lab loads it: UltimateBridge.apworld): its
innermost frame is Atlas's, or an Atlas frame other than the component's entry (atlas_generate, ...) is on the way. A
regression only the new core shows, with Atlas's code in its traceback, blocks.

Usage: python Tools/compare_batches.py [--old <json>] [--new <json> ...] [--control <json> ...] [--names-old <folder>]
           [--names-new <folder>] [--expected-removed <game or module> ...] [--merged <json>] [--out <md>]
Writes the report (--out, else it's printed), <out>.json beside it with every game's class, and <out>.regressions.txt
beside it (the regressions the new batch ran, one game a line, for batch_logic.py --games); none of them may be a file it
reads or merges. With only the names folders it compares the load reports and the name tables. Reads files only; runs
nothing.
"""
import argparse
import glob
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
NOT_GAMES = ('Archipelago', 'Universal Tracker')
# Atlas's own code in a traceback: the bridge's source, the runner, and the bridge as the lab loads it
# (...\UltimateBridge.apworld\UltimateBridge\__init__.py).
OWN_CODE = re.compile(r'atlas_bridge\.py|atlas_run\.py|UltimateBridge', re.I)
FRAME = re.compile(r'^\s*File "([^"]+)", line \d+, in (\S+)', re.M)
# Where every component's traceback starts: the runner, and the component's own function in the bridge
# (atlas_generate, atlas_yaml_template, atlas_seed_test, ...). These frames alone don't make a failure Atlas's.
ENTRY = re.compile(r'<module>|main|atlas_[a-z_]+')
TEXT_FIELDS = ('error', 'trace', 'first_error', 'first_trace', 'score_error', 'reuse_error')
RANK = {'exact': 3, 'differs': 2, 'failed': 1}
CLASSES = ('regression', 'improvement', 'informational', 'unchanged')


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def expand(paths):
    """The files named, a wildcard expanded here (Windows shells pass it as typed); a run's .meta.json isn't results."""
    files = []
    for path in paths or []:
        found = sorted(glob.glob(path)) if any(c in path for c in '*?[') else [path]
        files += [f for f in found if not f.lower().endswith('.meta.json') and f not in files]
    return files


def meta_path(path):
    return os.path.splitext(path)[0] + '.meta.json'


def regressions_path(out):
    """compare.md → compare.regressions.txt: each report its own list, so two compares in one folder keep theirs."""
    return os.path.splitext(out)[0] + '.regressions.txt'


def write_text(path, text):
    # Through a .part file and a rename: a stopped run leaves the old file or the new one, never half of one.
    with open(path + '.part', 'w', encoding='utf-8') as f:
        f.write(text)
    os.replace(path + '.part', path)


def merge(paths):
    """The parts' results as one, the runs their .meta.json files record, and the games more than one part holds (a
    finished entry wins over an unfinished one)."""
    results, runs, twice = {}, [], set()
    for path in paths:
        for game, entry in load(path).items():
            if game in results:
                twice.add(game)
                if results[game].get('finished') or not entry.get('finished'):
                    continue
            results[game] = entry
        if os.path.isfile(meta_path(path)):
            meta = load(meta_path(path))
            runs += meta.get('runs') or [meta]
    return results, runs, sorted(twice)


def merged_name(paths):
    """batch.p1.json, batch.p2.json, ... → batch.json; None when the parts aren't named that way."""
    found = [re.match(r'^(.*)\.p\d+\.json$', p, re.I) for p in paths]
    stems = {m.group(1) for m in found if m}
    return stems.pop() + '.json' if all(found) and len(stems) == 1 else None


def status(entry):
    if not entry or not entry.get('finished'):
        return None
    if entry.get('step') != 'done':
        return 'failed'
    return 'exact' if entry.get('exact') else 'differs'


def label(entry):
    s = status(entry)
    return f"failed ({entry.get('step')})" if s == 'failed' else (s or 'not run')


def load_report(folder):
    if not folder:
        return None
    path = os.path.join(folder, '_load_report.json')
    if not os.path.isfile(path):
        sys.exit(f'no load report: {path}')
    return load(path)


def name_tables(folder):
    """Each game's name table (GameNames' shape) in a names folder, by game."""
    tables = {}
    for name in sorted(os.listdir(folder)) if folder and os.path.isdir(folder) else []:
        if not name.endswith('.json') or name.startswith('_'):
            continue
        try:
            table = load(os.path.join(folder, name))
        except (OSError, ValueError):
            continue
        if isinstance(table, dict) and table.get('Game'):
            tables[table['Game']] = table
    return tables


def id_changes(old, new):
    """Ids added, removed and renamed between two name → id maps, with a few of each."""
    a = {v: k for k, v in (old or {}).items()}
    b = {v: k for k, v in (new or {}).items()}
    added = sorted(set(b) - set(a), key=str)
    removed = sorted(set(a) - set(b), key=str)
    renamed = sorted((i for i in set(a) & set(b) if a[i] != b[i]), key=str)
    return {'added': len(added), 'removed': len(removed), 'renamed': len(renamed),
            'examples': [f'+ {b[i]}' for i in added[:3]] + [f'- {a[i]}' for i in removed[:3]] + [f'{a[i]} → {b[i]}' for i in renamed[:3]]}


def names_diff(game, old_tables, new_tables):
    """How a game's names changed between the two names folders: {} when they didn't, None when a side has no table."""
    if game not in old_tables or game not in new_tables:
        return None
    diff = {kind: id_changes(old_tables[game].get(key), new_tables[game].get(key)) for kind, key in (('locations', 'Locations'), ('items', 'Items'))}
    return diff if any(d['added'] or d['removed'] or d['renamed'] for d in diff.values()) else {}


def names_text(diff):
    if diff is None:
        return 'no name tables to compare'
    if not diff:
        return 'names unchanged'
    parts = []
    for kind, d in diff.items():
        if d['added'] or d['removed'] or d['renamed']:
            parts.append(f"{kind} +{d['added']} -{d['removed']} renamed {d['renamed']}" + (f" ({'; '.join(d['examples'][:3])})" if d['examples'] else ''))
    return ', '.join(parts)


def through_own_code(text):
    """Whether a traceback runs through Atlas's own code: the innermost frame is Atlas's (the bridge itself failed), or an
    Atlas frame other than where every component starts sits on the way. A world's own failure that comes up through
    the component's entry frame isn't Atlas's. The innermost frames survive the bridge's cut (it keeps a trace's end)."""
    frames = FRAME.findall(text or '')
    if not frames:
        return False
    if OWN_CODE.search(frames[-1][0]):
        return True
    return any(OWN_CODE.search(path) and not ENTRY.fullmatch(function) for path, function in frames)


def texts_in(value):
    """Every string in a field, its dicts and lists walked (a seed test's score_error is the start's answer, with the
    tracker's traceback in it): str() of a dict would escape the newlines a frame starts on."""
    if isinstance(value, str):
        return [value]
    if isinstance(value, dict):
        value = list(value.values())
    return [text for item in value for text in texts_in(item)] if isinstance(value, list) else []


def runs_text(runs):
    """What the runs ran on: Archipelago and bridge, each different pair once."""
    seen = []
    for run in runs:
        pair = (run.get('archipelago') or '?', (run.get('bridge_sha256') or '?')[:12])
        if pair not in seen:
            seen.append(pair)
    return '; '.join(f'Archipelago {a}, bridge {b}' for a, b in seen) if seen else 'no .meta.json'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--old', default='', help='the earlier batch (batch_logic.json)')
    ap.add_argument('--new', nargs='+', default=[], help='the new batch, or its parts (merged)')
    ap.add_argument('--control', nargs='+', default=[], help='the control run: the regressions on the old core with the same seeds')
    ap.add_argument('--names-old', default='', help="the old lab's names folder (its _load_report.json and name tables)")
    ap.add_argument('--names-new', default='', help="the new lab's names folder")
    ap.add_argument('--expected-removed', nargs='+', default=[], help='games or world modules the update removes')
    ap.add_argument('--merged', default='', help='where the merged parts go (default: the parts\' name without .pN)')
    ap.add_argument('--out', default='', help='the report (.md); its .json and .regressions.txt go beside it')
    args = ap.parse_args()
    if args.out.lower().endswith('.json'):
        ap.error('--out is the .md report (the JSON goes beside it)')
    if bool(args.old) != bool(args.new):
        ap.error('--old and --new go together')
    if not args.new and not (args.names_old and args.names_new):
        ap.error('give --old and --new, or --names-old and --names-new')

    new_files = expand(args.new)
    old_files = expand([args.old]) if args.old else []
    control_files = expand(args.control)
    # Nothing this writes may be a file it reads (an input, or the meta file beside one).
    inputs = {os.path.abspath(p) for p in new_files + old_files + control_files} | {os.path.abspath(meta_path(p)) for p in new_files + old_files + control_files}
    merged = None
    if len(new_files) > 1:
        merged = args.merged or merged_name(new_files)
        if not merged:
            ap.error('the parts are not named <name>.pN.json: say where the merged batch goes with --merged')
        if inputs & {os.path.abspath(merged), os.path.abspath(meta_path(merged))}:
            ap.error(f'--merged would overwrite an input: {merged}')
    outputs = []
    if args.out:
        out = os.path.abspath(args.out)
        outputs = [out, os.path.splitext(out)[0] + '.json', regressions_path(out)]
        merging = {os.path.abspath(merged), os.path.abspath(meta_path(merged))} if merged else set()
        taken = [p for p in outputs if p in inputs | merging]
        if taken:
            ap.error(f'--out would overwrite what it reads or merges: {", ".join(taken)}')
    new, new_runs, twice = merge(new_files)
    if merged:
        write_text(merged, json.dumps(new, indent=1, ensure_ascii=False))
        write_text(meta_path(merged), json.dumps({'parts': new_files, 'runs': new_runs}, indent=1, ensure_ascii=False))
    old, old_runs, _ = merge(old_files)
    control, control_runs, _ = merge(control_files)
    has_control = bool(control_files)
    old_report, new_report = load_report(args.names_old), load_report(args.names_new)
    old_games = (old_report or {}).get('games') or {}
    new_games = (new_report or {}).get('games') or {}
    old_tables, new_tables = name_tables(args.names_old), name_tables(args.names_new)
    expected = {e.lower() for e in args.expected_removed}
    # An old seed scored on the new core: a game it didn't score had no seed on disk.
    cross = bool(new) and all(e.get('score_old') for e in new.values())
    batches = bool(args.new)

    def module(game):
        return str(old_games.get(game) or new_games.get(game) or '')

    def is_expected(game):
        return game.lower() in expected or module(game).lower() in expected

    def classify(game):
        o, n = old.get(game), new.get(game)
        so, sn = status(o), status(n)
        if not batches or (so is None and sn is None):
            # The load reports only.
            if old_report is not None and new_report is not None and game in old_games and game not in new_games:
                return ('informational', 'removed, as expected') if is_expected(game) else ('regression', 'no longer loads')
            if old_report is not None and new_report is not None and game in new_games and game not in old_games:
                return 'improvement', 'loads now'
            if not batches and names_diff(game, old_tables, new_tables):
                return 'informational', 'names changed'
            return ('unchanged', 'loads on both') if not batches else ('informational', 'in neither batch')
        if sn is None:
            if is_expected(game):
                return 'informational', 'removed, as expected'
            if new_report is not None and game not in new_games and (old_report is None or game in old_games):
                return 'regression', 'no longer loads'
            if cross:
                return 'informational', 'not scored (no seed on disk)'
            if new_report is not None:
                return 'regression', 'not run in the new batch'
            return 'informational', 'not in the new batch'
        if so is None:
            return ('improvement', 'new game') if sn != 'failed' else ('informational', f'new game, {label(n)}')
        reason = f'{label(o)} → {label(n)}'
        if RANK[sn] < RANK[so]:
            if cross and n.get('checksum_match') is False:
                return 'informational', reason + "; the world isn't the one the seed was made with"
            return 'regression', reason
        if RANK[sn] > RANK[so]:
            return 'improvement', reason
        moved = [f'{k} {o.get(k)} → {n.get(k)}' for k in ('late', 'early', 'unsure') if sn == 'differs' and o.get(k) != n.get(k)]
        if moved:
            return 'informational', ', '.join(moved)
        return 'unchanged', label(n)

    def own_code(game):
        """Whether the new run's failure (or the new lab's failed world, by its module) runs through Atlas's own code."""
        entry = new.get(game) or {}
        texts = [text for k in TEXT_FIELDS for text in texts_in(entry.get(k))]
        texts += texts_in(((new_report or {}).get('errors') or {}).get(module(game)))
        return any(through_own_code(t) for t in texts)

    def control_verdict(game):
        if not has_control or status(old.get(game)) is None or status(new.get(game)) is None:
            return None
        c = status(control.get(game))
        if c is None:
            return 'not in the control run'
        return 'the control shows it too' if RANK[c] < RANK[status(old[game])] else 'only on the new core'

    universe = set(old) | set(new) | set(old_games) | set(new_games)
    if not batches:
        universe |= set(old_tables) & set(new_tables)
    rows = {}
    for game in sorted((g for g in universe if g not in NOT_GAMES), key=str.lower):
        kind, reason = classify(game)
        o, n = old.get(game), new.get(game)
        row = {'class': kind, 'reason': reason, 'old': label(o) if o else None, 'new': label(n) if n else None,
               'names': names_diff(game, old_tables, new_tables)}
        if n:
            row.update({k: n.get(k) for k in ('seed_reused', 'yaml_reused', 'reuse_error', 'checksum_match') if k in n})
            for k in ('late', 'early', 'unsure'):
                if (o or {}).get(k) is not None or n.get(k) is not None:
                    row[k] = [(o or {}).get(k), n.get(k)]
        if kind == 'regression':
            row['control'] = control_verdict(game)
            row['own_code'] = own_code(game)
            row['blocks'] = row['own_code'] and row['control'] != 'the control shows it too'
        if is_expected(game) and (game in new or game in new_games):
            row['note'] = 'expected to be removed, still here'
        rows[game] = row

    counts = {k: sum(1 for r in rows.values() if r['class'] == k) for k in CLASSES}
    regressions = [g for g, r in rows.items() if r['class'] == 'regression']
    blocking = [g for g in regressions if rows[g].get('blocks')]
    failed_old = set((old_report or {}).get('failed') or [])
    failed_new = set((new_report or {}).get('failed') or [])

    lines = ['# Batch comparison', '']
    if batches:
        lines.append(f"- Old: {', '.join(old_files)} ({len(old)} games; {runs_text(old_runs)})")
        lines.append(f"- New: {', '.join(new_files)}{' → ' + merged if merged else ''} ({len(new)} games; {runs_text(new_runs)})"
                     + (' (old seeds scored on this core)' if cross else ''))
        if has_control:
            lines.append(f"- Control: {', '.join(control_files)} ({len(control)} games; {runs_text(control_runs)})")
    if args.names_old or args.names_new:
        lines.append(f'- Names: {args.names_old or "-"} → {args.names_new or "-"}')
    if len({(r.get('archipelago'), r.get('bridge_sha256')) for r in new_runs}) > 1:
        lines.append('- **The new batch ran on more than one core or bridge.**')
    if twice:
        lines.append(f"- In more than one part: {', '.join(twice)}")
    lines += ['', '| Class | Games |', '|---|---|'] + [f'| {k} | {counts[k]} |' for k in CLASSES]
    lines += ['', f'## Regressions ({len(regressions)}, {len(blocking)} blocking)', '']
    for game in regressions:
        r = rows[game]
        extra = []
        if r.get('control'):
            extra.append(r['control'])
        if r.get('own_code'):
            extra.append("Atlas's code in the traceback")
        extra.append(names_text(r['names']))
        lines.append(f"- {'**BLOCKS** ' if r.get('blocks') else ''}{game}: {r['reason']}; {'; '.join(extra)}")
    for kind, title in (('improvement', 'Improvements'), ('informational', 'Informational')):
        games = [g for g, r in rows.items() if r['class'] == kind]
        lines += ['', f'## {title} ({len(games)})', '']
        lines += [f"- {g}: {rows[g]['reason']}" + (f"; {rows[g]['note']}" if rows[g].get('note') else '') for g in games]
    if old_report is not None and new_report is not None:
        lines += ['', '## Worlds that failed to load', '']
        lines.append(f"- Only on the new core: {', '.join(sorted(failed_new - failed_old)) or 'none'}")
        lines.append(f"- No longer failing: {', '.join(sorted(failed_old - failed_new)) or 'none'}")
        lines.append(f'- Failing on both: {len(failed_old & failed_new)}')
    changed = [g for g, r in rows.items() if r['names']]
    if old_tables and new_tables:
        lines += ['', f'## Names changed ({len(changed)})', '']
        lines += [f"- {g}: {names_text(rows[g]['names'])}" for g in changed]
    text = '\n'.join(lines) + '\n'

    if not args.out:
        print(text)
        return
    out, report_json, regressions_txt = outputs
    os.makedirs(os.path.dirname(out), exist_ok=True)
    write_text(out, text)
    write_text(report_json, json.dumps({'old': old_files, 'new': new_files, 'merged': merged, 'control': control_files, 'names_old': args.names_old or None,
                                        'names_new': args.names_new or None, 'old_runs': old_runs, 'new_runs': new_runs, 'control_runs': control_runs,
                                        'score_old': cross, 'counts': counts, 'blocking': blocking, 'games': rows}, indent=1, ensure_ascii=False))
    if batches:
        # What a control run can check again: the regressions the new batch ran (one game a line, for batch_logic.py --games).
        write_text(regressions_txt, ''.join(g + '\n' for g in regressions if g in new))
    print(f"{out}: {', '.join(f'{counts[k]} {k}' for k in CLASSES)}; {len(blocking)} blocking")


if __name__ == '__main__':
    main()
