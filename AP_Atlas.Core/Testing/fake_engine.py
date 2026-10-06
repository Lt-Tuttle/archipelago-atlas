# The Archipelago Atlas: a fake logic engine, for Atlas's tests only (Testing/FakeLogicEngine.cs sets it up).
#
# It stands in for the UltimateBridge component (Scripts/Core/Engine/Python/atlas_bridge.py) and speaks the same
# protocol: one JSON object per line on stdin and on its own standard output (sys.__stdout__: atlas_run.py sends
# whatever else prints to standard error), each reply carrying its request's id. Like the bridge it hosts several slots,
# each named by its requests' "key" ("drop" forgets one). Instead of rebuilding a real world it
# answers from simple rules: an item pool, the items each location needs, and the items the goal needs. It reads the
# rules again for every request, so a test can change them while the engine runs. It can also misbehave on purpose:
# crash on an item, answer late, fail to load, start a process of its own, leave an item's step out of an answer, or
# send a log line, a late answer to an earlier request and a reply-like line without an id first. Everything it receives, and every crash, goes into a
# journal the test reads. It reads and writes only its own folder.
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
RULES = os.path.join(HERE, 'fake_engine', 'rules.json')
JOURNAL = os.path.join(HERE, 'fake_engine', 'journal.jsonl')


class Component:
    # As Archipelago's worlds.LauncherComponents.Component, where atlas_run.py looks components up.
    def __init__(self, display_name, func):
        self.display_name = display_name
        self.func = func


def read_rules():
    with open(RULES, encoding='utf-8') as f:
        return json.load(f)


def note(entry):
    entry['pid'] = os.getpid()
    with open(JOURNAL, 'a', encoding='utf-8') as f:
        f.write(json.dumps(entry) + '\n')


def crashes_so_far():
    try:
        with open(JOURNAL, encoding='utf-8') as f:
            return sum(1 for line in f if line.strip() and json.loads(line).get('event') == 'crash')
    except FileNotFoundError:
        return 0


def send(reply):
    channel = sys.__stdout__
    channel.write(json.dumps(reply) + '\n')
    channel.flush()


def scribble(text):
    # A line on Atlas's channel that isn't an answer, as code printing there by mistake would write.
    channel = sys.__stdout__
    channel.write(text + '\n')
    channel.flush()


def versions():
    return {'python': sys.version.split()[0], 'ap': 'fake', 'ut': 'fake', 'tested_ut': ['fake']}


def has_all(needs, items):
    return all(item in items for item in needs)


def start(req, rules):
    error = rules.get('start_error')
    if error:
        reply = {'status': 'error', 'game': req.get('game'), 'versions': versions()}
        reply.update(error)
        return reply
    expected = set(req.get('all_locations') or [])
    got = {loc['id'] for loc in rules['locations']}
    if expected:
        check = {'expected': len(expected), 'got': len(got), 'missing': len(expected - got), 'extra': len(got - expected),
                 'match': expected == got}
    else:
        check = {'expected': 0, 'got': len(got), 'missing': 0, 'extra': 0, 'match': None}
    yaml = {'source': 'not_needed', 'apworld_override': None}
    yaml.update(check)
    reply = {'status': 'ready', 'item_pool': rules['pool'], 'yaml': yaml, 'versions': versions()}
    if rules.get('data_checksum'):
        reply['data_checksum'] = rules['data_checksum']
    return reply


def update(items, missing, rules):
    reachable = [loc['id'] for loc in rules['locations'] if loc['id'] in missing and has_all(loc['needs'], items)]
    goal = rules.get('goal')
    return {'reachable': reachable, 'excluded': rules.get('excluded', []),
            'glitched': [loc for loc in rules.get('glitched', []) if loc in missing and loc not in reachable],
            'goal': None if goal is None else has_all(goal, items)}


def steps(req, slot, rules):
    # What each new item opens, in order, as the bridge answers 'steps' (it works out each one as 'update' does).
    if req.get('missing_locations') is not None:
        slot['missing'] = set(req['missing_locations'])
    inventory = list(req.get('base') or [])
    before = set(update(inventory, slot['missing'], rules)['reachable'])
    reply = {'start': sorted(before)} if req.get('start') else {}
    opened = []
    for item in req.get('items') or []:
        inventory.append(item)
        now = set(update(inventory, slot['missing'], rules)['reachable'])
        opened.append(sorted(now - before))
        before = now
    final = update(inventory, slot['missing'], rules)
    slot['items'] = inventory
    if rules.get('short_steps') and opened:
        opened = opened[:-1]  # a broken answer: one item's step missing
    reply.update({'steps': opened, 'excluded': final['excluded'], 'glitched': final['glitched'], 'goal': final['goal']})
    return reply


