# The Archipelago Atlas logic bridge. Installed as the UltimateBridge apworld; Atlas runs its components
# (UltimateBridge, AtlasNames, AtlasCheck) and talks to them as one JSON object per line over stdin/stdout.
import os
import re
import sys
import json
import shutil
import logging
import tempfile
import traceback

# Never let Archipelago pip-install packages or prompt on stdin from inside the bridge: stdin is our protocol.
try:
    import ModuleUpdate
    ModuleUpdate.update_ran = True
except Exception:
    pass

# Universal Tracker versions this bridge is tested with (Atlas warns about others).
TESTED_UT_VERSIONS = ['v0.3.4']


def send(message):
    # Atlas's channel: this process's own standard output. Only answers go there; anything else that prints goes to
    # standard error (protect_channel), so it can never be taken for an answer.
    channel = sys.__stdout__
    channel.write(json.dumps(message) + '\n')
    channel.flush()


def protect_channel():
    # From here on, whatever prints (a world, the tracker) goes to standard error, which Atlas logs. atlas_run.py does
    # this before any world loads; an Archipelago install's launcher doesn't, so the bridge does it too.
    sys.stdout = sys.stderr


def note(text):
    # A line for Atlas's log about something that didn't work but didn't stop the request.
    sys.stderr.write('[bridge] ' + text + '\n')
    sys.stderr.flush()


_noted = set()


def note_once(text):
    # As note(), for something that would otherwise be said on every request.
    if text not in _noted:
        _noted.add(text)
        note(text)


def versions():
    out = {'python': sys.version.split()[0], 'tested_ut': TESTED_UT_VERSIONS}
    try:
        import Utils
        out['ap'] = Utils.__version__
    except Exception as e:
        note("Archipelago's version could not be read: " + type(e).__name__ + ': ' + str(e))
    try:
        from worlds.tracker import UT_VERSION
        out['ut'] = UT_VERSION
    except Exception as e:
        note("The Universal Tracker's version could not be read: " + type(e).__name__ + ': ' + str(e))
    return out


def ensure_players_folder():
    # The Universal Tracker opens a folder picker (from a hidden process) when its YAML folder is missing.
    try:
        import yaml
        import Utils
        path = 'Players'
        host = Utils.user_path('host.yaml')
        if os.path.isfile(host):
            with open(host, encoding='utf-8-sig') as f:
                data = yaml.safe_load(f) or {}
            path = (data.get('universal_tracker') or {}).get('player_files_path') or path
        full = path if os.path.isabs(path) else Utils.user_path(path)
        os.makedirs(full, exist_ok=True)
        return full
    except Exception as e:
        note("The engine's Players folder could not be set up: " + type(e).__name__ + ': ' + str(e))
        return None


def name_matches(yaml_name, slot_name):
    # YAML names may hold placeholders ({player}, {number}) that generation filled in.
    if not yaml_name or not slot_name:
        return False
    if yaml_name.lower() == slot_name.lower():
        return True
    if '{' not in yaml_name:
        return False
    pattern = '^' + re.sub(r'\\\{[A-Za-z]+\\\}', '.*', re.escape(yaml_name)) + '$'
    return re.match(pattern, slot_name, re.IGNORECASE) is not None


def plays_game(doc, game):
    g = doc.get('game')
    if isinstance(g, str):
        return g.lower() == game.lower()
    if isinstance(g, dict):
        return any(str(k).lower() == game.lower() and str(v) != '0' for k, v in g.items())
    return False


def load_yaml_docs(path):
    import yaml
    with open(path, encoding='utf-8-sig') as f:
        return [d for d in yaml.safe_load_all(f) if isinstance(d, dict)]


def pick_doc(docs, game, slot_name, require_name):
    for_game = [d for d in docs if plays_game(d, game)]
    named = [d for d in for_game if name_matches(str(d.get('name', '')), slot_name)]
    if named:
        return named[0]
    if not require_name and len(for_game) == 1:
        return for_game[0]
    return None


def resolved_options(cls, slot_data):
    # Option values the world put in its slot data. These are what the seed actually rolled, so they beat a YAML
    # (which may say 'random'). Worlds keep them under 'options' or at the top level.
    hints = cls.options_dataclass.type_hints
    sources = []
    if isinstance(slot_data.get('options'), dict):
        sources.append(slot_data['options'])
    sources.append(slot_data)
    found = {}
    for src in sources:
        for key, value in src.items():
            if key in hints and key not in found:
                try:
                    hints[key].from_any(value)
                    json.dumps(value)
                    found[key] = value
                except Exception:
                    pass
    return found, len(hints)


# The Universal Tracker functions the bridge calls. A tracker update that renames or drops one is caught by the
# health check (and refused at slot start) instead of failing in confusing ways mid-session.
REQUIRED_UT_API = ['set_slot_params', 'run_generator', 'initalize_tracker_core', 'updateTracker',
                   'set_missing_locations', 'set_items_received', 'get_current_world']

# Small built-in games for the health check's end-to-end logic test, in order of preference.
SMOKE_GAMES = ['Clique', 'ChecksFinder', 'Hylics 2', 'Bumper Stickers']


