# Runs one Atlas bridge component (UltimateBridge, AtlasNames, AtlasCheck) from the portable engine's
# Archipelago source, without the Archipelago Launcher or its GUI.
# Usage: python atlas_run.py <archipelago folder> <component>
import os
import sys
import json


def main():
    root, component = os.path.abspath(sys.argv[1]), sys.argv[2]
    os.chdir(root)
    sys.path.insert(0, root)
    sys.argv = [os.path.join(root, 'Launcher.py')]

    # Never pip-install or prompt on stdin: stdin carries Atlas's requests.
    import ModuleUpdate
    ModuleUpdate.update_ran = True

    import worlds  # noqa: F401  loads every world, including the custom apworlds that register components
    from worlds.LauncherComponents import components
    for c in components:
        if c.display_name == component and c.func is not None:
            c.func()
            return
    print(json.dumps({'error': 'The ' + component + ' component is not installed in this engine.'}))
    sys.stdout.flush()


main()
