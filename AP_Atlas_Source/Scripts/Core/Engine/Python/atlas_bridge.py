# The Archipelago Atlas logic bridge. Installed as the UltimateBridge apworld; Atlas runs its components
# (UltimateBridge, AtlasNames, AtlasCheck) and talks to them as one JSON object per line over stdin/stdout.
import os
import re
import sys
import collections
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


def option_as_written(cls, key, option, value):
    # Whether Archipelago's generator takes this value, written in a YAML, as the option's value: rolled as the
    # generator rolls it (a list or a mapping under an option that takes weights would be read as a choice among its
    # entries or as weights), then checked as the generator checks it (its allowed keys, its schema). Slot data often
    # holds an option in the world's own form ("well_fed" for "Well Fed", {"flags": ..., "counts": ...} for a goal),
    # which from_any alone accepts and the generator then refuses: such a value is left to the YAML.
    if getattr(option, 'supports_weighting', True) and isinstance(value, (list, dict)):
        return False
    rolled = option.from_any(value)
    try:
        from Options import PlandoOptions
        rolled.verify(cls, 'AtlasRebuild', PlandoOptions.bosses)
    except AttributeError:
        pass  # an option without checks of its own
    yaml_round_trip(value)
    return True


def yaml_round_trip(value):
    # The value as a YAML reader gets it back, refused when it isn't the same (a float written without a dot).
    import yaml
    if yaml.safe_load(yaml.safe_dump({'v': value}))['v'] != value:
        raise ValueError('changes when written to a YAML')


def resolved_options(cls, slot_data):
    # Option values the world put in its slot data. These are what the seed actually rolled, so they beat a YAML
    # (which may say 'random'), when the generator would take them as written (option_as_written). Worlds keep them
    # under 'options' or at the top level.
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
                    if option_as_written(cls, key, hints[key], value):
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


def new_tracker(tracker_cls, logger, all_entrances_known=False):
    # A tracker core. With all_entrances_known (the seed test), entrances a world would leave unconnected until the game
    # reports them ("deferred") are connected as generated, as Universal Tracker's own fuzzer does.
    c = tracker_cls(logger, False, False)
    if all_entrances_known:
        try:
            from worlds.tracker import DeferredEntranceMode
            c.enforce_deferred_connections = DeferredEntranceMode.disabled
        except Exception as e:
            note('Every entrance could not be connected for this test: ' + type(e).__name__ + ': ' + str(e))
    return c


def rebuild_seed(req, salt=''):
    # The generation seed a slot's world is rebuilt with: from the room's seed name and the slot, so it's the same each
    # time the slot's engine starts. None (a new one each time, as Universal Tracker draws) without a seed name. The
    # second rebuild that checks the first (build_check_core) takes another ("check").
    import hashlib
    name = req.get('seed_name')
    if not name:
        return None
    return int(hashlib.sha256((str(name) + ':' + str(req.get('slot')) + salt).encode('utf-8')).hexdigest()[:15], 16)


class fixed_generation_seed:
    # While a world is rebuilt: Universal Tracker's generator takes this seed (it parses no command line, so it would
    # draw a new one). Its options are rolled and its world made with it.
    def __init__(self, seed):
        self.seed = seed
        self.module = None

    def __enter__(self):
        if self.seed is None:
            return self
        try:
            import worlds.tracker.TrackerCore as module
            original = module.mystery_argparse
        except Exception as e:
            note_once('The rebuild could not be given a fixed seed: ' + type(e).__name__ + ': ' + str(e))
            return self
        seed = self.seed

        def seeded(*args, **kwargs):
            parsed = original(*args, **kwargs)
            parsed.seed = seed
            return parsed
        self.module, self.original = module, original
        module.mystery_argparse = seeded
        return self

    def __exit__(self, *exc):
        if self.module is not None:
            self.module.mystery_argparse = self.original
        return False


def read_manifest(path):
    # An apworld's archipelago.json as a dict ({} when it has none or it can't be read).
    try:
        import zipfile
        with zipfile.ZipFile(path) as z:
            name = next((n for n in z.namelist() if n.endswith('archipelago.json')), None)
            return json.loads(z.read(name).decode('utf-8-sig')) if name else {}
    except Exception as e:
        note('The manifest of ' + os.path.basename(str(path)) + ' could not be read: ' + type(e).__name__ + ': ' + str(e))
        return {}


def core_version(text):
    # An Archipelago version as Archipelago compares them ("0.6.8" -> (0, 6, 8)), or None when it isn't one.
    try:
        from Utils import tuplize_version
        return tuplize_version(str(text))
    except Exception:
        return None


def unfit_reason(manifest):
    # Why Archipelago wouldn't load an apworld with this manifest on this engine (worlds/__init__.py), in a few words;
    # None when it would.
    from Utils import version_tuple
    low = core_version(manifest.get('minimum_ap_version') or '')
    high = core_version(manifest.get('maximum_ap_version') or '')
    if low is not None and low > version_tuple:
        return 'Needs Archipelago ' + str(manifest['minimum_ap_version'])
    if high is not None and high < version_tuple:
        return 'Needs Archipelago ' + str(manifest['maximum_ap_version']) + ' or older'
    return None


def refuse_unfit_apworld(path):
    # As Archipelago refuses an apworld made for another core version, said in a few words.
    reason = unfit_reason(read_manifest(path))
    if reason:
        raise RuntimeError(reason)


def unloaded_reason(game):
    # For a game the engine doesn't have: an apworld of it that Archipelago skipped because it was made for another core
    # (its loader only logs that), in a few words. None when there's none.
    try:
        import worlds
        for source in getattr(worlds, 'world_sources', None) or []:
            if not getattr(source, 'is_zip', False):
                continue
            manifest = read_manifest(getattr(source, 'resolved_path', None) or '')
            if str(manifest.get('game', '')).lower() == str(game or '').lower():
                reason = unfit_reason(manifest)
                if reason:
                    return reason
    except Exception as e:
        note("Why " + str(game) + " didn't load could not be read: " + type(e).__name__ + ': ' + str(e))
    return None


def relax_requires(cls, doc):
    # A YAML's "requires: version" names the Archipelago that wrote it (0.6.8's launcher template writes 0.6.8), and
    # Generate refuses it on an older engine. For a tracking rebuild the world's own minimum is what counts: Archipelago
    # doesn't load an apworld whose minimum is above it, and a seed's version is refused the same way
    # (apply_apworld_override). So a stamp above this engine is dropped when the world allows the engine; the game versions
    # and plando stay. Returns the doc to generate from (a copy when changed) and the version dropped, or None.
    requires = doc.get('requires') if isinstance(doc, dict) else None
    if not isinstance(requires, dict) or 'version' not in requires:
        return doc, None
    from Utils import version_tuple
    wanted = core_version(requires['version'])
    if wanted is None or wanted <= version_tuple:
        return doc, None  # Generate takes it, or says what's wrong with it
    path = getattr(cls, 'zip_path', None)
    minimum = core_version(read_manifest(path).get('minimum_ap_version') or '') if path else None
    if minimum is not None and minimum > version_tuple:
        return doc, None  # the world itself needs it: Generate refuses, and the reason names the version
    doc = dict(doc)
    doc['requires'] = {k: v for k, v in requires.items() if k != 'version'}
    return doc, str(requires['version'])


# Why a rebuild failed, in a few plain words: Generate's own messages first (Generate.py roll_settings and handle_option,
# Options.verify), each as (pattern, words, code). Anything else is the error's own line.
PLAIN_REASONS = [
    (r'required version of generator is at least ([0-9]+(?:\.[0-9]+)*)', 'Needs Archipelago {0}', 'needs_archipelago'),
    (r'required version of world "([^"]+)" is at least ([0-9]+(?:\.[0-9]+)*)', 'Needs {0} apworld {1}', 'needs_world_version'),
    (r'required version of world "([^"]+)" is no later than ([0-9]+(?:\.[0-9]+)*)', 'Needs {0} apworld {1} or older', 'needs_world_version'),
    (r'required plando module (.+?), which is not enabled', 'Needs plando: {0}', 'needs_plando'),
    (r'No (?:functional )?world found to handle game (.+?)\. Did you mean', '{0} not installed', 'no_world'),
    (r'No game options for selected game "(.+?)" found', 'No {0} options in the YAML', 'no_options'),
    (r'Error generating option (\S+) in ', 'Option {0} rejected', 'option'),
    (r'Found unexpected key (.+?) in (.+?)\. Allowed keys', '{1}: unknown {0}', 'option'),
]
_REASON_WRAPPERS = re.compile(r'^(?:Which caused:\s*)?(?:Exception|ValueError|RuntimeError|(?:Options\.)?OptionError):\s*')


