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


def main():
    root, component = os.path.abspath(sys.argv[1]), sys.argv[2]
    os.chdir(root)
    sys.path.insert(0, root)
    sys.argv = [os.path.join(root, 'Launcher.py')]

    # Never pip-install or prompt on stdin: stdin carries Atlas's requests.
    import ModuleUpdate
    ModuleUpdate.update_ran = True

    logging.getLogger().addHandler(_WorldLoadFailures(level=logging.ERROR))
    import worlds  # noqa: F401  loads every world, including the custom apworlds that register components
    from worlds.LauncherComponents import components
    for c in components:
        if c.display_name == component and c.func is not None:
            c.func()
            return
    print(json.dumps({'error': 'The ' + component + ' component is not installed in this engine.'}))
    sys.stdout.flush()


main()
