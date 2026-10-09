"""Compares two sets of solo test reports (Games -> Test this game...) game by game, to see what a change to Atlas fixed
or broke between two test waves.

Usage: python Tools/solo_diff.py <older reports folder or .json> <newer reports folder or .json>

Reads only the JSON twins it's given (PortableData/reports/solo-tests/<Game>-<date>.json; for a folder, each game's
newest). Prints, per game, the steps' outcomes, the logic verdict, sphere 0 against the live logic, the pins and tiles
linked and the scripts' errors, with what moved between the two. Writes nothing.
"""
import glob
import json
import os
import sys


def newest_per_game(path):
    """The newest report of each game in a folder (or the one file given), keyed by game."""
    files = [path] if os.path.isfile(path) else sorted(glob.glob(os.path.join(path, '*.json')))
    reports = {}
    for file in files:
        try:
            with open(file, encoding='utf-8-sig') as f:
                data = json.load(f)
        except (OSError, ValueError) as e:
            print(f'skipped {os.path.basename(file)}: {e}', file=sys.stderr)
            continue
        game = data.get('Game')
        if not game:
            continue
        started = data.get('StartedLocal') or ''
        if game not in reports or started > (reports[game].get('StartedLocal') or ''):
            reports[game] = data
    return reports


STEPS = ['Apworld', 'MapPack', 'Yaml', 'Generate', 'Host', 'Connect', 'Score', 'Report']
OUTCOMES = ['Pending', 'Running', 'Done', 'Skipped', 'Failed']


def name(value, names):
    """An enum as the report wrote it: its name, or (older reports) its number."""
    return names[value] if isinstance(value, int) and 0 <= value < len(names) else str(value)


def pct(part, whole):
    return 'n/a' if not whole else f'{100 * part // whole}%'


def facts(report):
    """The numbers worth comparing, as (label, value) pairs; a missing part reads 'not scored'."""
    out = []
    steps = report.get('Steps') or []
    out.append(('steps', ' '.join(f"{name(s.get('Step'), STEPS)}:{name(s.get('Outcome'), OUTCOMES)}" for s in steps) or 'none'))
    logic = report.get('Logic')
    if logic:
        out.append(('logic', 'exact' if logic.get('Exact') else f"differs (late {logic.get('Late')}, early {logic.get('Early')})"))
        out.append(('sphere 0 vs live', f"{len(logic.get('NotReached') or [])} not reached, {len(logic.get('BeyondSphere0') or [])} beyond"))
        if logic.get('ReachableAtConnect') is not None:
            out.append(('reachable at connect', f"{logic.get('ReachableAtConnect')} of {logic.get('TotalLocations')}"))
    else:
        out.append(('logic', 'not scored'))
    pins = report.get('Pins')
    out.append(('locations on a map', f"{pins.get('LocationsPlaced')} of {pins.get('LocationsTotal')} ({pct(pins.get('LocationsPlaced', 0), pins.get('LocationsTotal', 0))})" if pins else 'not scored'))
    out.append(('pin sections linked', f"{pins.get('SectionsLinked')} of {pins.get('SectionsTotal')}" if pins else 'not scored'))
    tiles = report.get('KeyItems')
    out.append(('tiles linked', f"{tiles.get('TilesLinked')} of {tiles.get('TilesTotal')}" if tiles else 'not scored'))
    scripts = report.get('Scripts')
    out.append(('script errors', str(len(scripts.get('Errors') or [])) if scripts else 'not scored'))
    return out


def main(older, newer):
    before, after = newest_per_game(older), newest_per_game(newer)
    for game in sorted(set(before) | set(after)):
        print(f'== {game}')
        if game not in before:
            print('   new in the second set')
        if game not in after:
            print('   missing from the second set')
            continue
        old = dict(facts(before[game])) if game in before else {}
        for label, value in facts(after[game]):
            was = old.get(label)
            mark = '' if was is None or was == value else f'   (was {was})'
            print(f'   {label}: {value}{mark}')
    return 0


if __name__ == '__main__':
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1], sys.argv[2]))