def world_identity(cls):
    # The installed apworld's data checksum (computed exactly as Archipelago does; the server sends the seed's in
    # RoomInfo) and its declared version, so Atlas can tell whether this is the apworld the seed was made with.
    out = {}
    try:
        out['data_checksum'] = cls.get_data_package_data()['checksum']
    except Exception as e:
        note('The data checksum of ' + str(getattr(cls, 'game', cls)) + ' could not be computed: ' + type(e).__name__ + ': ' + str(e))
    try:
        version = getattr(cls, 'world_version', None)
        if version is not None:
            out['world_version'] = '.'.join(str(part) for part in version) if isinstance(version, tuple) else str(version)
    except Exception as e:
        note('The version of ' + str(getattr(cls, 'game', cls)) + ' could not be read: ' + type(e).__name__ + ': ' + str(e))
    return out


def ut_api_missing():
    import inspect
    from worlds.tracker.TrackerCore import TrackerCore
    missing = [name for name in REQUIRED_UT_API if not callable(getattr(TrackerCore, name, None))]
    try:
        if 'super_override_yaml_path' not in inspect.signature(TrackerCore.run_generator).parameters:
            missing.append('run_generator(super_override_yaml_path)')
    except Exception:
        pass
    return missing


def generate_with(tracker_cls, logger, cls, game, slot_name, slot_data, doc):
    # Rebuilds one player's world from one YAML, in a folder of its own (no other YAML can affect it).
    folder = tempfile.mkdtemp(prefix='atlas_yaml_')
    try:
        with open(os.path.join(folder, 'player.yaml'), 'w', encoding='utf-8') as f:
            json.dump(doc, f)  # JSON is valid YAML
        c = tracker_cls(logger, False, False)
        c.set_slot_params(game, 1, slot_name, 1)
        c.run_generator(None, None, super_override_yaml_path=folder)
        c.initalize_tracker_core(cls, slot_data)
        if c.multiworld is None or c.player_id is None:
            return None, getattr(c, 'gen_error', None) or 'The world could not be generated from this YAML.'
        return c, None
    finally:
        shutil.rmtree(folder, ignore_errors=True)


def verify(c, expected):
    got = {l.address for l in c.multiworld.get_locations(c.player_id) if l.address is not None}
    exp = set(expected)
    if not exp:
        return {'expected': 0, 'got': len(got), 'missing': 0, 'extra': 0, 'match': None}
    return {'expected': len(exp), 'got': len(got), 'missing': len(exp - got), 'extra': len(got - exp), 'match': exp == got}


def smoke_test():
    # End to end, the way a real slot runs: rebuild a small built-in world, then compute its starting logic.
    from worlds.AutoWorld import AutoWorldRegister
    from worlds.tracker.TrackerCore import TrackerCore
    for game in SMOKE_GAMES:
        cls = AutoWorldRegister.world_types.get(game)
        if cls is None:
            continue
        try:
            c, err = generate_with(TrackerCore, logging.getLogger('AtlasCheck'), cls, game, 'AtlasCheck', {},
                                   {'name': 'AtlasCheck', 'game': game, game: {}})
            if c is None:
                return {'game': game, 'ok': False, 'locations': 0, 'in_logic': 0, 'error': err}
            locs = {l.address for l in c.multiworld.get_locations(c.player_id) if l.address is not None}
            c.set_missing_locations(locs)
            c.set_items_received([])
            state = c.updateTracker()
            in_logic = len(getattr(state, 'in_logic_locations', None) or [])
            ok = len(locs) > 0 and in_logic > 0
            goal_start = goal_reachable(c, state)
            goal_all = None
            try:
                from NetUtils import NetworkItem
                prog = [it.code for it in c.multiworld.itempool
                        if it.player == c.player_id and it.code is not None and it.advancement]
                c.set_items_received([NetworkItem(i, -1, -1, 0) for i in prog])
                goal_all = goal_reachable(c, c.updateTracker())
            except Exception as e:
                note('The health check could not test the goal with every item: ' + type(e).__name__ + ': ' + str(e))
                goal_all = None
            return {'game': game, 'ok': ok, 'locations': len(locs), 'in_logic': in_logic,
                    'goal_at_start': goal_start, 'goal_with_all_items': goal_all,
                    'error': None if ok else 'the tracker computed no logic for a world that has some'}
        except Exception:
            return {'game': game, 'ok': False, 'locations': 0, 'in_logic': 0, 'error': traceback.format_exc()[-600:]}
    return None


