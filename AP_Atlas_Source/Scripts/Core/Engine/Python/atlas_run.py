# Runs one Atlas bridge component (UltimateBridge, AtlasNames, AtlasCheck) from the portable engine's
# Archipelago source, without the Archipelago Launcher or its GUI.
# Usage: python atlas_run.py <archipelago folder> <component>
#        python atlas_run.py <archipelago folder> --atlas-games   (the games that load, and the worlds that didn't, as JSON)
#
# Archipelago's own folder (the engine's core: shipped with Atlas, or downloaded) is only read. What Atlas or the user
# adds lives in Atlas's engine data folder, named by ATLAS_ENGINE_DATA: Archipelago's user folder (ap: the game worlds
# unpacked when a slot needs them, installed apworlds, Players, host.yaml) and the packages worlds declare
# (site-packages).
import importlib.abc
import importlib.machinery
import os
import re
import sys
import json
import logging

# World name -> why it didn't load ({'missing': module or None, 'error': text}). The health check reports it,
# so Atlas can install a world's declared packages instead of just saying it failed.
ATLAS_LOAD_ERRORS = {}


class _WorldLoadFailures(logging.Handler):
    def emit(self, record):
        try:
            message = record.getMessage()
            if 'Could not load world' not in message or not record.exc_info:
                return
            match = re.search(r'WorldSource\(([^,)]+)', message)
            name = match.group(1).strip() if match else message
            if name.lower().endswith('.apworld'):
                name = name[:-len('.apworld')]
            name = os.path.basename(name)
            exc = record.exc_info[1]
            ATLAS_LOAD_ERRORS[name] = {
                'missing': getattr(exc, 'name', None) if isinstance(exc, ModuleNotFoundError) else None,
                'error': (type(exc).__name__ + ': ' + str(exc))[:300],
            }
        except Exception:
            pass


class _UserWorlds(importlib.abc.MetaPathFinder):
    # Archipelago finds the world folders in its user folder, but imports worlds.<name> only from its own folder: the
    # ones in the user folder (unpacked on demand) are found here.
    def __init__(self, folder):
        self.folder = folder

    def find_spec(self, fullname, path=None, target=None):
        parts = fullname.split('.')
        if len(parts) != 2 or parts[0] != 'worlds':
            return None
        if not os.path.isfile(os.path.join(self.folder, parts[1], '__init__.py')):
            return None
        return importlib.machinery.PathFinder.find_spec(fullname, [self.folder])


def use_engine_data():
    # Before anything asks Archipelago for its user folder: a core it can't write to would otherwise copy its Players
    # and sprites into the user's home folder.
    data = os.environ.get('ATLAS_ENGINE_DATA')
    if not data:
        return
    user = os.path.join(data, 'ap')
    worlds_folder = os.path.join(user, 'worlds')
    os.makedirs(worlds_folder, exist_ok=True)
    import Utils
    if hasattr(Utils, 'user_path'):
        Utils.user_path.cached_path = user
    extra = os.path.join(data, 'site-packages')
    if os.path.isdir(extra):
        import site
        site.addsitedir(extra)
    sys.meta_path.insert(0, _UserWorlds(worlds_folder))


def keep_files_in_engine_folder():
    # Archipelago keeps a per-user cache (data packages, a client id) in %LocalAppData%\Archipelago, and Python's
    # temporary files go to %TEMP%: both outside Atlas's folder. They stay in the engine folder instead (Atlas also
    # points the process's environment there; this holds whatever the installed packages do).
    engine = os.environ.get('ATLAS_ENGINE_DATA') or os.path.dirname(os.path.abspath(__file__))
    temp = os.path.join(engine, 'temp')
    os.makedirs(temp, exist_ok=True)
    for name in ('TEMP', 'TMP', 'TMPDIR'):
        os.environ[name] = temp
    import tempfile
    tempfile.tempdir = temp
    cache = os.path.join(engine, 'user', 'Local', 'Archipelago', 'Cache')

    def cache_path(*path):
        return os.path.join(cache, *path)

    cache_path.cached_path = cache
    # Utils' own functions look cache_path up when they run, and later "from Utils import cache_path" gets this one.
    import Utils
    Utils.cache_path = cache_path


def list_games():
    import worlds
    from worlds.AutoWorld import AutoWorldRegister
    games = {name: (getattr(cls, '__module__', '') or '').split('.')[1:2] for name, cls in AutoWorldRegister.world_types.items()}
    sys.__stdout__.write(json.dumps({'games': {name: (folder[0] if folder else '') for name, folder in games.items()},
                                     'failed': [str(w) for w in getattr(worlds, 'failed_world_loads', [])],
                                     'errors': ATLAS_LOAD_ERRORS}) + '\n')
    sys.__stdout__.flush()


def main():
    root, component = os.path.abspath(sys.argv[1]), sys.argv[2]
    # Atlas's channel is this process's own standard output (sys.__stdout__), written only by the component's
    # answers. Anything else that prints (a world as it loads, the tracker) goes to standard error, which Atlas logs,
    # so it can never be taken for an answer.
    sys.stdout = sys.stderr
    os.chdir(root)
    sys.path.insert(0, root)
    sys.argv = [os.path.join(root, 'Launcher.py')]
    use_engine_data()

    # Never pip-install or prompt on stdin: stdin carries Atlas's requests.
    import ModuleUpdate
    ModuleUpdate.update_ran = True
    keep_files_in_engine_folder()

    logging.getLogger().addHandler(_WorldLoadFailures(level=logging.ERROR))
    if component == '--atlas-games':
        list_games()
        return
    import worlds  # noqa: F401  loads every world, including the custom apworlds that register components
    from worlds.LauncherComponents import components
    for c in components:
        if c.display_name == component and c.func is not None:
            c.func()
            return
    sys.__stdout__.write(json.dumps({'event': 'boot_failed', 'error': 'The ' + component + ' component is not installed in this engine.'}) + '\n')
    sys.__stdout__.flush()


main()
