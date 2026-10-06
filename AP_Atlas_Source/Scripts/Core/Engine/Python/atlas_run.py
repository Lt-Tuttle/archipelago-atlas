# Runs one Atlas bridge component (UltimateBridge, AtlasNames, AtlasCheck) from the portable engine's
# Archipelago source, without the Archipelago Launcher or its GUI.
# Usage: python atlas_run.py <archipelago folder> <component>
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
            exc = record.exc_info[1]
            ATLAS_LOAD_ERRORS[name] = {
                'missing': getattr(exc, 'name', None) if isinstance(exc, ModuleNotFoundError) else None,
                'error': (type(exc).__name__ + ': ' + str(exc))[:300],
            }
        except Exception:
            pass


def keep_files_in_engine_folder():
    # Archipelago keeps a per-user cache (data packages, a client id) in %LocalAppData%\Archipelago, and Python's
    # temporary files go to %TEMP%: both outside Atlas's folder. They stay in the engine folder instead (Atlas also
    # points the process's environment there; this holds whatever the installed packages do).
    engine = os.path.dirname(os.path.abspath(__file__))
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


def main():
    root, component = os.path.abspath(sys.argv[1]), sys.argv[2]
    # Atlas's channel is this process's own standard output (sys.__stdout__), written only by the component's
    # answers. Anything else that prints (a world as it loads, the tracker) goes to standard error, which Atlas logs,
    # so it can never be taken for an answer.
    sys.stdout = sys.stderr
    os.chdir(root)
    sys.path.insert(0, root)
    sys.argv = [os.path.join(root, 'Launcher.py')]

    # Never pip-install or prompt on stdin: stdin carries Atlas's requests.
    import ModuleUpdate
    ModuleUpdate.update_ran = True
    keep_files_in_engine_folder()

    logging.getLogger().addHandler(_WorldLoadFailures(level=logging.ERROR))
    import worlds  # noqa: F401  loads every world, including the custom apworlds that register components
    from worlds.LauncherComponents import components
    for c in components:
        if c.display_name == component and c.func is not None:
            c.func()
            return
    sys.__stdout__.write(json.dumps({'event': 'boot_failed', 'error': 'The ' + component + ' component is not installed in this engine.'}) + '\n')
    sys.__stdout__.flush()


main()