def yaml_candidates(cls, game, slot_name, slot_data, linked_path, players_dir):
    # In order: the YAML the user linked to this slot, one built from slot data, a match in the Players folder.
    resolved, total = resolved_options(cls, slot_data)
    notes = []
    cands = []

    def overlay(doc):
        doc = dict(doc)
        doc['name'] = slot_name
        doc['game'] = game
        section = dict(doc.get(game) or {})
        section.update(resolved)
        doc[game] = section
        return doc

    if linked_path:
        try:
            doc = pick_doc(load_yaml_docs(linked_path), game, slot_name, False)
            if doc is not None:
                cands.append(('linked', os.path.basename(linked_path), overlay(doc)))
            else:
                notes.append('The linked YAML has no ' + game + ' player.')
        except Exception as e:
            notes.append('The linked YAML could not be read: ' + str(e))
    if resolved:
        cands.append(('slot_data', None, {'name': slot_name, 'game': game, game: dict(resolved)}))
    if players_dir and os.path.isdir(players_dir):
        for fname in sorted(os.listdir(players_dir)):
            if not fname.lower().endswith(('.yaml', '.yml')):
                continue
            try:
                doc = pick_doc(load_yaml_docs(os.path.join(players_dir, fname)), game, slot_name, True)
            except Exception as e:
                note('The YAML ' + fname + ' in the Players folder could not be read: ' + type(e).__name__ + ': ' + str(e))
                continue
            if doc is not None:
                cands.append(('players', fname, overlay(doc)))
    return cands, notes, len(resolved), total

def apply_apworld_override(req):
    # When Atlas has the apworld version this seed was made with (req: apworld_override + expected_checksum), use it
    # for this slot instead of the installed copy, unless the installed copy already matches.
    from worlds.AutoWorld import AutoWorldRegister
    path = req.get('apworld_override')
    if not path:
        return None
    game = req.get('game')
    expected = req.get('expected_checksum')
    current = AutoWorldRegister.world_types.get(game)
    if current is not None and expected and world_identity(current).get('data_checksum') == expected:
        return {'used': False, 'reason': 'the installed copy already matches the seed'}
    try:
        swap_in_apworld(path, game)
        loaded = AutoWorldRegister.world_types.get(game)
        identity = world_identity(loaded) if loaded is not None else {}
        return {'used': loaded is not None, 'file': os.path.basename(path),
                'matches': bool(expected) and identity.get('data_checksum') == expected,
                'world_version': identity.get('world_version')}
    except Exception as e:
        # swap_in_apworld restored the installed copy: the slot carries on with it, and Atlas reports why.
        return {'used': False, 'error': (type(e).__name__ + ': ' + str(e))[:300]}


def start_slot(req, logger):
    # Rebuilds one slot's world the way a live slot does; shared by the bridge and the seed test.
    from worlds.tracker.TrackerCore import TrackerCore
    from worlds.AutoWorld import AutoWorldRegister
    game = req.get('game')
    slot_name = req.get('player_name')
    slot_data = req.get('slot_data') or {}
    all_locations = req.get('all_locations') or []
    override = apply_apworld_override(req)
    cls = AutoWorldRegister.world_types.get(game)
    if cls is None:
        import worlds
        return None, {'code': 'world_missing', 'message': game + ' is not installed in this engine.',
                      'failed_worlds': [str(w) for w in getattr(worlds, 'failed_world_loads', [])],
                      'apworld_override': override}
    if getattr(cls, 'disable_ut', False):
        return None, {'code': 'ut_disabled', 'message': "This game's author asked for the Universal Tracker not to be used with it."}
    missing_api = ut_api_missing()
    if missing_api:
        return None, {'code': 'ut_incompatible', 'message': 'This Universal Tracker version is incompatible with Atlas (missing: ' +
                      ', '.join(missing_api) + '). Install the tested version from the Atlas Engine window.'}
    players_dir = ensure_players_folder()
    if getattr(cls, 'ut_can_gen_without_yaml', False):
        c = TrackerCore(logger, False, False)
        c.set_slot_params(game, 1, slot_name, 1)
        c.initalize_tracker_core(cls, slot_data)
        if getattr(c, 'tracker_disabled', False):
            return None, {'code': 'ut_disabled', 'message': "This game's author asked for the Universal Tracker not to be used with it."}
        if c.multiworld is None or c.player_id is None:
            return None, {'code': 'generation_failed', 'message': getattr(c, 'gen_error', None) or 'The world could not be rebuilt from the slot data.'}
        info = {'source': 'not_needed', 'apworld_override': override}
        info.update(verify(c, all_locations))
        return c, info
    cands, notes, used, total = yaml_candidates(cls, game, slot_name, slot_data, req.get('yaml_path'), players_dir)
    attempts = []
    best = None
    for source, fname, doc in cands:
        try:
            c, err = generate_with(TrackerCore, logger, cls, game, slot_name, slot_data, doc)
        except Exception as e:
            c, err = None, str(e)
        if c is None:
            attempts.append({'source': source, 'file': fname, 'ok': False, 'error': (err or '')[:400]})
            continue
        v = verify(c, all_locations)
        attempt = {'source': source, 'file': fname, 'ok': True}
        attempt.update(v)
        attempts.append(attempt)
        score = v['missing'] + v['extra']
        if best is None or score < best[0]:
            best = (score, c, source, fname, v)
        if v['match'] is not False:
            break  # this YAML rebuilds exactly the server's locations
    if best is None:
        code = 'generation_failed' if cands else 'yaml_needed'
        message = ('None of the YAMLs tried could rebuild this world.' if cands else
                   game + " needs this player's YAML: its slot data doesn't include the options.")
        return None, {'code': code, 'message': message, 'attempts': attempts, 'notes': notes}
    info = {'source': best[2], 'file': best[3], 'options_from_slot_data': used, 'options_total': total,
            'attempts': attempts, 'notes': notes, 'apworld_override': override}
    info.update(best[4])
    return best[1], info


