# Security policy

## Reporting a vulnerability
Please report security problems **privately**: open this repository's **Security** tab and choose **Report a vulnerability**. Please don't open a public issue for them.

Include what you found, how to reproduce it, and which version of Atlas you used. You'll get a reply as soon as possible, and credit in the fix's release notes if you'd like it.

## Supported versions
Only the latest release gets security fixes.

## What Atlas protects
- **Your secrets are encrypted for your Windows account:** room passwords, and your Cheese Tracker API key. The key is only ever sent to Cheese Tracker.
- **Atlas asks before it touches anything outside its own folder.** That covers searching your PC for Archipelago, adding files to your Archipelago install (which it records, so they can be undone), and looking things up on GitHub by itself.
  - Everything else stays inside: the games' names from servers, Atlas's logs, and the Atlas Engine's temporary files and caches. Tests fail if a run writes anything outside. (Godot, the engine Atlas is built on, creates an empty folder in `%APPDATA%\Godot\app_userdata` when Atlas starts; it has no setting to stop that.)
  - It reads only the YAMLs you link.
  - Everything you've allowed is listed in Settings → Privacy & permissions, where you can take it back.
- **Links are opened safely:** only https web pages and folders. Atlas never asks Windows to open a file, because that could run it.
- **Downloads that become code are checked:**
  - The Atlas Engine's Python, pip, Archipelago and Universal Tracker are checked against fixed SHA-256 hashes.
  - Archipelago's Python packages install only as the exact files in Atlas's list of hashes.
  - A world's own requirements can't point pip at other servers.
  - Apworlds download only from sources you trust.
- **Every web request goes through one careful client:** one request at a time per site, size and time limits, and backoff after failures.
- **Saves are crash-safe,** with backups Atlas can recover from.
