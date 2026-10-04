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


def versions():
    out = {'python': sys.version.split()[0], 'tested_ut': TESTED_UT_VERSIONS}
    try:
        import Utils
        out['ap'] = Utils.__version__
    except Exception:
        pass
    try:
        from worlds.tracker import UT_VERSION
        out['ut'] = UT_VERSION
    except Exception:
        pass
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
    except Exception:
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
    except Exception:
        pass
    try:
        version = getattr(cls, 'world_version', None)
        if version is not None:
            out['world_version'] = '.'.join(str(part) for part in version) if isinstance(version, tuple) else str(version)
    except Exception:
        pass
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
            return {'game': game, 'ok': ok, 'locations': len(locs), 'in_logic': in_logic,
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
            except Exception:
                continue
            if doc is not None:
                cands.append(('players', fname, overlay(doc)))
    return cands, notes, len(resolved), total

def start_slot(req, logger):
    # Rebuilds one slot's world the way a live slot does; shared by the bridge and the seed test.
    from worlds.tracker.TrackerCore import TrackerCore
    from worlds.AutoWorld import AutoWorldRegister
    game = req.get('game')
    slot_name = req.get('player_name')
    slot_data = req.get('slot_data') or {}
    all_locations = req.get('all_locations') or []
    cls = AutoWorldRegister.world_types.get(game)
    if cls is None:
        import worlds
        return None, {'code': 'world_missing', 'message': game + ' is not installed in this engine.',
                      'failed_worlds': [str(w) for w in getattr(worlds, 'failed_world_loads', [])]}
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
        info = {'source': 'not_needed'}
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
            'attempts': attempts, 'notes': notes}
    info.update(best[4])
    return best[1], info


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