def goal_reachable(core, tracker_state):
    # Whether the slot's goal can be completed now: the world's own completion rule, checked against the same state
    # that produced the in-logic list (events already collected). None when it can't be told.
    try:
        st = getattr(tracker_state, 'state', None)
        mw = getattr(core, 'multiworld', None)
        player = getattr(core, 'player_id', None)
        if st is None or mw is None or player is None:
            return None
        return bool(mw.has_beaten_game(st, player))
    except Exception as e:
        note_once("The slot's goal could not be checked: " + type(e).__name__ + ': ' + str(e))
        return None


def reachable_after(core, item_ids, missing_locations=None):
    # The location ids in logic with these items received (the same query the live 'update' answers).
    from NetUtils import NetworkItem
    if missing_locations is not None:
        core.set_missing_locations(set(missing_locations))
    core.set_items_received([NetworkItem(i, -1, -1, 0) for i in item_ids])
    state = core.updateTracker()
    world = core.get_current_world()
    ids = []
    for loc_name in getattr(state, 'in_logic_locations', None) or []:
        if loc_name in world.location_name_to_id:
            ids.append(world.location_name_to_id[loc_name])
    return ids, state


def item_pool(core):
    # The slot's items as the world classes them (Atlas counts progression from it), with Archipelago's flags:
    # 1 progression, 2 useful, 4 trap.
    mw = getattr(core, 'multiworld', None)
    if not mw:
        return []
    player = getattr(core, 'player_id', None)
    if player is None:
        player = getattr(core, 'slot', 1)
    items = [it for it in mw.itempool if it.player == player and it.code is not None]
    if not items and len(mw.itempool) > 0 and player != 1:
        items = [it for it in mw.itempool if it.player == 1 and it.code is not None]
    items += [loc.item for loc in mw.get_locations(player)
              if loc.item and (loc.item.player == player or loc.item.player == 1) and loc.item.code is not None]
    items += [it for it in mw.precollected_items.get(player, []) if it.code is not None]
    if not items:
        # Nothing placed (some worlds place items late): every item the world can make.
        world = core.get_current_world()
        failed = []
        for name in getattr(world, 'item_name_to_id', {}) if world else []:
            try:
                it = world.create_item(name)
            except Exception as e:
                failed.append(name + ' (' + type(e).__name__ + ')')
                continue
            if getattr(it, 'code', None) is not None:
                items.append(it)
        if failed:
            note('The world could not make ' + str(len(failed)) + ' of its items: ' + ', '.join(failed[:10]))
    pool = []
    for it in items:
        classification = getattr(it, 'classification', None)
        if classification is not None:
            try:
                flags = int(classification) & 7
            except (TypeError, ValueError):
                flags = int(getattr(classification, 'value', 0) or 0) & 7
        else:
            # Worlds from before ItemClassification.
            flags = ((1 if getattr(it, 'advancement', False) else 0) | (2 if getattr(it, 'never_exclude', False) else 0) |
                     (4 if getattr(it, 'trap', False) else 0))
        pool.append({'id': it.code, 'name': getattr(it, 'name', 'Unknown Item'), 'flags': flags})
    return pool


class Slot:
    # One slot's world in this engine: its tracker core, and the state of its last update (which 'explain' answers
    # about).
    def __init__(self, core):
        self.core = core
        self.last_state = None
        self.last_glitch_state = None
        self.goal_unknown_noted = False


def handle_init(req, logger):
    # Rebuilds the slot's world. Returns the reply, and the slot (None when it couldn't start: the engine then
    # answers nothing about logic).
    all_locations = req.get('all_locations') or []
    core, info = start_slot(req, logger)
    if core is None:
        reply = {'status': 'error', 'game': req.get('game'), 'versions': versions()}
        reply.update(info)
        return reply, None
    if all_locations:
        core.set_missing_locations(set(all_locations))
    from worlds.AutoWorld import AutoWorldRegister
    reply = {'status': 'ready', 'item_pool': item_pool(core), 'yaml': info, 'versions': versions()}
    world_cls = AutoWorldRegister.world_types.get(req.get('game'))
    if world_cls is not None:
        reply.update(world_identity(world_cls))
    return reply, Slot(core)


def handle_update(slot, req):
    # What's in logic with these items: the same query the seed test checks against real playthroughs.
    core = slot.core
    reachable, state = reachable_after(core, req.get('items', []), req.get('missing_locations'))
    world = core.get_current_world()
    excluded = []
    for loc in core.multiworld.get_locations(getattr(core, 'player_id', None) or 1):
        progress = getattr(loc, 'progress_type', None)
        if progress is not None and getattr(progress, 'name', '') == 'EXCLUDED' and loc.name in world.location_name_to_id:
            excluded.append(world.location_name_to_id[loc.name])
    glitched = [world.location_name_to_id[name] for name in getattr(state, 'glitched_locations', []) or []
                if name in world.location_name_to_id]
    # Kept for 'explain', which answers questions about this exact state.
    slot.last_state = getattr(state, 'state', None)
    slot.last_glitch_state = getattr(state, 'glitches_state', None)
    goal = goal_reachable(core, state)
    if goal is None and not slot.goal_unknown_noted:
        slot.goal_unknown_noted = True
        note("This slot's goal can't be checked: go mode isn't shown for it.")
    return {'reachable': reachable, 'excluded': excluded, 'glitched': glitched, 'goal': goal}