def short_note(text):
    """A note as one short line (a YAML parser's error runs to several lines with the file's path)."""
    line = (text or '').strip().splitlines()[0].strip() if (text or '').strip() else ''
    if not line:
        return None
    return line if len(line) <= 100 else line[:99] + '\u2026'


def plain_reason(text):
    # Why a rebuild failed, in a few words, and a code Atlas can act on ('other': the error's own words). Generate puts
    # the root cause on the line after "Please fix your yaml." (Utils.get_all_causes); otherwise the last line says it.
    text = text or ''
    for pattern, words, code in PLAIN_REASONS:
        found = re.search(pattern, text)
        if found:
            return words.format(*found.groups()), code
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    marker = next((i for i, line in enumerate(lines) if line.endswith('Please fix your yaml.')), None)
    line = lines[marker + 1] if marker is not None and marker + 1 < len(lines) else (lines[-1] if lines else '')
    line = _REASON_WRAPPERS.sub('', line)
    if not line:
        return None, 'other'
    return (line if len(line) <= 100 else line[:99] + '…'), 'other'


def generate_with(tracker_cls, logger, cls, game, slot_name, slot_data, doc, all_entrances_known=False, seed=None):
    # Rebuilds one player's world from one YAML, in a folder of its own (no other YAML can affect it). The engine's own
    # Players folder must exist, whatever the caller (a slot start makes it; the game sweep didn't, and on an engine no
    # slot had used yet Universal Tracker couldn't generate and failed on its own log line: KeyError 'ut_status').
    ensure_players_folder()
    # Universal Tracker hands back the world it last built for the same slot data: every rebuild here is its own.
    forget_cached_worlds()
    folder = tempfile.mkdtemp(prefix='atlas_yaml_')
    try:
        import yaml
        with open(os.path.join(folder, 'player.yaml'), 'w', encoding='utf-8') as f:
            # As YAML, not JSON: a YAML reader takes JSON's 9e-07 for text (Factorio's pollution factor was refused).
            yaml.safe_dump(doc, f, allow_unicode=True, sort_keys=False)
        c = new_tracker(tracker_cls, logger, all_entrances_known)
        c.set_slot_params(game, 1, slot_name, 1)
        with fixed_generation_seed(seed):
            c.run_generator(None, None, super_override_yaml_path=folder)
            c.initalize_tracker_core(cls, slot_data)
        if c.multiworld is None or c.player_id is None:
            return None, getattr(c, 'gen_error', None) or 'The world could not be generated from this YAML.'
        return c, None
    finally:
        shutil.rmtree(folder, ignore_errors=True)


def server_ids(c):
    # The ids the server knows the slot's locations by. A location whose address isn't a number isn't one of them: A Link
    # to the Past's dungeon prizes list ROM addresses, and Universal Tracker leaves those out the same way (a set of them
    # raised "unhashable type: 'list'", so no A Link to the Past slot's logic started).
    return {l.address for l in c.multiworld.get_locations(c.player_id) if isinstance(l.address, int)}


def verify(c, expected):
    got = server_ids(c)
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
            locs = server_ids(c)
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
                c.set_items_received([NetworkItem(i, -1, -1, 1) for i in prog])  # progression, as the room flags them
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
    # Each is (source, file, doc, the Archipelago version a YAML's "requires" named that the doc no longer asks for).
    resolved, total = resolved_options(cls, slot_data)
    notes = []
    cands = []

    def overlay(doc):
        doc, relaxed = relax_requires(cls, doc)
        doc = dict(doc)
        doc['name'] = slot_name
        doc['game'] = game
        section = dict(doc.get(game) or {})
        section.update(resolved)
        doc[game] = section
        return doc, relaxed

    if linked_path:
        try:
            doc = pick_doc(load_yaml_docs(linked_path), game, slot_name, False)
            if doc is not None:
                cands.append(('linked', os.path.basename(linked_path)) + overlay(doc))
            else:
                notes.append('The linked YAML has no ' + game + ' player.')
        except Exception as e:
            notes.append('The linked YAML could not be read: ' + str(e))
    if resolved:
        cands.append(('slot_data', None, {'name': slot_name, 'game': game, game: dict(resolved)}, None))
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
                cands.append(('players', fname) + overlay(doc))
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
        # As Archipelago's loader refuses an apworld made for another core (worlds/__init__.py), before anything is swapped.
        refuse_unfit_apworld(path)
        swap_in_apworld(path, game)
        loaded = AutoWorldRegister.world_types.get(game)
        identity = world_identity(loaded) if loaded is not None else {}
        return {'used': loaded is not None, 'file': os.path.basename(path),
                'matches': bool(expected) and identity.get('data_checksum') == expected,
                'world_version': identity.get('world_version')}
    except Exception as e:
        # swap_in_apworld restored the installed copy: the slot carries on with it, and Atlas reports why.
        return {'used': False, 'error': (type(e).__name__ + ': ' + str(e))[:300], 'reason': plain_reason(str(e))[0]}


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
                      'reason': unloaded_reason(game),
                      'apworld_override': override}
    if getattr(cls, 'disable_ut', False):
        return None, {'code': 'ut_disabled', 'message': "This game's author asked for the Universal Tracker not to be used with it."}
    missing_api = ut_api_missing()
    if missing_api:
        return None, {'code': 'ut_incompatible', 'message': 'This Universal Tracker version is incompatible with Atlas (missing: ' +
                      ', '.join(missing_api) + '). Install the tested version from the Atlas Engine page.'}
    players_dir = ensure_players_folder()
    known = bool(req.get('all_entrances_known'))
    if getattr(cls, 'ut_can_gen_without_yaml', False):
        forget_cached_worlds()
        c = new_tracker(TrackerCore, logger, known)
        c.set_slot_params(game, 1, slot_name, 1)
        with fixed_generation_seed(rebuild_seed(req)):
            c.initalize_tracker_core(cls, slot_data)
        if getattr(c, 'tracker_disabled', False):
            return None, {'code': 'ut_disabled', 'message': "This game's author asked for the Universal Tracker not to be used with it."}
        if c.multiworld is None or c.player_id is None:
            error = getattr(c, 'gen_error', None) or 'The world could not be rebuilt from the slot data.'
            reason, reason_code = plain_reason(error)
            return None, {'code': 'generation_failed', 'message': error, 'reason': reason, 'reason_code': reason_code,
                          'apworld_override': override}
        info = {'source': 'not_needed', 'apworld_override': override, '_rebuild': None}
        info.update(verify(c, all_locations))
        return c, info
    cands, notes, used, total = yaml_candidates(cls, game, slot_name, slot_data, req.get('yaml_path'), players_dir)
    attempts = []
    best = None
    for source, fname, doc, relaxed in cands:
        try:
            c, err = generate_with(TrackerCore, logger, cls, game, slot_name, slot_data, doc, known, rebuild_seed(req))
        except Exception as e:
            c, err = None, str(e)
        if c is None:
            # The end of a traceback says what went wrong; its start only where. Its gist, in a few words, goes first.
            text = (err or '').strip()
            reason, reason_code = plain_reason(text)
            attempts.append({'source': source, 'file': fname, 'ok': False, 'reason': reason, 'reason_code': reason_code,
                             'error': text if len(text) <= 600 else '...' + text[-600:]})
            continue
        v = verify(c, all_locations)
        attempt = {'source': source, 'file': fname, 'ok': True}
        if relaxed:
            attempt['relaxed_requires'] = relaxed
        attempt.update(v)
        attempts.append(attempt)
        score = v['missing'] + v['extra']
        if best is None or score < best[0]:
            best = (score, c, source, fname, v, doc, relaxed)
        if v['match'] is not False:
            break  # this YAML rebuilds exactly the server's locations
    if best is None:
        code = 'generation_failed' if cands else 'yaml_needed'
        message = ('None of the YAMLs tried could rebuild this world.' if cands else
                   game + " needs this player's YAML: its slot data doesn't include the options.")
        # Why, in a few words: the linked YAML's failure first (the one the user chose), else the first one; with no YAML, the notes.
        failed = [a for a in attempts if not a['ok']]
        first = next((a for a in failed if a['source'] == 'linked'), failed[0] if failed else None)
        return None, {'code': code, 'message': message,
                      'reason': first.get('reason') if first else short_note(notes[0] if notes else None),
                      'reason_code': first.get('reason_code') if first else None,
                      'attempts': attempts, 'notes': notes, 'apworld_override': override}
    info = {'source': best[2], 'file': best[3], 'options_from_slot_data': used, 'options_total': total,
            'attempts': attempts, 'notes': notes, 'apworld_override': override, '_rebuild': best[5]}
    if best[6]:
        info['relaxed_requires'] = best[6]
    info.update(best[4])
    return best[1], info