def explain(req, items, rules):
    # Why a location is (or isn't) in logic with the items of the last update, as the real engine answers.
    names = {item['id']: item['name'] for item in rules['pool']}
    loc = next((l for l in rules['locations'] if l['id'] == req.get('location')), None)
    if loc is None:
        return {'error': 'Location is not part of this world.'}
    if items is None:
        return {'location': loc['name'], 'error': 'The logic engine has not evaluated any items yet.'}
    lacking = [names.get(item, str(item)) for item in loc['needs'] if item not in items]
    out = {'location': loc['name'], 'region': 'Fake Region', 'progress_type': 'DEFAULT',
           'rule': ' and '.join('Has ' + names.get(item, str(item)) for item in loc['needs']) or 'ALWAYS',
           'in_logic': not lacking, 'region_reachable': True, 'glitched': False, 'entrances': []}
    if lacking and req.get('analyze', True):
        out['candidates'] = len(lacking)
        out['single_unlocks'] = lacking if len(lacking) == 1 else []
        if len(lacking) > 1:
            out['required'] = lacking
        out['partial'] = False
    return out


def serve(*args):
    note({'event': 'start'})
    # As worlds do while they load: printed, so it goes to standard error (atlas_run.py keeps the channel for answers).
    print('Fake engine: printed as a world loads')
    rules = read_rules()
    if rules.get('boot_error'):
        # As the real bridge does when the tracker can't be imported: it says so, and ends.
        send({'event': 'boot_failed', 'error': rules['boot_error'], 'trace': 'Traceback: ' + rules['boot_error']})
        return
    if rules.get('spawn_child'):
        # A process of its own (as a world could start one): stopping the engine must end it too.
        child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(600)'],
                                 stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        note({'event': 'child', 'child_pid': child.pid})
    slots = {}  # by key: the slot's missing locations and the items of its last update
    while True:
        line = sys.stdin.readline()
        if not line:
            break  # Atlas closed the pipe: it stopped the engine
        if not line.strip():
            continue
        req = json.loads(line)
        rid = req.get('id')
        action = req.get('action')
        note({'request': req})
        rules = read_rules()
        # Working on it first (a slow world crashes after a while, not at once).
        delay = rules.get('delays', {}).get(action, 0)
        if delay:
            time.sleep(delay)
        crash = rules.get('crash')
        if (crash and action in ('update', 'steps') and crash['on_item'] in (req.get('items') or []) + (req.get('base') or [])
                and crashes_so_far() < crash.get('times', 1)):
            note({'event': 'crash', 'id': rid})
            os._exit(3)
        if rules.get('chatter') and rid is not None:
            scribble('Fake engine: thinking about request ' + str(rid))
            send({'id': rid - 1, 'reachable': [], 'excluded': [], 'glitched': [], 'goal': None})
            scribble(json.dumps({'reachable': [], 'excluded': [], 'glitched': [], 'goal': None}))
        key = req.get('key')
        if action == 'init':
            reply = start(req, rules)
            if reply.get('status') == 'ready':
                slots[key] = {'missing': set(req.get('all_locations') or [loc['id'] for loc in rules['locations']]), 'items': None}
            else:
                slots.pop(key, None)
        elif action == 'drop':
            slots.pop(key, None)
            reply = {'dropped': True}
        elif action in ('update', 'steps', 'explain') and key not in slots:
            reply = {'error': 'The logic engine has not been started for a slot yet.'}
        elif action == 'update':
            slot = slots[key]
            if req.get('missing_locations') is not None:
                slot['missing'] = set(req['missing_locations'])
            slot['items'] = list(req.get('items') or [])
            reply = update(slot['items'], slot['missing'], rules)
        elif action == 'steps':
            reply = steps(req, slots[key], rules)
        elif action == 'explain':
            reply = explain(req, slots[key]['items'], rules)
        else:
            reply = {'error': 'Unknown action: ' + str(action)}
        reply['id'] = rid
        send(reply)


components = [Component('UltimateBridge', serve)]