def rule_text(fn, state=None):
    # A readable form of an access rule. 'ALWAYS' / 'NEVER' mark rules with no real condition.
    if fn is None:
        return 'ALWAYS'
    kind = type(fn).__qualname__
    if kind.startswith('True_'):
        return 'ALWAYS'
    if kind.startswith('False_'):
        return 'NEVER'
    # Worlds built with rule_builder describe themselves, per item, against the current state
    # ('Has Lordvessel', 'Missing Lordvessel').
    if hasattr(fn, 'explain_str'):
        try:
            text = fn.explain_str(state) if state is not None else str(fn)
        except Exception:
            try:
                text = str(fn)
            except Exception:
                text = ''  # a rule that can't describe itself: shown without text
        text = (text or '').strip()
        if text in ('', 'True'):
            return 'ALWAYS'
        return text[:800]
    # Plain functions/lambdas: their source, when the world ships it (apworlds usually do).
    try:
        import inspect
        src = inspect.getsource(fn).strip()
        if 'lambda state: True' in src and len(src) < 120:
            return 'ALWAYS'
        return src[:800]
    except Exception:
        pass  # no source shipped (or a built-in): its repr below
    try:
        text = repr(fn)
        return '' if text.startswith('<function') or text.startswith('<bound') else text[:800]
    except Exception:
        return ''


def handle_explain(slot, req):
    # Why is (or isn't) this location in logic? Region, rule text, the entrances into its region, and when it's out of
    # logic, which remaining items would open it, within a time budget.
    import time
    from collections import Counter
    core = slot.core
    out = {}
    mw = getattr(core, 'multiworld', None)
    world = core.get_current_world()
    player = getattr(core, 'player_id', None) or 1
    loc_name = world.location_id_to_name.get(req.get('location')) if world else None
    if mw is None or loc_name is None:
        out['error'] = 'Location is not part of this world.'
        return out
    loc = mw.get_location(loc_name, player)
    base = slot.last_state
    region = getattr(loc, 'parent_region', None)
    out['location'] = loc_name
    out['region'] = region.name if region is not None else ''
    pt = getattr(loc, 'progress_type', None)
    out['progress_type'] = getattr(pt, 'name', '') if pt is not None else ''
    out['rule'] = rule_text(getattr(loc, 'access_rule', None), base)
    if base is None:
        out['error'] = 'The logic engine has not evaluated any items yet.'
        return out
    out['in_logic'] = bool(loc.can_reach(base))
    out['region_reachable'] = bool(region.can_reach(base)) if region is not None else False
    out['glitched'] = bool(slot.last_glitch_state is not None and loc.can_reach(slot.last_glitch_state))
    entrances = []
    for ent in list(getattr(region, 'entrances', []) or [])[:15]:
        src_region = getattr(ent, 'parent_region', None)
        try:
            reach = bool(ent.can_reach(base))
        except Exception as e:
            note('Could not tell whether the entrance ' + repr(ent.name) + ' is reachable: ' + type(e).__name__ + ': ' + str(e))
            reach = False
        entrances.append({'name': ent.name, 'from': src_region.name if src_region is not None else '',
                          'reachable': reach, 'rule': rule_text(getattr(ent, 'access_rule', None), base)})
    out['entrances'] = entrances
    if out['in_logic'] or not req.get('analyze', True):
        return out

    budget = float(req.get('budget', 6.0))
    deadline = time.time() + budget
    pool = Counter(it.name for it in mw.itempool if it.player == player and getattr(it, 'advancement', False))
    missing = {name: count - base.count(name, player) for name, count in pool.items() if base.count(name, player) < count}
    event_locs = [l for l in mw.get_locations(player) if not l.address]

    def reachable_with(extra):
        st = base.copy()
        for name, n in extra.items():
            for _ in range(n):
                st.collect(mw.create_item(name, player), True)
        st.sweep_for_advancements(locations=event_locs)
        return bool(loc.can_reach(st))

    out['candidates'] = len(missing)
    if not missing or not reachable_with(missing):
        out['unreachable_with_all'] = True
        return out
    singles = []
    partial = False
    for name in sorted(missing):
        if time.time() > deadline:
            partial = True
            break
        if reachable_with({name: 1}):
            singles.append(name)
    out['single_unlocks'] = singles
    if not singles and not partial:
        # No single item does it: report which items are part of every way in.
        required = []
        for name in sorted(missing):
            if time.time() > deadline:
                partial = True
                break
            rest = dict(missing)
            del rest[name]
            if not reachable_with(rest):
                required.append(name)
        out['required'] = required
    out['partial'] = partial
    return out