def build_check_core(req, logger, cls, doc, salt=':check'):
    # A second rebuild of the slot's world, as the first was built (from its slot data alone, or from the YAML doc) but
    # with another generation seed. A world that restores every choice of the seed from its slot data comes out the
    # same; one that leaves a choice to chance (a song list, a course order, a shop's prices) comes out otherwise, and
    # where the two disagree about a location, Atlas can't tell which is the seed's.
    from worlds.tracker.TrackerCore import TrackerCore
    seed = rebuild_seed(req, salt)
    known = bool(req.get('all_entrances_known'))
    game, slot_name, slot_data = req.get('game'), req.get('player_name'), req.get('slot_data') or {}
    if doc is None:
        ensure_players_folder()  # the tracker writes its log page there (KeyError 'ut_status' without it)
        forget_cached_worlds()
        c = new_tracker(TrackerCore, logger, known)
        c.set_slot_params(game, 1, slot_name, 1)
        with fixed_generation_seed(seed):
            c.initalize_tracker_core(cls, slot_data)
        if c.multiworld is None or c.player_id is None:
            return None, getattr(c, 'gen_error', None) or 'The world could not be rebuilt a second time.'
        return c, None
    return generate_with(TrackerCore, logger, cls, game, slot_name, slot_data, doc, known, seed)


def unsure_locations(slot, items, reachable, missing=None):
    # The missing locations whose logic another rebuild sees otherwise with the same items (none without one).
    unsure = set()
    for check in slot.check_cores:
        other, _ = reachable_after(check, items, missing if missing is not None else slot.core.missing_locations)
        unsure |= set(reachable) ^ set(other)
    return sorted(unsure)


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


def network_item(entry):
    # An item as Atlas sends it: its id, or [id, flags] with the room's flags (1 progression, 2 useful, 4 trap).
    # Universal Tracker counts an item as progression from those flags, as its own client does: a world's create_item
    # alone often classes a seed's progression items otherwise (VVVVVV's trinkets, Pokemon's badges), and then they
    # opened nothing.
    from NetUtils import NetworkItem
    if isinstance(entry, (list, tuple)):
        return NetworkItem(int(entry[0]), -1, -1, int(entry[1]) if len(entry) > 1 else 0)
    return NetworkItem(int(entry), -1, -1, 0)


def reachable_after(core, item_ids, missing_locations=None):
    # The location ids in logic with these items received (the same query the live 'update' answers). Each item is an
    # id or [id, flags] (network_item).
    if missing_locations is not None:
        core.set_missing_locations(set(missing_locations))
    core.set_items_received([network_item(i) for i in item_ids])
    state = core.updateTracker()
    # By the tracker's own ids (locations_available), not its names: a world may name a location object otherwise
    # than its data package does (Resident Evil 3 Remake's "Victory" is "Helipad - Victory" to the server), and a name
    # looked up in the data package missed it.
    available = getattr(core, 'locations_available', None)
    if available is None:
        # A tracker without its id list: by name, as before.
        world = core.get_current_world()
        return [world.location_name_to_id[n] for n in getattr(state, 'in_logic_locations', None) or [] if n in world.location_name_to_id], state
    return [int(a) for a in available if isinstance(a, int)], state


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
    # One slot's world in this engine: its tracker core, and the state of its last answer ('explain' answers about it):
    # the items it was for, what was in logic with them, and the tracker's state.
    def __init__(self, core):
        self.core = core
        self.last_items = None
        self.last_reachable = None
        self.last_state = None
        self.last_glitch_state = None
        self.goal_unknown_noted = False
        self.entrance_keys = []
        # The second rebuild (handle_check), and what it takes: the start request and how the world was built.
        self.check_cores = []
        self.start_req = None
        self.rebuild = None
        self.world_cls = None
        # The locations the seed excluded (fixed once the world is built: read once, at init or the first answer).
        self.excluded = None


def found_entrance_keys(core, req):
    # The room's data storage keys a world reads the entrances the player found from (found_entrances_datastorage_key,
    # with {player} and {team} filled in), as Universal Tracker's own client asks for them. Such a world leaves those
    # entrances unconnected until it hears of them (reconnect_found_entrances), so without them whatever lies behind
    # stays out of logic. None when the world has none, or when entrances aren't deferred.
    world = core.get_current_world()
    keys = getattr(world, 'found_entrances_datastorage_key', None)
    if not keys or not callable(getattr(world, 'reconnect_found_entrances', None)):
        return []
    try:
        from worlds.tracker import DeferredEntranceMode
        if core.enforce_deferred_connections == DeferredEntranceMode.disabled:
            return []
    except Exception as e:
        note_once('Whether entrances wait for the game could not be read: ' + type(e).__name__ + ': ' + str(e))
    if req.get('slot') is None or req.get('team') is None:
        note_once("This world learns its entrances from the room, but the slot's number or team wasn't given: they stay unknown.")
        return []
    if isinstance(keys, str):
        keys = [keys]
    found = []
    for key in keys:
        try:
            found.append(str(key).format(player=req['slot'], team=req['team']))
        except Exception as e:
            note('A data storage key the world names could not be filled in (' + str(key) + '): ' + type(e).__name__ + ': ' + str(e))
    return found


def handle_entrances(slot, req):
    # The room's values for the slot's found-entrance keys ("values": key -> value), passed to the world as Universal
    # Tracker's client passes them; then what's in logic with the slot's items ("base"). A value the world can't take
    # is reported, the others still apply.
    world = slot.core.get_current_world()
    applied, errors = [], []
    for key, value in (req.get('values') or {}).items():
        if key not in slot.entrance_keys:
            errors.append(key + ': not one of the keys this world reads')
            continue
        try:
            world.reconnect_found_entrances(key, value)
            for check in slot.check_cores:
                check.get_current_world().reconnect_found_entrances(key, value)
            applied.append(key)
        except Exception as e:
            errors.append(key + ': ' + type(e).__name__ + ': ' + str(e))
            note('The world could not take the entrances found under ' + key + ': ' + type(e).__name__ + ': ' + str(e))
    # New connections change what's reachable with the same items: nothing kept from before counts.
    slot.last_items = None
    slot.last_reachable = None
    items = list(req.get('base') or [])
    reachable, state = reachable_after(slot.core, items, req.get('missing_locations'))
    reply = {'reachable': reachable, 'applied': applied, 'errors': errors, 'unsure': unsure_locations(slot, items, reachable)}
    reply.update(report_state(slot, items, reachable, state))
    return reply