def launch_bridge(*args):
    try:
        from worlds.tracker.TrackerCore import TrackerCore
        from worlds.AutoWorld import AutoWorldRegister
        from NetUtils import NetworkItem
        from BaseClasses import LocationProgressType

        logger = logging.getLogger('UltimateBridge')
        # Built per slot at 'init', from that slot's own YAML only (other YAMLs can't break it).
        core = None

        last_state = None
        last_glitch_state = None

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
                        text = ''
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
                pass
            try:
                text = repr(fn)
                return '' if text.startswith('<function') or text.startswith('<bound') else text[:800]
            except Exception:
                return ''

        def explain_location(req, rid):
            # Why is (or isn't) this location in logic? Region, rule text, the entrances into its region, and
            # when it's out of logic, which remaining items would open it, within a time budget.
            import time
            from collections import Counter
            out = {'id': rid}
            mw = getattr(core, 'multiworld', None)
            world = core.get_current_world()
            player = getattr(core, 'player_id', None) or 1
            loc_name = world.location_id_to_name.get(req.get('location')) if world else None
            if mw is None or loc_name is None:
                out['error'] = 'Location is not part of this world.'
                return out
            loc = mw.get_location(loc_name, player)
            base = last_state
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
            out['glitched'] = bool(last_glitch_state is not None and loc.can_reach(last_glitch_state))
            entrances = []
            for ent in list(getattr(region, 'entrances', []) or [])[:15]:
                src_region = getattr(ent, 'parent_region', None)
                try:
                    reach = bool(ent.can_reach(base))
                except Exception:
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

        while True:
            line = sys.stdin.readline()
            if not line: break

            # Echo the request id on every reply so the C# side can drop late replies.
            rid = None
            try:
                req = json.loads(line)
                rid = req.get('id')
                action = req.get('action')
                
                if action == 'init':
                    all_locations = req.get('all_locations') or []
                    new_core, info = start_slot(req, logger)
                    if new_core is None:
                        reply = {'id': rid, 'status': 'error', 'game': req.get('game'), 'versions': versions()}
                        reply.update(info)
                        print(json.dumps(reply))
                        sys.stdout.flush()
                        continue
                    core = new_core
                    if all_locations:
                        core.set_missing_locations(set(all_locations))

                    item_pool = []
                    mw = getattr(core, 'multiworld', None)
                    if mw:
                        target_player = getattr(core, 'player_id', None)
                        if target_player is None:
                            target_player = getattr(core, 'slot', 1)
                            
                        pool_items = [it for it in mw.itempool if it.player == target_player and it.code is not None]
                        if not pool_items and len(mw.itempool) > 0 and target_player != 1:
                            pool_items = [it for it in mw.itempool if it.player == 1 and it.code is not None]
                            
                        loc_items = [loc.item for loc in mw.get_locations(target_player) if loc.item and (loc.item.player == target_player or loc.item.player == 1) and loc.item.code is not None]
                        pre_items = [it for it in mw.precollected_items.get(target_player, []) if it.code is not None]
                        
                        all_items = pool_items + loc_items + pre_items
                        
                        if not all_items:
                            world = core.get_current_world()
                            if world:
                                item_names = getattr(world, 'item_name_to_id', {})
                                for item_name in item_names:
                                    try:
                                        it = world.create_item(item_name)
                                        if getattr(it, 'code', None) is not None:
                                            all_items.append(it)
                                    except Exception:
                                        pass
                        
                        for it in all_items:
                            flags = 0
                            cls = getattr(it, 'classification', None)
                            if cls is not None:
                                # New AP ItemClassification
                                try:
                                    if cls & 1: flags |= 1
                                    if cls & 2: flags |= 2
                                    if cls & 4: flags |= 4
                                except Exception:
                                    try:
                                        val = cls.value
                                        if val & 1: flags |= 1
                                        if val & 2: flags |= 2
                                        if val & 4: flags |= 4
                                    except Exception:
                                        pass
                            else:
                                # Legacy
                                if getattr(it, 'advancement', False):
                                    flags |= 1
                                if getattr(it, 'never_exclude', False):
                                    flags |= 2
                                if getattr(it, 'trap', False):
                                    flags |= 4
                                    
                            item_pool.append({
                                'id': it.code,
                                'name': getattr(it, 'name', 'Unknown Item'),
                                'flags': flags
                            })
                            
                        # Debug print the first 5 items
                        logger.info('UltimateBridge Item Dump:')
                        for debug_it in item_pool[:5]:
                            logger.info(' - ' + str(debug_it.get('name')) + ': Flags=' + str(debug_it.get('flags')))

                    reply = {'id': rid, 'status': 'ready', 'item_pool': item_pool, 'yaml': info, 'versions': versions()}
                    world_cls = AutoWorldRegister.world_types.get(req.get('game'))
                    if world_cls is not None:
                        reply.update(world_identity(world_cls))
                    print(json.dumps(reply))
                    sys.stdout.flush()

                elif core is None:
                    print(json.dumps({'id': rid, 'error': 'The logic engine has not been started for a slot yet.'}))
                    sys.stdout.flush()

                elif action == 'update':
                    # The same query the seed test verifies against real playthroughs.
                    reachable_ids, state = reachable_after(core, req.get('items', []), req.get('missing_locations'))
                    world = core.get_current_world()
                            
                    excluded_ids = []
                    target_player = getattr(core, 'player_id', None) or 1
                    for loc in core.multiworld.get_locations(target_player):
                        pt = getattr(loc, 'progress_type', None)
                        if pt is not None and getattr(pt, 'name', '') == 'EXCLUDED':
                            if loc.name in world.location_name_to_id:
                                excluded_ids.append(world.location_name_to_id[loc.name])
                            
                    # Kept for 'explain', which answers questions about this exact state.
                    last_state = getattr(state, 'state', None)
                    last_glitch_state = getattr(state, 'glitches_state', None)
                    glitched_ids = []
                    for loc_name in getattr(state, 'glitched_locations', []) or []:
                        if loc_name in world.location_name_to_id:
                            glitched_ids.append(world.location_name_to_id[loc_name])

                    print(json.dumps({'id': rid, 'reachable': reachable_ids, 'excluded': excluded_ids, 'glitched': glitched_ids}))
                    sys.stdout.flush()

                elif action == 'explain':
                    print(json.dumps(explain_location(req, rid)))
                    sys.stdout.flush()

            except Exception as e:
                print(json.dumps({'id': rid, 'error': str(e), 'trace': traceback.format_exc()}))
                sys.stdout.flush()
                
    except Exception as e:
        print(json.dumps({'error': 'Bridge Boot Failed', 'trace': traceback.format_exc()}))
        sys.stdout.flush()