def launch_bridge(*args):
    # Atlas's logic engine: one request per line on stdin, one answer per line on Atlas's channel, each carrying its
    # request's id. A request that fails is answered with the error; one the bridge doesn't understand, too.
    protect_channel()
    logger = logging.getLogger('UltimateBridge')
    try:
        from worlds.tracker.TrackerCore import TrackerCore  # noqa: F401  the tracker must load, or no slot can start
    except Exception as e:
        send({'event': 'boot_failed', 'error': type(e).__name__ + ': ' + str(e), 'trace': traceback.format_exc()})
        return
    slot = None  # built at 'init', from the slot's own YAML only (other YAMLs can't break it)
    while True:
        line = sys.stdin.readline()
        if not line:
            break  # Atlas closed the pipe: it stopped the engine
        rid = None
        try:
            req = json.loads(line)
            rid = req.get('id')
            action = req.get('action')
            if action == 'init':
                reply, started = handle_init(req, logger)
                if started is not None:
                    slot = started
            elif slot is None:
                reply = {'error': 'The logic engine has not been started for a slot yet.'}
            elif action == 'update':
                reply = handle_update(slot, req)
            elif action == 'explain':
                reply = handle_explain(slot, req)
            else:
                reply = {'error': 'Unknown request: ' + str(action)}
        except Exception as e:
            reply = {'error': str(e), 'trace': traceback.format_exc()}
        reply['id'] = rid
        send(reply)


def atlas_names(*args):
    # One request on stdin: {"games": [...]}. Replies with each installed game's item and location
    # name -> id tables, plus every game name the install knows. No tracker engine is started.
    protect_channel()
    try:
        line = sys.stdin.readline()
        req = json.loads(line) if line else {}
        from worlds.AutoWorld import AutoWorldRegister
        out = {}
        for game in req.get('games', []):
            cls = AutoWorldRegister.world_types.get(game)
            if cls is None:
                continue
            version = getattr(cls, 'world_version', None) or getattr(cls, 'data_version', '')
            if isinstance(version, tuple):
                version = '.'.join(str(part) for part in version)
            out[game] = {'items': dict(cls.item_name_to_id), 'locations': dict(cls.location_name_to_id), 'version': str(version)}
        send({'games': out, 'known': sorted(AutoWorldRegister.world_types.keys())})
    except Exception:
        send({'error': traceback.format_exc()})

def load_multidata(path):
    # A generated seed's server data (.archipelago, or the output .zip holding it), read with Archipelago's own
    # restricted loader.
    import zipfile
    import zlib
    import Utils
    if path.lower().endswith('.zip'):
        with zipfile.ZipFile(path) as z:
            name = next(n for n in z.namelist() if n.lower().endswith('.archipelago'))
            raw = z.read(name)
    else:
        with open(path, 'rb') as f:
            raw = f.read()
    return Utils.restricted_loads(zlib.decompress(raw[1:]))


def cached_apworld(index_path, game, checksum):
    # Atlas's cached apworld file matching a seed's checksum for a game, if any.
    if not index_path or not checksum or not os.path.isfile(index_path):
        return None
    try:
        with open(index_path, encoding='utf-8-sig') as f:
            entries = json.load(f) or []
        for e in entries:
            if str(e.get('Game', '')).lower() == str(game).lower() and e.get('Checksum') == checksum and os.path.isfile(e.get('File') or ''):
                return e['File']
    except Exception as e:
        note("Atlas's apworld cache list could not be read: " + type(e).__name__ + ': ' + str(e))
    return None