def handle_init(req, logger):
    # Rebuilds the slot's world. Returns the reply, and the slot (None when it couldn't start: the engine then
    # answers nothing about logic).
    all_locations = req.get('all_locations') or []
    core, info = start_slot(req, logger)
    if core is None:
        reply = {'status': 'error', 'game': req.get('game'), 'versions': versions()}
        reply.update(info)
        # What the world that couldn't start is (its data checksum and version): a seed made with another version may
        # want its own (Atlas's connect-time version rule). A missing world has none.
        from worlds.AutoWorld import AutoWorldRegister
        failed_cls = AutoWorldRegister.world_types.get(req.get('game'))
        if failed_cls is not None:
            reply.update(world_identity(failed_cls))
        return reply, None
    if all_locations:
        core.set_missing_locations(set(all_locations))
    from worlds.AutoWorld import AutoWorldRegister
    slot = Slot(core)
    slot.entrance_keys = found_entrance_keys(core, req)
    slot.start_req = dict(req)
    slot.rebuild = info.pop('_rebuild', None)
    # Fixed once the world is built: what the seed excluded, and its priority checks (those the server has).
    slot.excluded = progress_ids(core, 'EXCLUDED')
    priority = priority_ids(core, slot.excluded)
    if all_locations:
        priority &= set(all_locations)
    reply = {'status': 'ready', 'item_pool': item_pool(core), 'yaml': info, 'versions': versions(), 'entrance_keys': slot.entrance_keys,
             # The server's locations the rebuilt world doesn't have: logic can't say anything about them.
             'missing_locations': sorted(set(all_locations) - server_ids(core)) if all_locations else [],
             'priority': sorted(priority), 'excluded': sorted(slot.excluded)}
    world_cls = AutoWorldRegister.world_types.get(req.get('game'))
    slot.world_cls = world_cls
    if world_cls is not None:
        reply.update(world_identity(world_cls))
    return reply, slot


def progress_ids(core, kind):
    # The rebuilt world's locations of one progress type ('EXCLUDED', 'PRIORITY'), by their server ids.
    found = set()
    for loc in core.multiworld.get_locations(getattr(core, 'player_id', None) or 1):
        progress = getattr(loc, 'progress_type', None)
        if progress is not None and getattr(progress, 'name', '') == kind and isinstance(loc.address, int):
            found.add(loc.address)
    return found


def priority_ids(core, excluded):
    # The seed's priority checks: what the world marked PRIORITY itself (Elden Ring's priority_location_groups, in
    # create_items), and the standard priority_locations, which Archipelago's Main applies after set_rules and Universal
    # Tracker doesn't: minus exclude_locations, and never an excluded one (as Main leaves it). A world that marks them only
    # while filling isn't seen (Universal Tracker stops at generate_basic).
    found = progress_ids(core, 'PRIORITY')
    try:
        options = core.get_current_world().options
        names = set(options.priority_locations.value) - set(options.exclude_locations.value)
    except Exception:
        names = set()
    if names:
        for loc in core.multiworld.get_locations(getattr(core, 'player_id', None) or 1):
            if loc.name in names and isinstance(loc.address, int) and loc.address not in excluded:
                found.add(loc.address)
    return found


def report_state(slot, items, reachable, state):
    # The slot's answer for these items: what the seed excluded, what's reachable only with glitches, and whether the
    # goal is. Kept as the slot's last answer ('explain' answers about this exact state; 'steps' starts from it).
    core = slot.core
    if slot.excluded is None:
        slot.excluded = progress_ids(core, 'EXCLUDED')  # fixed once the world is built: read once (the seed test's probe Slot too)
    excluded = sorted(slot.excluded)
    glitched = [int(a) for a in (getattr(core, 'glitched_locations', None) or []) if isinstance(a, int)]
    slot.last_items = list(items)
    slot.last_reachable = set(reachable)
    slot.last_state = getattr(state, 'state', None)
    slot.last_glitch_state = getattr(state, 'glitches_state', None)
    goal = goal_reachable(core, state)
    if goal is None and not slot.goal_unknown_noted:
        slot.goal_unknown_noted = True
        note("This slot's goal can't be checked: go mode isn't shown for it.")
    return {'excluded': excluded, 'glitched': glitched, 'goal': goal}


def handle_update(slot, req):
    # What's in logic with these items: the same query the seed test checks against real playthroughs.
    items = req.get('items', [])
    reachable, state = reachable_after(slot.core, items, req.get('missing_locations'))
    reply = {'reachable': reachable, 'unsure': unsure_locations(slot, items, reachable)}
    reply.update(report_state(slot, items, reachable, state))
    return reply


