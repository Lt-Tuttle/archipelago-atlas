# Security policy

## Reporting a vulnerability
Please report security problems **privately**: open this repository's **Security** tab and choose **Report a vulnerability**. Please don't open a public issue for them.

Include what you found, how to reproduce it, and which version of Atlas you used. You'll get a reply as soon as possible, and credit in the fix's release notes if you'd like it.

## Supported versions
Only the latest release gets security fixes.

## What Atlas protects today
- Your Cheese Tracker API key is stored encrypted for your Windows account, and sent only to Cheese Tracker.
- The Atlas Engine's downloads (Python, Archipelago, Universal Tracker) are checked against fixed hashes. Apworld downloads need your approval first.
- Atlas's saves are crash-safe, with backups it can recover from.

Before the first public release, room passwords will be encrypted too, and Atlas will ask before it reads or writes anything outside its own folder.