def atlas_seed_test(*args):
    # Accuracy against ground truth. stdin: {"seed": path, "players": [slots] (optional), "yaml_paths": {slot: path}}.
    # For each player: rebuild the world from the seed's slot data exactly as a live slot does, compare the data
    # checksum and location list with the seed's, then replay the generator's spheres: with the items found in
    # spheres before i received, the reachable set must be exactly the locations of spheres 0..i. "late" = the seed
    # reaches it but Atlas wouldn't show it in logic yet; "early" = Atlas would show it in logic too soon.
    import time
    protect_channel()
    logger = logging.getLogger('AtlasSeedTest')
    line = sys.stdin.readline()
    req = json.loads(line) if line else {}
    out = {'seed': req.get('seed'), 'players': []}
    try:
        from worlds.AutoWorld import AutoWorldRegister
        data = load_multidata(req['seed'])
        out['seed_name'] = data.get('seed_name')
        out['generator'] = '.'.join(str(v) for v in (data.get('version') or ()))
        all_locations = data['locations']
        spheres = data.get('spheres') or []
        precollected = data.get('precollected_items') or {}
        wanted = {int(p) for p in (req.get('players') or all_locations.keys())}
        out['spheres'] = len(spheres)
        for player, slot in data['slot_info'].items():
            if player not in wanted or player not in all_locations:
                continue
            game, name = slot.game, slot.name
            result = {'player': player, 'name': name, 'game': game}
            out['players'].append(result)
            started = time.time()
            cls = AutoWorldRegister.world_types.get(game)
            expected_checksum = ((data.get('datapackage') or {}).get(game) or {}).get('checksum')
            if cls is not None and expected_checksum:
                result['seed_checksum'] = expected_checksum
                result['local_checksum'] = world_identity(cls).get('data_checksum')
                result['checksum_match'] = result['local_checksum'] == expected_checksum
            locs = sorted(all_locations[player].keys())
            override = cached_apworld(req.get('apworld_cache_index'), game, expected_checksum)
            core, start = start_slot({'game': game, 'player_name': name,
                                      'slot_data': (data.get('slot_data') or {}).get(player) or {},
                                      'all_locations': locs,
                                      'yaml_path': (req.get('yaml_paths') or {}).get(str(player)),
                                      'apworld_override': override, 'expected_checksum': expected_checksum}, logger)
            if cls is not None and expected_checksum and override:
                # The test ran on the seed's own version when Atlas had it cached.
                current = AutoWorldRegister.world_types.get(game)
                result['local_checksum'] = world_identity(current).get('data_checksum') if current else None
                result['checksum_match'] = result['local_checksum'] == expected_checksum
                result['apworld_override'] = start.get('apworld_override') if isinstance(start, dict) else None
            if core is None:
                result['error'] = start
                continue
            result['rebuild'] = {k: start.get(k) for k in ('source', 'file', 'match', 'expected', 'got', 'missing', 'extra')}
            world = core.get_current_world()
            names = getattr(world, 'location_id_to_name', {}) or {}
            inventory = list(precollected.get(player, []))
            reached = set()
            steps = []
            for i, sphere in enumerate(spheres):
                reached |= set(sphere.get(player, ()))
                reach, _ = reachable_after(core, inventory, locs)
                reach = set(reach)
                late, early = reached - reach, reach - reached
                steps.append({'sphere': i, 'expected': len(reached), 'reachable': len(reach),
                              'late_count': len(late), 'early_count': len(early),
                              'late': [names.get(l, str(l)) for l in sorted(late)[:15]],
                              'early': [names.get(l, str(l)) for l in sorted(early)[:15]]})
                # The items found in this sphere that belong to this player (from any player's world).
                for finder, found in sphere.items():
                    for loc in found:
                        entry = all_locations.get(finder, {}).get(loc)
                        if entry is not None and entry[1] == player:
                            inventory.append(entry[0])
            result['steps'] = steps
            result['exact'] = bool(steps) and all(s['late_count'] == 0 and s['early_count'] == 0 for s in steps)
            result['seconds'] = round(time.time() - started, 1)
    except Exception:
        out['error'] = traceback.format_exc()[-1500:]
    send(out)


def swap_in_apworld(path, game=None):
    # Loads an apworld file in place of the installed copy of the same world, in this process only (the user's
    # install isn't touched). Returns the game it registers. Imported the way Archipelago imports .apworld files:
    # a zip importer's spec served by a module finder.
    import importlib
    import importlib.abc
    import zipimport
    import worlds  # noqa: F401
    from worlds.AutoWorld import AutoWorldRegister
    pkg = os.path.splitext(os.path.basename(path))[0]
    spec = zipimport.zipimporter(path).find_spec('worlds.' + pkg)
    if spec is None:
        raise RuntimeError(os.path.basename(path) + ' has no ' + pkg + ' package inside.')
    # Kept so a candidate that fails to load leaves the installed copy exactly as it was.
    saved_modules = {m: sys.modules.pop(m) for m in list(sys.modules) if m == 'worlds.' + pkg or m.startswith('worlds.' + pkg + '.')}
    saved_worlds = {}
    for registered, cls in list(AutoWorldRegister.world_types.items()):
        if cls.__module__.split('.')[1:2] == [pkg] or registered == game:
            saved_worlds[registered] = AutoWorldRegister.world_types.pop(registered)

    class _ApworldFinder(importlib.abc.MetaPathFinder):
        def find_spec(self, fullname, _path=None, _target=None):
            return spec if fullname == 'worlds.' + pkg else None

    finder = _ApworldFinder()
    sys.meta_path.insert(0, finder)
    before = set(AutoWorldRegister.world_types)
    try:
        importlib.import_module('worlds.' + pkg)
        new = [g for g in AutoWorldRegister.world_types if g not in before]
        if not new:
            raise RuntimeError('The apworld loaded but registered no game.')
        # Archipelago takes an apworld's version from its manifest when it loads it; do the same.
        try:
            import zipfile
            from Utils import tuplize_version
            with zipfile.ZipFile(path) as z:
                manifest = next((n for n in z.namelist() if n.endswith('archipelago.json')), None)
                if manifest:
                    version = json.loads(z.read(manifest).decode('utf-8-sig')).get('world_version')
                    if version:
                        AutoWorldRegister.world_types[new[0]].world_version = tuplize_version(version)
        except Exception as e:
            note("The version in " + os.path.basename(path) + "'s manifest could not be read: " + type(e).__name__ + ': ' + str(e))
        return new[0]
    except BaseException:
        for m in [m for m in list(sys.modules) if m == 'worlds.' + pkg or m.startswith('worlds.' + pkg + '.')]:
            del sys.modules[m]
        for registered in [g for g in AutoWorldRegister.world_types if g not in before]:
            del AutoWorldRegister.world_types[registered]
        sys.modules.update(saved_modules)
        AutoWorldRegister.world_types.update(saved_worlds)
        if finder in sys.meta_path:
            sys.meta_path.remove(finder)
        raise