def build_checks(start_req, logger, cls, doc, core):
    # The check rebuilds for a slot whose world is `core`. One is built first: when it comes out the same as the first
    # rebuild (no option, value, entrance or placed item differs, and logic agrees with no items, half the progression
    # items and all of them), the world restores the seed's choices and no check is kept (a slot pays nothing more).
    # When it differs, two more are built: more draws catch more of the choices a seed could have made (on the logic
    # batch, Muse Dash's every difference fell inside the unsure locations with three, about half with one).
    # Returns (checks kept, what differs, error).
    first, err = build_check_core(start_req, logger, cls, doc, ':check')
    if first is None:
        return [], None, err
    try:
        differs = world_differences(core.get_current_world(), first.get_current_world())
    except Exception as e:
        differs = {'error': type(e).__name__ + ': ' + str(e)}
    summary = {k: differs.get(k) for k in ('options_count', 'values_count', 'entrances_count', 'locked_count', 'error')}
    summary['examples'] = [x['name'] for key in ('options', 'values', 'entrances', 'locked') for x in (differs.get(key) or [])][:6]
    same = not any(summary.get(k) for k in ('options_count', 'values_count', 'entrances_count', 'locked_count', 'error'))
    # Logic itself, with no items, half the progression items and all of them: rebuilds that differ only in details
    # logic doesn't use agree here (their checks are kept all the same: a state in play may tell them apart).
    progression = [[i['id'], 1] for i in item_pool(core) if i['flags'] & 1]
    missing = set(core.missing_locations) or server_ids(core)  # a slot's missing locations; every location without a slot
    summary['logic_differs'] = False
    for items in ([], progression[:len(progression) // 2], progression):
        a, _ = reachable_after(core, items, missing)
        b, _ = reachable_after(first, items, missing)
        if set(a) != set(b):
            same = False
            summary['logic_differs'] = True
            break
    summary['same'] = same
    if same:
        return [], summary, None
    checks = [first]
    for n in (2, 3):
        more, err = build_check_core(start_req, logger, cls, doc, ':check' + str(n))
        if more is None:
            break  # the ones built still count
        checks.append(more)
    return checks, summary, None


def handle_check(slot, req, logger):
    # Builds the slot's check rebuilds (build_checks) and says what differs, and the locations whose logic differs now
    # with the slot's items ("base"). From then on every logic answer says what's unsure.
    import time
    started = time.time()
    if slot.world_cls is None or slot.start_req is None:
        return {'check': 'failed', 'error': "The slot's world isn't known."}
    try:
        checks, summary, err = build_checks(slot.start_req, logger, slot.world_cls, slot.rebuild, slot.core)
    except Exception as e:
        checks, summary, err = [], None, type(e).__name__ + ': ' + str(e)
    slot.check_cores = checks
    if summary is None:
        text = (err or '').strip()
        return {'check': 'failed', 'error': text if len(text) <= 600 else '...' + text[-600:], 'seconds': round(time.time() - started, 1)}
    items = list(req.get('base') or [])
    missing = req.get('missing_locations')
    reachable, state = reachable_after(slot.core, items, missing)
    reply = {'check': 'built', 'differs': summary, 'rebuilds': len(checks),
             'unsure': unsure_locations(slot, items, reachable, set(missing) if missing is not None else None),
             'seconds': round(time.time() - started, 1)}
    slot.last_items = None  # the check rebuilds left the tracker's state elsewhere
    reply.update(report_state(slot, items, reachable, state))
    return reply


def handle_steps(slot, req):
    # What each new item opens, in the order they arrived, in one request: for each, the locations in logic after it
    # that weren't before it (each the same query as 'update'). "base" is what the slot had before them; with "start",
    # what's in logic with just that comes back too. A slot's last answer is kept, so when it was for the base it isn't
    # worked out again (checks since then only take locations away).
    core = slot.core
    base = list(req.get('base') or [])
    missing = req.get('missing_locations')
    if missing is not None:
        core.set_missing_locations(set(missing))
    state = None
    if slot.last_reachable is not None and slot.last_items == base:
        before = slot.last_reachable & set(core.missing_locations)
    else:
        reachable, state = reachable_after(core, base)
        before = set(reachable)
    reply = {}
    if req.get('start'):
        reply['start'] = sorted(before)
    steps = []
    inventory = list(base)
    for item in req.get('items') or []:
        inventory.append(item)
        reachable, state = reachable_after(core, inventory)
        now = set(reachable)
        steps.append(sorted(now - before))
        before = now
    if state is None:
        # Nothing new, and the base was known: its state is still needed (glitches, the goal, 'explain').
        reachable, state = reachable_after(core, inventory)
        before = set(reachable)
    reply['steps'] = steps
    reply['unsure'] = unsure_locations(slot, inventory, before)
    reply.update(report_state(slot, inventory, before, state))
    return reply


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
    # By its id (its object may be named otherwise than in the data package).
    loc = next((l for l in mw.get_locations(player) if l.address == req.get('location')), None) or mw.get_location(loc_name, player)
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


def forget_cached_worlds():
    # The Universal Tracker keeps every world it rebuilds from slot data in lists on its class, shared by every slot in
    # the process, and gives a slot with the same slot data another's world. Each slot here builds its own world, and
    # one a slot no longer uses must be freed, so the lists are emptied before each start and after a slot leaves.
    from worlds.tracker.TrackerCore import TrackerCore
    found = False
    for name in ('cached_multiworlds', 'cached_slot_data'):
        cache = getattr(TrackerCore, name, None)
        if isinstance(cache, list):
            cache.clear()
            found = True
    if not found:
        note_once("The Universal Tracker's world cache wasn't found, so it isn't emptied: worlds it rebuilt may stay in memory.")


def launch_bridge(*args):
    # Atlas's logic engine: one request per line on stdin, one answer per line on Atlas's channel, each carrying its
    # request's id. It hosts the slots of one multiworld (they share the seed's apworld versions); a request names its
    # slot with "key" (requests without one share one slot). A request that fails is answered with the error; one the
    # bridge doesn't understand, too.
    protect_channel()
    logger = logging.getLogger('UltimateBridge')
    try:
        from worlds.tracker.TrackerCore import TrackerCore  # noqa: F401  the tracker must load, or no slot can start
    except Exception as e:
        send({'event': 'boot_failed', 'error': type(e).__name__ + ': ' + str(e), 'trace': traceback.format_exc()})
        return
    slots = {}  # by key: each built at 'init', from the slot's own YAML only (other YAMLs can't break it)
    while True:
        line = sys.stdin.readline()
        if not line:
            break  # Atlas closed the pipe: it stopped the engine
        rid = None
        try:
            req = json.loads(line)
            rid = req.get('id')
            action = req.get('action')
            key = req.get('key')
            if action == 'init':
                forget_cached_worlds()
                reply, started = handle_init(req, logger)
                if started is not None:
                    slots[key] = started
                else:
                    slots.pop(key, None)
            elif action == 'drop':
                # The slot left: its world goes.
                slots.pop(key, None)
                forget_cached_worlds()
                reply = {'dropped': True}
            elif action in ('update', 'steps', 'explain', 'entrances', 'check'):
                slot = slots.get(key)
                if slot is None:
                    reply = {'error': 'The logic engine has not been started for a slot yet.'}
                elif action == 'update':
                    reply = handle_update(slot, req)
                elif action == 'steps':
                    reply = handle_steps(slot, req)
                elif action == 'entrances':
                    reply = handle_entrances(slot, req)
                elif action == 'check':
                    reply = handle_check(slot, req, logger)
                else:
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
            # Paths are kept relative to the index's folder (older entries have full paths).
            path = e.get('File') or ''
            if path and not os.path.isabs(path):
                path = os.path.normpath(os.path.join(os.path.dirname(index_path), path))
            if str(e.get('Game', '')).lower() == str(game).lower() and e.get('Checksum') == checksum and os.path.isfile(path):
                return path
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
            core, start = start_slot({'game': game, 'player_name': name, 'seed_name': data.get('seed_name'), 'slot': player,
                                      'slot_data': (data.get('slot_data') or {}).get(player) or {},
                                      'all_locations': locs,
                                      'yaml_path': (req.get('yaml_paths') or {}).get(str(player)),
                                      'apworld_override': override, 'expected_checksum': expected_checksum,
                                      # The seed's spheres assume every entrance where the generator put it.
                                      'all_entrances_known': True}, logger)
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
            # What a live slot treats as unsure: the server's locations the rebuilt world lacks, and (with "check") those
            # whose logic a second rebuild with another seed sees otherwise. Late and early count the rest.
            lacking = set(locs) - server_ids(core)
            probe = None
            if req.get('check'):
                start_req = {'game': game, 'player_name': name, 'seed_name': data.get('seed_name'), 'slot': player,
                             'slot_data': (data.get('slot_data') or {}).get(player) or {}, 'all_entrances_known': True}
                try:
                    checks, summary, err = build_checks(start_req, logger, AutoWorldRegister.world_types.get(game), start.get('_rebuild'), core)
                except Exception as e:
                    checks, summary, err = [], None, type(e).__name__ + ': ' + str(e)
                probe = Slot(core) if summary is not None else None
                if probe is not None:
                    probe.check_cores = checks
                result['rebuilds'] = len(checks)
                result['differs'] = summary
                result['check'] = 'built' if probe is not None else 'failed: ' + str(err)[-300:]
            inventory = list(precollected.get(player, []))
            reached = set()
            steps = []
            unsure_ever = set()
            for i, sphere in enumerate(spheres):
                reached |= set(sphere.get(player, ()))
                reach, tracked = reachable_after(core, inventory, locs)
                reach = set(reach)
                unsure = set(lacking)
                if probe is not None:
                    unsure |= set(unsure_locations(probe, inventory, reach, set(locs)))
                unsure_ever |= unsure
                late_all, early_all = reached - reach, reach - reached
                late, early = late_all - unsure, early_all - unsure
                last_late, last_late_all = late, late_all
                steps.append({'sphere': i, 'expected': len(reached), 'reachable': len(reach),
                              'late_count': len(late), 'early_count': len(early),
                              'late': [names.get(l, str(l)) for l in sorted(late)[:15]],
                              'early': [names.get(l, str(l)) for l in sorted(early)[:15]],
                              'unsure_count': len(unsure), 'late_all': len(late_all), 'early_all': len(early_all)})
                if req.get('debug'):
                    # What was fed, and what the tracker counted from it (for finding why a step differs).
                    item_names = getattr(world, 'item_id_to_name', {}) or {}
                    fed = collections.Counter(item_names.get(i[0] if isinstance(i, list) else i, str(i)) for i in inventory)
                    steps[-1]['fed'] = dict(fed)
                    steps[-1]['tracked'] = dict(getattr(tracked, 'prog_items', None) or {})
                    steps[-1]['tracked_all'] = dict(getattr(tracked, 'all_items', None) or {})
                    steps[-1]['tracker_errors'] = [str(getattr(line, 'text', line)) for lines in (getattr(core, 'log_lines', None) or {}).values() for line in lines if 'ERROR' in str(getattr(line, 'group', ''))][:5]
                # The items found in this sphere that belong to this player (from any player's world).
                for finder, found in sphere.items():
                    for loc in found:
                        entry = all_locations.get(finder, {}).get(loc)
                        if entry is not None and entry[1] == player:
                            # With the room's flags, as a live slot receives it.
                            inventory.append([entry[0], entry[2] if len(entry) > 2 else 0])
            # The generator lists a seed's unreachable locations (accessibility other than "full") as a last "sphere" of
            # their own: they aren't late if Atlas can't reach them either with every item of the seed.
            accessibility = getattr(getattr(getattr(world, 'options', None), 'accessibility', None), 'current_key', None)
            if steps and spheres and accessibility not in (None, 'full'):
                everything, _ = reachable_after(core, inventory, locs)
                bucket = set(spheres[-1].get(player, ())) - set(everything)
                if bucket and bucket <= last_late_all:
                    last = steps[-1]
                    last['unreachable'] = len(bucket)
                    last['late_count'] = len(last_late - bucket)
                    last['late_all'] = len(last_late_all - bucket)
                    last['late'] = [names.get(l, str(l)) for l in sorted(last_late - bucket)[:15]]
                    result['unreachable'] = len(bucket)
            result['steps'] = steps
            result['exact'] = bool(steps) and all(s['late_count'] == 0 and s['early_count'] == 0 for s in steps)
            # Without setting the unsure ones apart (the logic a slot would show with "Show logic anyway").
            result['exact_all'] = bool(steps) and all(s.get('late_all', 0) == 0 and s.get('early_all', 0) == 0 for s in steps)
            result['unsure'] = len(unsure_ever)
            result['seconds'] = round(time.time() - started, 1)
    except Exception:
        out['error'] = traceback.format_exc()[-1500:]
    send(out)


def plain_value(value, depth=0):
    # A value as JSON-like data, for comparing two worlds: numbers, text, lists, sets (sorted) and mappings, enums by
    # value. None for anything else (an object of the world's: a region, a location, the multiworld).
    import enum
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, enum.Enum):
        return plain_value(value.value, depth)
    if depth > 4:
        return '...'
    if isinstance(value, dict):
        out = {}
        for k, v in value.items():
            pk, pv = plain_value(k, depth + 1), plain_value(v, depth + 1)
            if pk is None or (pv is None and v is not None):
                return None
            out[str(pk)] = pv
        return out
    if isinstance(value, (list, tuple, set, frozenset)):
        items = [plain_value(v, depth + 1) for v in value]
        if any(i is None and v is not None for i, v in zip(items, value)):
            return None
        return sorted(items, key=repr) if isinstance(value, (set, frozenset)) else items
    return None


def short_value(value):
    text = json.dumps(value, sort_keys=True, default=str)
    return text if len(text) <= 300 else text[:300] + '...'


def tracker_support(cls):
    # What the world does for Universal Tracker: rebuilds from its slot data (interpret_slot_data, and its code reading
    # re_gen_passthrough), or without a YAML.
    import inspect
    package = cls.__module__.split('.')[:2]
    reads = False
    for name, module in list(sys.modules.items()):
        if name.split('.')[:2] == package:
            try:
                if 're_gen_passthrough' in inspect.getsource(module):
                    reads = True
                    break
            except (OSError, TypeError):
                pass
    return {'interpret_slot_data': callable(getattr(cls, 'interpret_slot_data', None)), 'reads_passthrough': reads,
            'without_yaml': bool(getattr(cls, 'ut_can_gen_without_yaml', False))}


def world_differences(seed_world, rebuilt_world):
    # What differs between the generator's world and the rebuilt one: option values, the world's own values (plain
    # data only; a rebuilt world's tracker flags left out) and where each entrance leads.
    out = {'options': [], 'values': [], 'entrances': []}
    seed_opts, rebuilt_opts = getattr(seed_world, 'options', None), getattr(rebuilt_world, 'options', None)
    for name in sorted(set(vars(seed_opts) if seed_opts else ()) | set(vars(rebuilt_opts) if rebuilt_opts else ())):
        a = plain_value(getattr(getattr(seed_opts, name, None), 'value', None))
        b = plain_value(getattr(getattr(rebuilt_opts, name, None), 'value', None))
        if a != b:
            out['options'].append({'name': name, 'seed': short_value(a), 'rebuilt': short_value(b)})
    ignore = {'options', 'multiworld', 'random', 'player'}
    for name in sorted(set(vars(seed_world)) | set(vars(rebuilt_world))):
        if name.startswith('__') or name in ignore:
            continue
        a, b = plain_value(getattr(seed_world, name, None)), plain_value(getattr(rebuilt_world, name, None))
        if a is None and b is None:
            continue
        if a != b:
            out['values'].append({'name': name, 'seed': short_value(a), 'rebuilt': short_value(b)})

    def leads(world):
        found = {}
        for region in world.multiworld.get_regions(world.player):
            for exit in region.exits:
                found[exit.name] = exit.connected_region.name if exit.connected_region else None
        return found
    try:
        a, b = leads(seed_world), leads(rebuilt_world)
        for name in sorted(set(a) | set(b)):
            if a.get(name, '(none)') != b.get(name, '(none)'):
                out['entrances'].append({'name': name, 'seed': a.get(name, '(none)'), 'rebuilt': b.get(name, '(none)')})
    except Exception as e:
        out['entrances_error'] = type(e).__name__ + ': ' + str(e)
    out['locked'] = []

    def locked(world):
        # Items a world places itself (locked), by location: a random placement here changes what's reachable.
        return {l.name: l.item.name for l in world.multiworld.get_locations(world.player) if getattr(l, 'locked', False) and l.item is not None}
    try:
        a, b = locked(seed_world), locked(rebuilt_world)
        for name in sorted(set(a) | set(b)):
            if a.get(name) != b.get(name):
                out['locked'].append({'name': name, 'seed': a.get(name), 'rebuilt': b.get(name)})
    except Exception as e:
        out['locked_error'] = type(e).__name__ + ': ' + str(e)
    for key in ('options', 'values', 'entrances', 'locked'):
        out[key + '_count'] = len(out[key])
        out[key] = out[key][:40]
    return out


def atlas_seed_replay(*args):
    # Development: tells a difference the seed test finds in Atlas's logic (the world rebuilt from the slot's data, as a
    # live slot has it) from one in the test itself. The seed is generated again here from its players folder and seed
    # number (the same seed comes out), and its spheres are replayed against the generator's own world: with the items
    # of the spheres before i collected, the reachable set must be spheres 0..i. Exact here but not in the seed test: the
    # rebuilt world isn't the seed's (the world doesn't rebuild its random choices from its slot data, or its logic needs
    # more than Atlas gives it). Differing here too: the test's reading of the spheres.
    # stdin: {"multidata": path, "players_dir": folder, "seed": number, "player": 1}.
    import time
    import tempfile as _tempfile
    protect_channel()
    _log_to_stderr()
    req = _read_request()
    out = {}
    started = time.time()
    work = _tempfile.mkdtemp(prefix='atlas_replay_')
    try:
        import Generate
        import Main
        from BaseClasses import CollectionState
        data = load_multidata(req['multidata'])
        player = int(req.get('player') or 1)
        spheres = data.get('spheres') or []
        _skip_patch_output(req.get('skip_patch_games'))
        argv = ['--player_files_path', req['players_dir'], '--outputpath', work, '--spoiler', '0', '--multi', '1', '--seed', str(req['seed'])]
        gen_args, seed = Generate.mystery_argparse(argv), None
        gen_args, seed = Generate.main(gen_args)
        multiworld = Main.main(gen_args, seed)
        out['seed_name'] = multiworld.seed_name
        out['same_seed'] = multiworld.seed_name == data.get('seed_name')
        world = multiworld.worlds[player]
        names = getattr(world, 'location_id_to_name', {}) or {}
        by_address = {l.address: l for l in multiworld.get_locations(player) if isinstance(l.address, int)}
        everywhere = {(l.player, l.address): l for l in multiworld.get_locations() if isinstance(l.address, int)}
        all_locations = data['locations']
        steps = []
        found = []
        reached = set()
        for i, sphere in enumerate(spheres):
            reached |= set(sphere.get(player, ()))
            state = CollectionState(multiworld)
            for item in found:
                state.collect(item, True)
            state.sweep_for_advancements(locations=[l for l in multiworld.get_locations() if not isinstance(l.address, int) or l.item is None or not isinstance(l.item.code, int)])
            reach = {a for a, l in by_address.items() if l.can_reach(state)}
            late, early = reached - reach, reach - reached
            steps.append({'sphere': i, 'expected': len(reached), 'reachable': len(reach), 'late_count': len(late), 'early_count': len(early),
                          'late': [names.get(l, str(l)) for l in sorted(late)[:15]], 'early': [names.get(l, str(l)) for l in sorted(early)[:15]]})
            for finder, locs in sphere.items():
                for loc in locs:
                    location = everywhere.get((finder, loc))
                    if location is not None and location.item is not None:
                        found.append(location.item)
        out['steps'] = steps
        out['exact'] = bool(steps) and all(s['late_count'] == 0 and s['early_count'] == 0 for s in steps)
        out['late'] = sum(s['late_count'] for s in steps)
        out['early'] = sum(s['early_count'] for s in steps)
        out['accessibility'] = getattr(getattr(world.options, 'accessibility', None), 'current_key', None)
        if req.get('compare'):
            # The world rebuilt as the seed test rebuilds it (a live slot's way), compared with the generator's.
            slot = data['slot_info'][player]
            core, start = start_slot({'game': slot.game, 'player_name': slot.name, 'seed_name': data.get('seed_name'), 'slot': player,
                                      'slot_data': (data.get('slot_data') or {}).get(player) or {},
                                      'all_locations': sorted(data['locations'][player].keys()),
                                      'yaml_path': req.get('yaml_path'), 'all_entrances_known': True},
                                     logging.getLogger('AtlasSeedReplay'))
            if core is None:
                out['compare'] = {'error': start}
            else:
                out['compare'] = world_differences(world, core.get_current_world())
                out['compare']['tracker_support'] = tracker_support(type(world))
                out['compare']['rebuild'] = {k: start.get(k) for k in ('source', 'file', 'match', 'missing', 'extra')}
        out['seconds'] = round(time.time() - started, 1)
    except BaseException as e:
        if isinstance(e, KeyboardInterrupt):
            raise
        out['error'] = traceback.format_exc()[-1500:]
    finally:
        shutil.rmtree(work, ignore_errors=True)
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
    # The data checksum of a candidate apworld file, to find the version a seed was made with, and with "names" its
    # location and item tables (to measure a map pack against that release).
    # stdin: {"apworld": path, "game": expected game (optional), "names": bool}. Runs in a throwaway process.
    protect_channel()
    line = sys.stdin.readline()
    req = json.loads(line) if line else {}
    out = {}
    try:
        from worlds.AutoWorld import AutoWorldRegister
        game = swap_in_apworld(req['apworld'], req.get('game'))
        out['game'] = game
        cls = AutoWorldRegister.world_types[game]
        out.update(world_identity(cls))
        out['location_count'] = len(cls.location_name_to_id)
        if req.get('names'):
            out['locations'] = dict(cls.location_name_to_id)
            out['items'] = dict(cls.item_name_to_id)
    except Exception:
        out['error'] = traceback.format_exc()[-800:]
    send(out)


def sweep_trust(cls, game, c, doc, logger):
    # Whether a slot of this game gets the seed's logic: the slot data the world writes for this rebuild (as a seed's
    # would carry it), then the world rebuilt from it as a slot is (start_slot's way) and checked with other seeds
    # (build_checks). "same": every choice comes back from the slot data.
    try:
        written = c.get_current_world().fill_slot_data() or {}
        slot_data = json.loads(json.dumps(written, default=lambda o: getattr(o, 'value', str(o))))
    except Exception as e:
        return {'error': "its slot data can't be written without a whole seed (" + type(e).__name__ + ')'}
    req = {'game': game, 'player_name': 'AtlasSweep', 'slot_data': slot_data, 'seed_name': 'atlas-sweep', 'slot': 1}
    rebuild_doc = None if getattr(cls, 'ut_can_gen_without_yaml', False) else doc
    try:
        main, err = build_check_core(req, logger, cls, rebuild_doc, '')
        if main is None:
            return {'error': "it couldn't be rebuilt from its slot data (" + str(err or '').strip()[-200:] + ')'}
        checks, summary, err = build_checks(req, logger, cls, rebuild_doc, main)
    except Exception as e:
        return {'error': type(e).__name__ + ': ' + str(e)[:200], 'trace': traceback.format_exc()[-800:]}
    if summary is None:
        return {'error': "it couldn't be rebuilt a second time (" + str(err or '').strip()[-200:] + ')'}
    return {'same': bool(summary.get('same')), 'logic_differs': bool(summary.get('logic_differs')), 'examples': summary.get('examples') or [],
            'rebuilds': len(checks)}


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
                    doc = {'name': 'AtlasSweep', 'game': game, game: {}}
                    c, err = generate_with(TrackerCore, logger, cls, game, 'AtlasSweep', {}, doc)
                    if c is None:
                        out['error'] = err or 'the world could not be generated with default options'
                    else:
                        locs = server_ids(c)
                        reach, _ = reachable_after(c, [], locs)
                        out['locations'] = len(locs)
                        out['in_logic'] = len(reach)
                        out['ok'] = len(locs) > 0
                        if req.get('trust'):
                            out['trust'] = sweep_trust(cls, game, c, doc, logger)
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


# ---- The solo test: a YAML from the game's own options, a one-player seed, a server on this PC ----
# Three components the "Test this game…" chain runs (Scripts/Core/Engine/SoloTest.cs). They use Archipelago's own
# generator and server inside the engine, so a solo seed is made and hosted without archipelago.gg; the server binds the
# loopback address only.


def _log_to_stderr():
    # Archipelago's generator and server log through the root logger: a handler on standard error makes their progress
    # lines reach Atlas's live log line (the channel stays for answers).
    handler = logging.StreamHandler(sys.stderr)
    handler.setFormatter(logging.Formatter('%(message)s'))
    root = logging.getLogger()
    root.setLevel(logging.INFO)
    root.addHandler(handler)


def _read_request():
    line = sys.stdin.readline()
    return json.loads(line) if line and line.strip() else {}


def _failure(error, **extra):
    out = {'ok': False, 'error': (type(error).__name__ + ': ' + str(error))[:400] if isinstance(error, BaseException) else str(error)[:400],
           'trace': traceback.format_exc()[-1500:]}
    out.update(extra)
    return out


def atlas_yaml_template(*args):
    # A player YAML for one game with every option at its default, from Archipelago's own option templates (the same
    # rendering the Launcher's "Generate Template Options" makes), the player named as asked.
    # stdin: {"game", "player_name", "output_dir"}; one answer line.
    import time
    protect_channel()
    req = _read_request()
    started = time.time()
    game, player_name, output_dir = req.get('game') or '', req.get('player_name') or 'AtlasTest', req.get('output_dir') or ''
    try:
        from worlds.AutoWorld import AutoWorldRegister
        import Options
        from Utils import get_file_safe_name
        cls = AutoWorldRegister.world_types.get(game)
        if cls is None:
            send({'ok': False, 'code': 'world_missing', 'error': game + ' is not installed in this engine.'})
            return
        os.makedirs(output_dir, exist_ok=True)
        text, source = None, 'template'
        try:
            # The templates go to a throwaway folder: generate_yaml_templates writes every world's and clears *.yaml first.
            tmp = tempfile.mkdtemp(prefix='atlas_tpl_')
            try:
                Options.generate_yaml_templates(tmp, True)
                with open(os.path.join(tmp, get_file_safe_name(game) + '.yaml'), encoding='utf-8-sig') as f:
                    text = f.read()
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
            text = re.sub(r'^name:.*$', 'name: ' + player_name, text, count=1, flags=re.M)
            text = re.sub(r'^description:.*$', 'description: Atlas solo test (every option at its default)', text, count=1, flags=re.M)
        except Exception as e:
            note('The option template of ' + game + ' could not be made (' + type(e).__name__ + ': ' + str(e) + '); plain defaults instead.')
            text, source = None, 'defaults'
        import yaml
        if text is None:
            options = {}
            for key, option in cls.options_dataclass.type_hints.items():
                default = getattr(option, 'default', None)
                if isinstance(default, (set, frozenset, tuple)):
                    default = list(default)
                options[key] = default
            text = yaml.safe_dump({'name': player_name, 'description': 'Atlas solo test (every option at its default)', 'game': game, game: options},
                                  sort_keys=False, allow_unicode=True)
        doc = yaml.safe_load(text)
        if not isinstance(doc, dict) or doc.get('name') != player_name or doc.get('game') != game or not isinstance(doc.get(game), dict):
            raise ValueError('the YAML made for ' + game + ' does not name the player, the game and its options as expected')
        path = os.path.join(output_dir, player_name + '.yaml')
        with open(path, 'w', encoding='utf-8-sig') as f:
            f.write(text)
        out = {'ok': True, 'path': path, 'game': game, 'from': source, 'options': len(doc[game]), 'hidden_world': bool(getattr(cls, 'hidden', False)),
               'versions': versions(), 'seconds': round(time.time() - started, 2)}
        out.update(world_identity(cls))
        send(out)
    except Exception as e:
        send(_failure(e, code='template_failed'))


def _no_output(self, output_directory):
    # A world's other output steps can wait for its patch to be written: A Link to the Past's modify_multidata waits on
    # rom_name_available_event, which its generate_output sets as it ends; skipped, the seed's output waited forever. Every
    # event the world holds is set, as its own step does when it ends (an error included).
    import threading
    for value in list(vars(self).values()):
        if isinstance(value, threading.Event):
            value.set()


def _skip_patch_output(games):
    # A world whose output needs a file this PC doesn't have (a base ROM) still generates: its output step is switched off
    # in this process only; the server data and the spoiler are all the tracker needs.
    from worlds.AutoWorld import AutoWorldRegister
    for game in games or []:
        cls = AutoWorldRegister.world_types.get(game)
        if cls is None:
            continue
        cls.stage_assert_generate = classmethod(lambda c, multiworld: None)
        cls.generate_output = _no_output
        if hasattr(cls, 'stage_generate_output'):
            cls.stage_generate_output = classmethod(lambda c, multiworld, output_directory: None)


def atlas_generate(*args):
    # One seed from the players folder, with Archipelago's own generator, into the output folder; the server data
    # (.archipelago) and the spoiler are put beside the zip. stdin: {"players_dir", "output_dir", "spoiler": 3, "seed",
    # "skip_patch_games": [], "skip_prog_balancing": false}; one answer line at the end (progress goes to standard error).
    import time
    import zipfile
    protect_channel()
    _log_to_stderr()
    req = _read_request()
    started = time.time()
    stage = 'setup'
    try:
        import Generate
        import Main
        players_dir, output_dir = req.get('players_dir') or '', req.get('output_dir') or ''
        os.makedirs(output_dir, exist_ok=True)
        _skip_patch_output(req.get('skip_patch_games'))
        argv = ['--player_files_path', players_dir, '--outputpath', output_dir, '--spoiler', str(req.get('spoiler', 3)), '--multi', '1']
        if req.get('seed') is not None:
            argv += ['--seed', str(req['seed'])]
        if req.get('skip_prog_balancing'):
            argv.append('--skip_prog_balancing')
        stage = 'generate'
        gen_args = Generate.mystery_argparse(argv)
        gen_args, seed = Generate.main(gen_args)
        stage = 'output'
        multiworld = Main.main(gen_args, seed)
        seed_name = multiworld.seed_name
        zip_path = os.path.join(output_dir, 'AP_' + seed_name + '.zip')
        beside = os.path.dirname(os.path.abspath(output_dir))
        multidata_path, spoiler_path = None, None
        with zipfile.ZipFile(zip_path) as z:
            for name in z.namelist():
                base = os.path.basename(name)
                if base.lower().endswith('.archipelago') or base.lower().endswith('_spoiler.txt'):
                    target = os.path.join(beside, base)
                    with z.open(name) as src, open(target, 'wb') as dst:
                        shutil.copyfileobj(src, dst)
                    if base.lower().endswith('.archipelago'):
                        multidata_path = target
                    else:
                        spoiler_path = target
        if multidata_path is None:
            raise FileNotFoundError('the generated archive holds no .archipelago file')
        stage = 'read'
        data = load_multidata(multidata_path)
        players = []
        for slot, info in (data.get('slot_info') or {}).items():
            locations = data.get('locations', {}).get(slot) or {}
            players.append({'slot': slot, 'name': getattr(info, 'name', None) or str(info), 'game': getattr(info, 'game', None) or '',
                            'locations': len(locations), 'items': len(locations)})
        spheres = data.get('spheres') or []
        sphere0 = 0
        sphere0_names = []
        if spheres:
            first = spheres[0]
            sphere0 = sum(len(v) for v in first.values()) if isinstance(first, dict) else len(first)
            # The names of sphere 0 (every player's), so Atlas can say which of them its live logic missed.
            if isinstance(first, dict):
                packages = data.get('datapackage') or {}
                for player, ids in first.items():
                    info = (data.get('slot_info') or {}).get(player)
                    game = getattr(info, 'game', None) or ''
                    id_to_name = {v: k for k, v in ((packages.get(game) or {}).get('location_name_to_id') or {}).items()}
                    sphere0_names.extend(id_to_name.get(i, str(i)) for i in ids)
                sphere0_names.sort()
        send({'ok': True, 'seed': seed, 'seed_name': seed_name, 'zip': zip_path, 'multidata': multidata_path, 'spoiler': spoiler_path,
              'players': players, 'spheres': len(spheres), 'sphere0': sphere0, 'sphere0_locations': sphere0_names,
              'patch_skipped': list(req.get('skip_patch_games') or []), 'seconds': round(time.time() - started, 2)})
    except BaseException as e:
        if isinstance(e, KeyboardInterrupt):
            raise
        send(_failure(e, error_type=type(e).__name__, stage=stage, seconds=round(time.time() - started, 2)))


def atlas_host(*args):
    # Archipelago's own server for a generated seed, on this PC only (127.0.0.1, a free port the OS picks, no password,
    # nothing saved); long-lived: it answers once with the port and then serves until Atlas ends the process.
    # stdin: {"id", "multidata", "host": "127.0.0.1", "port": 0}; answer: {"id", "event": "hosting", ...} or "host_failed".
    protect_channel()
    _log_to_stderr()
    req = _read_request()
    rid = req.get('id')
    try:
        import asyncio
        import functools
        import MultiServer
        import websockets
        args = MultiServer.parse_args()  # host.yaml's defaults (sys.argv holds the launcher's name alone)
        args.multidata = req.get('multidata')
        args.host = '127.0.0.1'
        args.port = 0
        args.password = None
        args.server_password = None
        args.disable_save = True
        args.auto_shutdown = 0

        async def run():
            ctx = MultiServer.Context(args.host, args.port, args.server_password, args.password, args.location_check_points,
                                      args.hint_cost, not args.disable_item_cheat, args.release_mode, args.collect_mode,
                                      args.countdown_mode, args.remaining_mode, args.auto_shutdown, args.compatibility, args.log_network)
            ctx.load(args.multidata, False)
            ctx.init_save(False)
            ctx.server = websockets.serve(functools.partial(MultiServer.server, ctx=ctx), host='127.0.0.1', port=0,
                                          extensions=[MultiServer.server_per_message_deflate_factory])
            served = await ctx.server
            port = served.sockets[0].getsockname()[1]
            send({'id': rid, 'event': 'hosting', 'host': '127.0.0.1', 'port': port, 'seed_name': getattr(ctx, 'seed_name', ''),
                  'players': [getattr(s, 'name', str(s)) for s in ctx.slot_info.values()]})
            await ctx.exit_event.wait()  # never set here: Atlas ends the process when the test ends

        asyncio.run(run())
    except BaseException as e:
        if isinstance(e, KeyboardInterrupt):
            raise
        out = _failure(e)
        out.update({'id': rid, 'event': 'host_failed'})
        send(out)


from worlds.LauncherComponents import Component, components, Type
components.append(Component('UltimateBridge', None, func=launch_bridge, component_type=Type.CLIENT))
components.append(Component('AtlasNames', None, func=atlas_names, component_type=Type.CLIENT))
components.append(Component('AtlasCheck', None, func=atlas_check, component_type=Type.CLIENT))
components.append(Component('AtlasSeedTest', None, func=atlas_seed_test, component_type=Type.CLIENT))
components.append(Component('AtlasSeedReplay', None, func=atlas_seed_replay, component_type=Type.CLIENT))
components.append(Component('AtlasChecksum', None, func=atlas_checksum, component_type=Type.CLIENT))
components.append(Component('AtlasGameSweep', None, func=atlas_game_sweep, component_type=Type.CLIENT))
components.append(Component('AtlasYamlTemplate', None, func=atlas_yaml_template, component_type=Type.CLIENT))
components.append(Component('AtlasGenerate', None, func=atlas_generate, component_type=Type.CLIENT))
components.append(Component('AtlasHost', None, func=atlas_host, component_type=Type.CLIENT))
