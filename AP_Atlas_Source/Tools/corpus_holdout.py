r"""The pack lab's holdout: a quarter of the corpus's games, chosen once by a fixed seed, whose packs nobody looks at while a
general rule or a fixing tool is designed. A rule ships only when it also helps the holdout (or leaves it unchanged), and
the guided tools are proven by fixing held-out packs cold. Split by game, so nothing about a held-out game leaks into a rule.

Usage: python Tools/corpus_holdout.py --corpus <folder> --baseline <packs baseline json> [--write] [--link]
  --baseline  a corpus check baseline recorded with the games' names (each pack's game)
  --write     writes Tests/corpus_holdout.json (refused when it exists: the split is made once)
  --link      makes <corpus>\split\design and <corpus>\split\holdout with hard links to the packs (no copies)
A developer tool; nothing in Atlas runs it. Packs the corpus check couldn't name a game for stay on the design side.
"""
import argparse
import json
import os
import random
import sys

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
SPLIT_FILE = os.path.join(PROJECT, 'Tests', 'corpus_holdout.json')
SEED = 20261013
SHARE = 0.25


def split(baseline):
    by_game = {}
    for pack, entry in baseline.items():
        game = (entry or {}).get('Game')
        if game:
            by_game.setdefault(game, []).append(pack)
    games = sorted(by_game, key=str.lower)
    rng = random.Random(SEED)
    held = sorted(rng.sample(games, round(len(games) * SHARE)), key=str.lower)
    packs = sorted(p for g in held for p in by_game[g])
    return held, packs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--corpus', required=True)
    ap.add_argument('--baseline', required=True)
    ap.add_argument('--write', action='store_true')
    ap.add_argument('--link', action='store_true')
    args = ap.parse_args()
    baseline = json.load(open(args.baseline, encoding='utf-8-sig'))
    if os.path.exists(SPLIT_FILE):
        recorded = json.load(open(SPLIT_FILE, encoding='utf-8-sig'))
        held, packs = recorded['games'], recorded['packs']
        if args.write:
            print(f'{SPLIT_FILE} exists: the split is made once and kept.')
    else:
        held, packs = split(baseline)
        if args.write:
            with open(SPLIT_FILE, 'w', encoding='utf-8', newline='\r\n') as f:
                json.dump({'about': 'The pack lab holdout (Tools/corpus_holdout.py): games whose packs are not looked at while rules and tools are designed.',
                           'seed': SEED, 'share': SHARE, 'games': held, 'packs': packs}, f, indent=1, ensure_ascii=False)
                f.write('\n')
            print('wrote', SPLIT_FILE)
    print(f'{len(held)} games held out ({len(packs)} packs) of {len({e.get("Game") for e in baseline.values() if e.get("Game")})} named games')
    if args.link:
        source = os.path.join(args.corpus, 'packs')
        held_set = set(packs)
        for side in ('design', 'holdout'):
            os.makedirs(os.path.join(args.corpus, 'split', side), exist_ok=True)
        made = 0
        for name in os.listdir(source):
            if not name.lower().endswith('.zip'):
                continue
            target = os.path.join(args.corpus, 'split', 'holdout' if name in held_set else 'design', name)
            if not os.path.exists(target):
                os.link(os.path.join(source, name), target)
                made += 1
        print(f'linked {made} packs into {os.path.join(args.corpus, "split")}')


if __name__ == '__main__':
    main()