def atlas_checksum(*args):
    # The data checksum of a candidate apworld file, to find the version a seed was made with.
    # stdin: {"apworld": path, "game": expected game (optional)}. Runs in a throwaway process.
    protect_channel()
    line = sys.stdin.readline()
    req = json.loads(line) if line else {}
    out = {}
    try:
        from worlds.AutoWorld import AutoWorldRegister
        game = swap_in_apworld(req['apworld'], req.get('game'))
        out['game'] = game
        out.update(world_identity(AutoWorldRegister.world_types[game]))
    except Exception:
        out['error'] = traceback.format_exc()[-800:]
    send(out)


def atlas_game_sweep(*args):
    # Rebuilds games with default options and computes their starting logic, to find worlds that can't be tracked
    # before a user connects one. stdin: {"games": [names] (optional: all installed)}. One JSON line per game, then
    # {"done": true}, so a hang in one game only loses that game.
    import time
    protect_channel()
    line = sys.stdin.readline()
    req = json.loads(line) if line else {}
    from worlds.AutoWorld import AutoWorldRegister
    from worlds.tracker.TrackerCore import TrackerCore
    logger = logging.getLogger('AtlasGameSweep')
    games = req.get('games') or sorted(g for g in AutoWorldRegister.world_types if g not in ('Archipelago', 'Universal Tracker'))
    for game in games:
        started = time.time()
        out = {'game': game}
        cls = AutoWorldRegister.world_types.get(game)
        try:
            if cls is None:
                out['error'] = 'not installed'
            else:
                out['yamlless'] = bool(getattr(cls, 'ut_can_gen_without_yaml', False))
                out['ut_disabled'] = bool(getattr(cls, 'disable_ut', False))
                out.update(world_identity(cls))
                if out['ut_disabled']:
                    out['error'] = "the game's author disabled the Universal Tracker for it"
                else:
                    c, err = generate_with(TrackerCore, logger, cls, game, 'AtlasSweep', {}, {'name': 'AtlasSweep', 'game': game, game: {}})
                    if c is None:
                        out['error'] = err or 'the world could not be generated with default options'
                    else:
                        locs = {l.address for l in c.multiworld.get_locations(c.player_id) if l.address is not None}
                        reach, _ = reachable_after(c, [], locs)
                        out['locations'] = len(locs)
                        out['in_logic'] = len(reach)
                        out['ok'] = len(locs) > 0
        except BaseException as e:
            if isinstance(e, KeyboardInterrupt):
                raise
            out['error'] = (traceback.format_exc().strip().split('\n') or ['?'])[-1][:300]
        out['seconds'] = round(time.time() - started, 2)
        send(out)
    send({'done': True})


def atlas_check(*args):
    # Health check for the setup page: versions, installed games, worlds that failed to load, tracker import.
    protect_channel()
    out = versions()
    try:
        import worlds
        from worlds.AutoWorld import AutoWorldRegister
        out['games'] = sorted(AutoWorldRegister.world_types.keys())
        out['failed'] = [str(w) for w in getattr(worlds, 'failed_world_loads', [])]
        # Why each failed (recorded by atlas_run.py in the portable engine), with where its declared packages are.
        try:
            import __main__
            details = dict(getattr(__main__, 'ATLAS_LOAD_ERRORS', {}) or {})
        except Exception as e:
            note('Why worlds failed to load could not be read: ' + type(e).__name__ + ': ' + str(e))
            details = {}
        for source in getattr(worlds, 'world_sources', []) or []:
            name = os.path.splitext(os.path.basename(getattr(source, 'path', '') or ''))[0]
            if name in details:
                details[name]['path'] = getattr(source, 'resolved_path', None) or getattr(source, 'path', None)
                details[name]['is_zip'] = bool(getattr(source, 'is_zip', False))
        out['failed_details'] = details
        try:
            from worlds.tracker.TrackerCore import TrackerCore  # noqa: F401
            out['tracker'] = True
        except Exception as e:
            out['tracker'] = False
            out['tracker_error'] = str(e)
        out['players_dir'] = ensure_players_folder()
        if out['tracker']:
            out['api_missing'] = ut_api_missing()
            if not out['api_missing']:
                out['smoke'] = smoke_test()
    except Exception:
        out['error'] = traceback.format_exc()
    send(out)


from worlds.LauncherComponents import Component, components, Type
components.append(Component('UltimateBridge', None, func=launch_bridge, component_type=Type.CLIENT))
components.append(Component('AtlasNames', None, func=atlas_names, component_type=Type.CLIENT))
components.append(Component('AtlasCheck', None, func=atlas_check, component_type=Type.CLIENT))
components.append(Component('AtlasSeedTest', None, func=atlas_seed_test, component_type=Type.CLIENT))
components.append(Component('AtlasChecksum', None, func=atlas_checksum, component_type=Type.CLIENT))
components.append(Component('AtlasGameSweep', None, func=atlas_game_sweep, component_type=Type.CLIENT))