def atlas_names(*args):
    # One request on stdin: {"games": [...]}. Replies with each installed game's item and location
    # name -> id tables, plus every game name the install knows. No tracker engine is started.
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
        print(json.dumps({'games': out, 'known': sorted(AutoWorldRegister.world_types.keys())}))
    except Exception:
        print(json.dumps({'error': traceback.format_exc()}))
    sys.stdout.flush()

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


def atlas_seed_test(*args):
    # Accuracy against ground truth. stdin: {"seed": path, "players": [slots] (optional), "yaml_paths": {slot: path}}.
    # For each player: rebuild the world from the seed's slot data exactly as a live slot does, compare the data
    # checksum and location list with the seed's, then replay the generator's spheres: with the items found in
    # spheres before i received, the reachable set must be exactly the locations of spheres 0..i. "late" = the seed
    # reaches it but Atlas wouldn't show it in logic yet; "early" = Atlas would show it in logic too soon.
    import time
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
            core, start = start_slot({'game': game, 'player_name': name,
                                      'slot_data': (data.get('slot_data') or {}).get(player) or {},
                                      'all_locations': locs,
                                      'yaml_path': (req.get('yaml_paths') or {}).get(str(player))}, logger)
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
    print(json.dumps(out))
    sys.stdout.flush()


def atlas_checksum(*args):
    # The data checksum of a candidate apworld file, to find the version a seed was made with.
    # stdin: {"apworld": path, "game": expected game (optional)}. Runs in a throwaway process: an installed copy of
    # the same world is unloaded first so the candidate loads in its place.
    line = sys.stdin.readline()
    req = json.loads(line) if line else {}
    out = {}
    try:
        import worlds
        from worlds.AutoWorld import AutoWorldRegister
        path = req['apworld']
        pkg = os.path.splitext(os.path.basename(path))[0]
        for name in [m for m in list(sys.modules) if m == 'worlds.' + pkg or m.startswith('worlds.' + pkg + '.')]:
            del sys.modules[name]
        for game, cls in list(AutoWorldRegister.world_types.items()):
            if cls.__module__.split('.')[1:2] == [pkg] or game == req.get('game'):
                del AutoWorldRegister.world_types[game]
        # Import it the way Archipelago imports .apworld files: a zip importer's spec served by a module finder.
        import importlib
        import importlib.abc
        import zipimport
        spec = zipimport.zipimporter(path).find_spec('worlds.' + pkg)
        if spec is None:
            raise RuntimeError(os.path.basename(path) + ' has no ' + pkg + ' package inside.')

        class _CandidateFinder(importlib.abc.MetaPathFinder):
            def find_spec(self, fullname, _path=None, _target=None):
                return spec if fullname == 'worlds.' + pkg else None

        sys.meta_path.insert(0, _CandidateFinder())
        before = set(AutoWorldRegister.world_types)
        importlib.import_module('worlds.' + pkg)
        new = [g for g in AutoWorldRegister.world_types if g not in before]
        if not new:
            out['error'] = 'The apworld loaded but registered no game.'
        else:
            out['game'] = new[0]
            out.update(world_identity(AutoWorldRegister.world_types[new[0]]))
    except Exception:
        out['error'] = traceback.format_exc()[-800:]
    print(json.dumps(out))
    sys.stdout.flush()


def atlas_check(*args):
    # Health check for the setup page: versions, installed games, worlds that failed to load, tracker import.
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
        except Exception:
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
    print(json.dumps(out))
    sys.stdout.flush()


from worlds.LauncherComponents import Component, components, Type
components.append(Component('UltimateBridge', None, func=launch_bridge, component_type=Type.CLIENT))
components.append(Component('AtlasNames', None, func=atlas_names, component_type=Type.CLIENT))
components.append(Component('AtlasCheck', None, func=atlas_check, component_type=Type.CLIENT))
components.append(Component('AtlasSeedTest', None, func=atlas_seed_test, component_type=Type.CLIENT))
components.append(Component('AtlasChecksum', None, func=atlas_checksum, component_type=Type.CLIENT))
