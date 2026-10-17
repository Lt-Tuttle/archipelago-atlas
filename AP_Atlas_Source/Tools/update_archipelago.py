r"""Moves the Atlas Engine to a new Archipelago release, as one checked process.

Usage:
    python -I Tools/update_archipelago.py <X.Y.Z> [--dry-run] [--fresh] [--zip FILE] [--allow-removed-worlds]
        [--no-tracker-check] [--engine-python EXE] [--godot EXE] [--repo DIR] [--skip-build]
        [--api-base URL] [--download-base URL] [--pypi-base URL]   (only http://127.0.0.1:<port>, for the tests)
    python -I Tools/update_archipelago.py --latest                 exit 0: Atlas pins the newest release; 20: a newer one is out
    python -I Tools/update_archipelago.py <X.Y.Z> --rollback

1. Preflight: a newer X.Y.Z; the files it pins are committed (or as this tool left them); no batch, corpus check, build
   or Atlas from source running; the engine's Python is the pinned one.
2. Release: GitHub says X.Y.Z is a full release, and which commit its tag is.
3. Download: the tag's archive, once (capped, checked: one top folder Archipelago-X.Y.Z/, nothing outside it, no loose
   apworlds, its Utils.py version, the zip comment is the tag's commit, the engine's Python in its supported range).
4. Requirements: the package lock is rebuilt only when Archipelago's requirements changed (and every new package must
   be credited first).
5. Index: the worlds index, with make_worlds_index.py --verify (the engine's Python, which has the worlds' packages).
6. Pins: AtlasEngine.cs, the index, the lock, CREDITS.md, THIRD_PARTY_NOTICES.md and a CHANGELOG draft, each pattern
   matched exactly once or nothing is written; every file keeps its own line endings; the originals go to backup\.
7. Build: dotnet build, the old core set aside, build_engine.ps1 on a copy of the build cache with the verified zip (a
   cache the engine build would download into, for Python, pip, the tracker or the packages, is refused first).
8. Post-check: the new core says X.Y.Z, the pinned tracker and the lock's signature, and holds no indexed world.
9. Report: report.md and report.json, from a run past the preflight (one the preflight stops, or that finds the
   update done, only prints: the last run's report stays).

Everything goes to Builds\ap-update\<X.Y.Z>\ (git ignores Builds). A rerun skips what still verifies and fetches
nothing again. --dry-run does phases 1-5 into new\ and prints the diff of every file phase 6 would patch; it writes no
state.json and nothing tracked. --rollback puts the patched files back while they're as this tool left them, and the
old core back in Builds\engine-core; it deletes nothing. Child processes keep their temporary files, caches and pip's
settings inside the work folder, and never open a window. Only GitHub's ArchipelagoMW/Archipelago (and PyPI when the
requirements change) are asked; --no-tracker-check also skips the Universal Tracker check on FarisTheAncient's fork.

Exit codes: 0 done, 1 crash, 2 usage, 3 preflight, 4 release, 5 download or verify, 6 lock, 7 index, 8 build,
9 post-check, 10 a pattern didn't match exactly once, 20 (--latest only) a newer release is out.
"""
import argparse
import difflib
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)  # python -I leaves the script's own folder off the path
sys.dont_write_bytecode = True  # no __pycache__ in Tools\ (python -I ignores PYTHONDONTWRITEBYTECODE)
import build_engine_lock  # noqa: E402

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
UPSTREAM = 'ArchipelagoMW/Archipelago'
TRACKER_FORK = 'FarisTheAncient/Archipelago'
GITHUB_API = 'https://api.github.com'
GITHUB = 'https://github.com'
PYPI = 'https://pypi.org'
# Where a site may send a request on: GitHub's archive links go to codeload.
REDIRECTS = {'https://github.com': {'https://codeload.github.com'}}
LOOPBACK = re.compile(r'^http://127\.0\.0\.1:\d{1,5}$')
VERSION = re.compile(r'^\d+\.\d+\.\d+$')
ZIP_LIMIT = 200 << 20
UNPACKED_LIMIT = 500 << 20
ENTRY_LIMIT = 20_000
API_LIMIT = 4
# The shipped-core check unpacks Bumper Stickers, which imports A Link to the Past (MainTrackerWindow.EngineBuild.cs).
SHIPPED_CHECK_WORLDS = ('bumpstik', 'alttp')
# Never beside these: they use the core, the lab or the build cache this replaces (or are another update).
BUSY = ('batch_logic.py', 'build_corpus_names.py', 'build_release.ps1', 'build_engine.ps1', 'run_selftest.ps1', 'update_archipelago.py')
NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
LICENCE_FILE = re.compile(r'^(LICEN[CS]E|COPYING|NOTICE)', re.IGNORECASE)

# What phase 6 sets, each matched exactly once: (file, name, pattern); the pattern's groups are the values it sets.
PINS = [
    ('engine', 'ArchipelagoVersion', r'public const string ArchipelagoVersion = "([^"\r\n]+)";'),
    ('engine', 'ArchipelagoUrl', r'private const string ArchipelagoUrl = "([^"\r\n]+)";'),
    ('engine', 'ArchipelagoSha256', r'private const string ArchipelagoSha256 = "([0-9a-f]{64})";'),
    ('credits', 'Archipelago row', r'^\| Archipelago ([\d.]+) \| The Archipelago contributors \| https://github\.com/ArchipelagoMW/Archipelago/releases/tag/([\d.]+) \| MIT \|'),
    ('notices', 'Archipelago core', r"includes Archipelago ([\d.]+)'s core"),
    ('lock', 'lock header', r"^# The Atlas Engine's Python packages for Archipelago ([^,\s]+),"),
]
# What it only reads, matched exactly once too.
READS = [
    ('engine', 'PythonVersion', r'public const string PythonVersion = "([^"\r\n]+)";'),
    ('engine', 'TrackerVersion', r'public const string TrackerVersion = "([^"\r\n]+)";'),
    ('lock', 'signature', r'^# requirements-signature: ([0-9A-F]{16})\b'),
    ('engine', 'TrackerSha256', r'private const string TrackerSha256 = "([0-9a-f]{64})";'),
]
LOCK_HEADER = PINS[5][2]
LOCK_SIGNATURE = READS[2][2]
PACKAGE = re.compile(r'^([A-Za-z0-9_.\-]+)==([^\s\\]+)', re.MULTILINE)


class Stop(Exception):
    """A refusal: the exit code, what to tell the owner, and the code of the step that failed inside, if any."""

    def __init__(self, code, message, inner=None):
        super().__init__(message)
        self.code = code
        self.inner = inner


# --- Files ---

def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def read_bytes(path):
    with open(path, 'rb') as f:
        return f.read()


def write_atomic(path, data):
    # A temporary file beside it, then a rename: a crash leaves the old file or the new one, never half of one.
    os.makedirs(os.path.dirname(path), exist_ok=True)
    temp = path + '.tmp'
    with open(temp, 'wb') as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.replace(temp, path)


def copy_atomic(source, path):
    temp = path + '.part'
    shutil.copyfile(source, temp)
    os.replace(temp, path)


def write_json(path, value):
    write_atomic(path, (json.dumps(value, indent=2, ensure_ascii=False) + '\n').encode('utf-8'))


def load_json(path):
    try:
        with open(path, encoding='utf-8-sig') as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def text_of(data):
    return data.decode('utf-8-sig')


def with_newline_of(data, like):
    # The file's own line endings: CRLF if it has any (the checkout's style), else LF (the lock in the repository).
    lf = data.replace(b'\r\n', b'\n')
    return lf.replace(b'\n', b'\r\n') if b'\r\n' in like else lf


def version_key(v):
    return tuple(int(x) for x in v.split('.'))


def now():
    return datetime.now(timezone.utc).isoformat(timespec='seconds')


class Repo:
    """The repository's files this tool reads and pins, and where its work goes."""

    def __init__(self, root):
        self.root = os.path.abspath(root)
        self.project = os.path.join(self.root, 'AP_Atlas_Source')
        engine = os.path.join(self.project, 'Scripts', 'Core', 'Engine')
        self.runner = os.path.join(engine, 'Python', 'atlas_run.py')
        self.builds = os.path.join(self.root, 'Builds')
        self.paths = {
            'engine': os.path.join(engine, 'AtlasEngine.cs'),
            'index': os.path.join(engine, 'Data', 'archipelago_worlds.json'),
            'lock': os.path.join(engine, 'Data', 'engine_packages.lock.txt'),
            'credits': os.path.join(self.root, 'CREDITS.md'),
            'notices': os.path.join(self.root, 'THIRD_PARTY_NOTICES.md'),
            'changelog': os.path.join(self.root, 'CHANGELOG.md'),
        }

    def rel(self, key):
        return os.path.relpath(self.paths[key], self.root).replace('\\', '/')


class Files:
    """The pinned files as they are now, and as they were before this tool first wrote them (kept in backup\\)."""

    def __init__(self, repo, work, state):
        self.repo, self.work, self.state = repo, work, state

    def current(self, key):
        try:
            return read_bytes(self.repo.paths[key])
        except OSError:
            raise Stop(3, f'{self.repo.rel(key)} is missing.')

    def backup_path(self, key):
        return os.path.join(self.work, 'backup', *self.repo.rel(key).split('/'))

    def original(self, key):
        rel = self.repo.rel(key)
        if rel in self.state.get('backups', {}) and os.path.isfile(self.backup_path(key)):
            return read_bytes(self.backup_path(key))
        return self.current(key)


def find_once(text, pattern, where):
    found = list(re.finditer(pattern, text, re.MULTILINE))
    if len(found) != 1:
        raise Stop(10, f'{where}: {len(found)} matches for {pattern!r}, not exactly one. Nothing was written.')
    return found[0]


def line_of(text, match):
    return text.count('\n', 0, match.start()) + 1


def read_pins(repo, data_of):
    """Every pin and read value, from the files data_of(key) gives: {name: (key, values, line)}."""
    texts = {key: text_of(data_of(key)) for key in ('engine', 'credits', 'notices', 'lock')}
    out = {}
    for key, name, pattern in PINS + READS:
        m = find_once(texts[key], pattern, repo.rel(key))
        out[name] = (key, m.groups(), line_of(texts[key], m))
    changelog = text_of(data_of('changelog'))
    unreleased_section(changelog, repo.rel('changelog'))
    try:
        index = json.loads(text_of(data_of('index')))
        out['index'] = ('index', (index['archipelago'],), 1)
    except (ValueError, KeyError, TypeError):
        raise Stop(10, f'{repo.rel("index")} has no "archipelago" version. Nothing was written.')
    return out


def unreleased_section(text, where):
    # The CHANGELOG's [Unreleased] section and its "### Changed" heading, each exactly once.
    unreleased = find_once(text, r'^## \[Unreleased\][ \t]*\r?\n', where)
    following = re.compile(r'^## \[', re.MULTILINE).search(text, unreleased.end())
    end = following.start() if following else len(text)
    changed = find_once(text[unreleased.end():end], r'^### Changed[ \t]*\r?\n', where + ' [Unreleased]')
    return unreleased.end(), end, unreleased.end() + changed.end()


def set_groups(text, match, values):
    out, last = [], 0
    for i, value in enumerate(values, 1):
        out.append(text[last:match.start(i)])
        out.append(value)
        last = match.end(i)
    out.append(text[last:])
    return ''.join(out)


def archive_url(base, v):
    return f'{base}/{UPSTREAM}/archive/refs/tags/{v}.zip'


# --- Child processes ---

def left_out(name):
    """Whether a variable stays out of the child processes: pip's and Python's own settings, Atlas's (ATLAS_ENGINE_CORE
    would point the engine build at another core), crash reporting's, and anything that looks like a secret."""
    upper = name.upper()
    return (upper.startswith(('PIP_', 'PYTHON', 'ATLAS_', 'SENTRY_'))
            or any(word in upper for word in ('TOKEN', 'SECRET', 'PASSWORD', 'PASSWD', 'CREDENTIAL', '_DSN', 'API_KEY', 'APIKEY', 'ACCESS_KEY', 'PRIVATE_KEY')))


def child_env(home):
    """What every child process gets: its temporary files, user folders, pip's cache and settings in home\\. TMPDIR too:
    Python's tempfile reads it before TEMP and TMP, and Git Bash sets it to the user's temp folder."""
    env = {k: v for k, v in os.environ.items() if not left_out(k)}
    for name, sub in (('TEMP', 'Temp'), ('TMP', 'Temp'), ('TMPDIR', 'Temp'), ('APPDATA', 'AppData\\Roaming'), ('LOCALAPPDATA', 'AppData\\Local'),
                      ('PIP_CACHE_DIR', 'pip-cache'), ('PYTHONPYCACHEPREFIX', 'pycache')):
        path = os.path.join(home, sub)
        os.makedirs(path, exist_ok=True)
        env[name] = path
    env.update(PIP_CONFIG_FILE=os.devnull, PIP_DISABLE_PIP_VERSION_CHECK='1', PIP_NO_INPUT='1', PYTHONNOUSERSITE='1',
               PYTHONDONTWRITEBYTECODE='1', PYTHONIOENCODING='utf-8', SKIP_REQUIREMENTS_UPDATE='1')
    return env


ISOLATION = ('TEMP', 'TMP', 'TMPDIR', 'APPDATA', 'LOCALAPPDATA', 'PIP_CACHE_DIR', 'PIP_CONFIG_FILE', 'PIP_DISABLE_PIP_VERSION_CHECK',
             'PYTHONNOUSERSITE', 'PYTHONPYCACHEPREFIX', 'PYTHONDONTWRITEBYTECODE', 'SKIP_REQUIREMENTS_UPDATE')


def run(args, env, cwd=None, timeout=120, log=None):
    """Runs a program directly (never through cmd start), without a window or input; its output to the log, if any."""
    try:
        done = subprocess.run(args, stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding='utf-8', errors='replace',
                              env=env, cwd=cwd, timeout=timeout, creationflags=NO_WINDOW)
    except subprocess.TimeoutExpired as e:
        out, err = (x.decode('utf-8', 'replace') if isinstance(x, bytes) else x or '' for x in (e.stdout, e.stderr))
        done = subprocess.CompletedProcess(args, -1, out, err + f'\n(stopped after {timeout} s)')
    if log:
        os.makedirs(os.path.dirname(log), exist_ok=True)
        with open(log, 'a', encoding='utf-8') as f:
            f.write(f'\n=== {now()} {subprocess.list2cmdline(args)} (exit {done.returncode})\n{done.stdout}\n{done.stderr}\n')
    return done


def tail(done, lines=12):
    text = ((done.stdout or '') + '\n' + (done.stderr or '')).strip().splitlines()
    return '\n'.join('    ' + line for line in text[-lines:])


def must_run_alone(args, repo, env):
    """Refuses while something runs that this must not run beside. The tests run it on stand-in sites only, and there what
    else runs on the PC doesn't matter (ATLAS_UPDATE_TEST_SKIP_BUSY=1; never with GitHub or PyPI as the sites)."""
    if os.environ.get('ATLAS_UPDATE_TEST_SKIP_BUSY') == '1' and all(LOOPBACK.match(b) for b in (args.api_base, args.download_base, args.pypi_base)):
        return
    busy = busy_programs(repo, env)
    if busy:
        raise Stop(3, 'Run this alone. Still running:\n    ' + '\n    '.join(busy))


def busy_programs(repo, env):
    """What's running that this must not run beside (read through Get-CimInstance, no window): this run and its parents aside."""
    script = ('[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; Get-CimInstance Win32_Process | ForEach-Object { '
              '[pscustomobject]@{ Id = $_.ProcessId; Parent = $_.ParentProcessId; Name = $_.Name; Line = $_.CommandLine } } | ConvertTo-Json -Compress')
    try:
        done = run(['powershell', '-NoProfile', '-NonInteractive', '-Command', script], env)
        rows = json.loads(done.stdout.lstrip('\ufeff').strip() or '[]') if done.returncode == 0 else None  # Windows PowerShell may start with a BOM
    except (OSError, ValueError):
        rows = None
    if rows is None:
        raise Stop(3, "Couldn't list the programs running (PowerShell's Get-CimInstance), so this can't tell it runs alone.")
    rows = [rows] if isinstance(rows, dict) else rows
    parents = {r.get('Id'): r.get('Parent') for r in rows}
    mine, pid = set(), os.getpid()
    while pid and pid not in mine:
        mine.add(pid)
        pid = parents.get(pid)
    project = repo.project.lower().replace('\\', '/')
    found = []
    for r in rows:
        if r.get('Id') in mine:
            continue
        line = r.get('Line') or ''
        low = line.lower().replace('\\', '/')
        name = (r.get('Name') or '').lower()
        atlas = ('godot' in name or name == 'thearchipelagoatlas.exe') and project in low
        if atlas or any(b in low for b in BUSY):
            found.append(f'{r.get("Name")} ({r.get("Id")}): {line[:200]}')
    return found


# --- The network ---

def origin_of(url):
    parts = urllib.parse.urlsplit(url)
    return f'{parts.scheme}://{parts.netloc}'.lower()


class _Redirects(urllib.request.HTTPRedirectHandler):
    def __init__(self, net, allowed):
        super().__init__()
        self.net, self.allowed = net, allowed

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if origin_of(newurl) not in self.allowed:
            raise urllib.error.URLError(f'{req.full_url} sent this on to {origin_of(newurl)}, which this run may not ask')
        self.net.note(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


class Net:
    """One request at a time, 0.2 s apart, with the build tools' user agent; every site asked is kept for the report."""

    def __init__(self):
        self.hosts = []
        self.pip_hosts = []
        self.api_requests = 0
        self.downloads = 0
        self._last = 0.0

    def note(self, url):
        if origin_of(url) not in self.hosts:
            self.hosts.append(origin_of(url))

    def _open(self, url, accept):
        wait = self._last + 0.2 - time.monotonic()
        if wait > 0:
            time.sleep(wait)
        base = origin_of(url)
        allowed = {base} | REDIRECTS.get(base, set())
        handlers = [_Redirects(self, allowed)]
        if LOOPBACK.match(base):
            handlers.append(urllib.request.ProxyHandler({}))  # a site on this PC is never reached through a proxy
        self.note(url)
        request = urllib.request.Request(url, headers={'User-Agent': build_engine_lock.USER_AGENT, 'Accept': accept})
        return urllib.request.build_opener(*handlers).open(request, timeout=60)

    def get_json(self, url):
        self.api_requests += 1
        if self.api_requests > API_LIMIT:
            raise RuntimeError(f'more than {API_LIMIT} API requests in one run')
        try:
            with self._open(url, 'application/vnd.github+json') as response:
                data = response.read(5 << 20)
            return json.loads(data.decode('utf-8'))
        finally:
            self._last = time.monotonic()

    def download(self, url, path, limit):
        self.downloads += 1
        if self.downloads > 1:
            raise RuntimeError('more than one download in one run')
        part = path + '.part'
        try:
            with self._open(url, 'application/zip, application/octet-stream') as response:
                length = response.headers.get('Content-Length')
                if length and length.isdigit() and int(length) > limit:
                    raise Stop(5, f'{url} is {int(length) >> 20} MB, over the {limit >> 20} MB limit.')
                size = 0
                with open(part, 'wb') as f:
                    for block in iter(lambda: response.read(1 << 20), b''):
                        size += len(block)
                        if size > limit:
                            raise Stop(5, f'{url} is over the {limit >> 20} MB limit.')
                        f.write(block)
            os.replace(part, path)
        finally:
            self._last = time.monotonic()
            if os.path.exists(part):
                os.remove(part)


# --- 1. Preflight ---

def preflight(args, repo, v, files, state, env):
    """The version this updates from, or None when every pin and the core already say v and nothing is left to do."""
    pins = read_pins(repo, files.current)
    pinned = pins['ArchipelagoVersion'][1][0]
    stamp = load_json(os.path.join(repo.builds, 'engine-core', 'core.json')) or {}
    if all_pins_say(pins, v) and stamp.get('Archipelago') == v and finished(state, files.work):
        return None
    origin = state.get('from') or pinned
    if not VERSION.match(origin):
        raise Stop(3, f'AtlasEngine.ArchipelagoVersion is {origin!r}, not X.Y.Z.')
    if version_key(v) <= version_key(origin):
        raise Stop(3, f'{v} is not newer than {origin}, the version Atlas pins.')

    rels = [repo.rel(key) for key in repo.paths]
    done = run(['git', '-C', repo.root, 'status', '--porcelain', '--'] + rels, env)
    if done.returncode != 0:
        raise Stop(3, "git couldn't say whether the pinned files are committed:\n" + tail(done))
    dirty = []
    for line in done.stdout.splitlines():
        rel = line[3:].strip().strip('"')
        path = os.path.join(repo.root, *rel.split('/'))
        written = state.get('written', {}).get(rel)
        if not (written and os.path.isfile(path) and sha256_file(path) == written):
            dirty.append(line.strip())
    if dirty:
        raise Stop(3, 'Commit these first (or restore them); they are not as this tool left them:\n    ' + '\n    '.join(dirty))

    must_run_alone(args, repo, env)

    want = pins['PythonVersion'][1][0]
    if not os.path.isfile(args.engine_python):
        raise Stop(3, f'The engine Python {args.engine_python} is missing (--engine-python).')
    done = run([args.engine_python, '-I', '-c', 'import platform; print(platform.python_version())'], env)
    have = done.stdout.strip()
    if done.returncode != 0 or have != want:
        raise Stop(3, f'The engine Python {args.engine_python} is {have or "not working"}; AtlasEngine.PythonVersion is {want}.')
    if not (args.dry_run or args.skip_build) and not os.path.isfile(args.godot):
        raise Stop(3, f"Godot's console executable {args.godot} is missing (--godot, or ATLAS_GODOT).")
    return origin


def finished(state, work):
    """Whether v's run here passed its post-check, or there's no run of v here (a checkout after the update's commit). A
    run stopped in the build or the post-check has the pins and the core's stamp already: it goes on from there."""
    if not state.get('version'):
        return True
    return bool(state['phases'].get('post')) and os.path.isfile(os.path.join(work, 'core_snapshot.json'))


def all_pins_say(pins, v):
    return (pins['ArchipelagoVersion'][1] == (v,) and pins['ArchipelagoUrl'][1] == (archive_url(GITHUB, v),)
            and pins['Archipelago row'][1] == (v, v) and pins['Archipelago core'][1] == (v,)
            and pins['lock header'][1] == (v,) and pins['index'][1] == (v,))


# --- 2. Release ---

def release_phase(args, v, net, work, pinned_tracker):
    path = os.path.join(work, 'release.json')
    cached = load_json(path)
    cached = cached if isinstance(cached, dict) and cached.get('version') == v and cached.get('commit') else None
    if cached and not args.fresh:
        release = cached
    else:
        api = f'{args.api_base}/repos/{UPSTREAM}'
        try:
            facts = net.get_json(f'{api}/releases/tags/{v}')
            if facts.get('draft') is not False or facts.get('prerelease') is not False:
                raise Stop(4, f'GitHub lists {v} as a draft or a pre-release (draft {facts.get("draft")}, prerelease {facts.get("prerelease")}).')
            tag = (net.get_json(f'{api}/git/ref/tags/{v}') or {}).get('object') or {}
            if tag.get('type') == 'tag':  # an annotated tag: one more step to its commit
                tag = (net.get_json(f'{api}/git/tags/{tag.get("sha")}') or {}).get('object') or {}
        except urllib.error.HTTPError as e:
            raise Stop(4, f'GitHub has no release {v} ({e.code} for {e.url}).')
        except (urllib.error.URLError, OSError, ValueError) as e:
            raise Stop(4, f"GitHub couldn't be asked about {v}: {e}")
        commit = tag.get('sha') or ''
        if tag.get('type') != 'commit' or not re.fullmatch(r'[0-9a-f]{40}', commit):
            raise Stop(4, f"The tag {v} doesn't lead to a commit ({tag}).")
        if cached and cached['commit'] != commit:
            raise Stop(4, f'The tag {v} now points at {commit}; it was {cached["commit"]}. Both stay recorded in {path}.')
        release = {
            'version': v,
            'commit': commit,
            'published_at': facts.get('published_at'),
            'html_url': facts.get('html_url'),
            'notes': facts.get('body') or '',
            'zip': (cached or {}).get('zip'),
            'tracker': (cached or {}).get('tracker'),
        }
    if not args.no_tracker_check and not (release.get('tracker') or {}).get('checked'):
        release['tracker'] = tracker_check(args, net, pinned_tracker)
    write_json(path, release)
    return release


def tracker_check(args, net, pinned):
    # Only reported: a newer Universal Tracker is never switched to here.
    try:
        rows = net.get_json(f'{args.api_base}/repos/{TRACKER_FORK}/releases?per_page=10')
    except (urllib.error.URLError, OSError, ValueError) as e:
        return {'checked': False, 'error': str(e)}
    tags = [r.get('tag_name') or '' for r in rows if isinstance(r, dict) and not r.get('draft') and not r.get('prerelease')]
    found = [t[len('Tracker_'):] for t in tags if re.fullmatch(r'Tracker_v\d+(\.\d+)*', t)]

    def key(t):
        return tuple(int(x) for x in t.lstrip('v').split('.')) if re.fullmatch(r'v?\d+(\.\d+)*', t) else ()

    newest = max(found, key=key, default=None)
    return {'checked': True, 'pinned': pinned, 'newest': newest, 'newer': bool(newest and key(newest) > key(pinned))}


# --- 3. Download ---

def download_phase(args, v, net, work, release, python_version):
    zip_path = os.path.join(work, f'Archipelago-{v}.zip')
    recorded = (release.get('zip') or {}).get('sha256')
    source = archive_url(args.download_base, v)
    if args.zip:
        candidate, source = os.path.abspath(args.zip), os.path.abspath(args.zip)
        sha = sha256_file(candidate)
        if recorded and sha != recorded:
            raise Stop(5, f'{candidate} (SHA-256 {sha}) is not the zip recorded for {v} ({recorded}).')
    elif os.path.isfile(zip_path) and not args.fresh:
        candidate = zip_path
        sha = sha256_file(candidate)
        if recorded and sha != recorded:
            raise Stop(5, f'The kept {zip_path} (SHA-256 {sha}) is not the zip recorded for {v} ({recorded}); --fresh downloads it again.')
    else:
        candidate = os.path.join(work, f'Archipelago-{v}.download.zip')
        # A download an earlier run refused is checked again, not fetched again (its refusal doesn't change).
        if args.fresh or not os.path.isfile(candidate):
            try:
                net.download(source, candidate, ZIP_LIMIT)
            except (urllib.error.URLError, OSError) as e:
                raise Stop(5, f"{source} couldn't be downloaded: {e}")
        sha = sha256_file(candidate)
        if recorded and sha != recorded:
            kept = os.path.join(work, f'Archipelago-{v}-{sha[:12]}.zip')
            os.replace(candidate, kept)
            raise Stop(5, f"GitHub's archive of {v} changed since it was recorded: SHA-256 {sha}, recorded {recorded}. "
                          f'Both are kept: {zip_path} and {kept}.')
    downloaded = candidate == os.path.join(work, f'Archipelago-{v}.download.zip')
    try:
        facts = check_zip(candidate, v, release['commit'], python_version)
    except Stop as stop:
        if not downloaded:
            raise
        raise Stop(5, f'{stop} It stays as {candidate}: a rerun checks it again without asking GitHub; --fresh downloads it again.')
    if downloaded:
        os.replace(candidate, zip_path)
    elif candidate != zip_path:
        copy_atomic(candidate, zip_path)
    # Pinned before any of the zip's code runs.
    release['zip'] = {'url': archive_url(GITHUB, v), 'from': source, 'sha256': sha, 'bytes': os.path.getsize(zip_path)}
    write_json(os.path.join(work, 'release.json'), release)
    return zip_path, sha, facts


def check_zip(path, v, commit, python_version):
    root = f'Archipelago-{v}'
    try:
        z = zipfile.ZipFile(path)
    except (OSError, zipfile.BadZipFile) as e:
        raise Stop(5, f'{path} is not a zip: {e}')
    with z:
        infos = z.infolist()
        if len(infos) > ENTRY_LIMIT:
            raise Stop(5, f'The zip has {len(infos)} entries, over {ENTRY_LIMIT}.')
        unpacked = sum(i.file_size for i in infos)
        if unpacked > UNPACKED_LIMIT:
            raise Stop(5, f'The zip unpacks to {unpacked >> 20} MB, over {UNPACKED_LIMIT >> 20} MB.')
        tops = set()
        for info in infos:
            name = info.filename
            parts = re.split(r'[\\/]', name)
            if name.startswith(('/', '\\')) or re.match(r'^[A-Za-z]:', name) or '..' in parts:
                raise Stop(5, f'The zip has an entry outside its folder: {name!r}.')
            if (info.external_attr >> 16) & 0o170000 == 0o120000:
                raise Stop(5, f'The zip has a symbolic link: {name!r}.')
            tops.add(parts[0])
            if re.fullmatch(re.escape(root) + r'/worlds/[^/]+\.apworld', name):
                raise Stop(5, f'The zip has a loose apworld, which Archipelago would load instead of its folder: {name!r}.')
        if tops != {root}:
            raise Stop(5, f'The zip holds {sorted(tops)}, not just {root}/.')

        def read(name):
            try:
                return z.read(f'{root}/{name}').decode('utf-8', errors='replace')
            except KeyError:
                raise Stop(5, f'The zip has no {root}/{name}.')

        m = re.search(r'^__version__\s*=\s*["\']([^"\']+)["\']', read('Utils.py'), re.MULTILINE)
        if not m or m.group(1) != v:
            raise Stop(5, f'Its Utils.py says __version__ {m.group(1) if m else "nothing"}, not {v}.')
        comment = z.comment.decode('ascii', errors='replace').strip()
        if comment != commit:
            raise Stop(5, f"The zip's comment is {comment!r}, not the tag's commit {commit}: it isn't GitHub's archive of the tag.")
        supported = python_range(read('ModuleUpdate.py'))
        if not supported:
            raise Stop(5, "Its ModuleUpdate.py doesn't say which Python versions it supports.")
        low, high = supported
        have = version_key(python_version)
        if have < low or (high and have >= high):
            shown = '.'.join(map(str, low)) + (f' up to {".".join(map(str, high))}' if high else ' and up')
            raise Stop(5, f'It supports Python {shown}; the engine runs {python_version}.')
        return {'entries': len(infos), 'unpacked_bytes': unpacked, 'requirements': read('requirements.txt'), 'utils': read('Utils.py'),
                'python': '.'.join(map(str, low)) + (' to below ' + '.'.join(map(str, high)) if high else ' and up'),
                'core_files': sorted(i.filename[len(root) + 1:] for i in infos if not i.is_dir() and not i.filename.startswith(f'{root}/worlds/'))}


def python_range(text):
    # "(3, 11, 9) <= sys.version_info < (3, 14, 0)" (Windows and macOS), else a lower bound alone.
    num = r'\(\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*(\d+)\s*)?\)'
    m = re.search(num + r'\s*<=\s*sys\.version_info\s*<\s*' + num, text)
    if m:
        g = [int(x) if x else 0 for x in m.groups()]
        return tuple(g[0:3]), tuple(g[3:6])
    m = re.search(r'sys\.version_info\s*<\s*' + num, text)
    if m:
        return tuple(int(x) if x else 0 for x in m.groups()), None
    return None


def licence_diff(repo, origin, v, work, facts):
    """Core files (outside worlds\\) added or removed since the old zip, with new licence files and folders flagged."""
    old_zip = os.path.join(repo.builds, 'engine-data', 'engine', 'downloads', f'Archipelago-{origin}.zip')
    if not os.path.isfile(old_zip):
        return {'compared_with': None, 'note': f'{old_zip} is missing: no licence diff.'}
    root = f'Archipelago-{origin}/'
    with zipfile.ZipFile(old_zip) as z:
        old = {i.filename[len(root):] for i in z.infolist() if not i.is_dir() and i.filename.startswith(root) and not i.filename.startswith(root + 'worlds/')}
    new = set(facts['core_files'])
    added, removed = sorted(new - old), sorted(old - new)
    old_folders = {f.split('/')[0] for f in old if '/' in f}
    flagged = [f for f in added if LICENCE_FILE.match(f.split('/')[-1])]
    flagged += sorted({f.split('/')[0] + '/' for f in added if '/' in f and f.split('/')[0] not in old_folders})
    lines = [f'Core files of Archipelago {v} (outside worlds/) against {origin}', '', 'Needs a person: ' + (', '.join(flagged) or 'nothing new'), '']
    lines += ['+ ' + f for f in added] + ['- ' + f for f in removed]
    write_atomic(os.path.join(work, 'licence_diff.txt'), ('\n'.join(lines) + '\n').encode('utf-8'))
    return {'compared_with': old_zip, 'added': added, 'removed': removed, 'flagged': flagged}


# --- 4. Requirements ---

def requirements_phase(args, v, work, env, files, state, facts, net):
    ap_dir = os.path.join(work, 'new', 'archipelago')
    os.makedirs(ap_dir, exist_ok=True)
    for name in ('requirements.txt', 'Utils.py'):
        write_atomic(os.path.join(ap_dir, name), facts['requirements' if name == 'requirements.txt' else 'utils'].encode('utf-8'))
    signature = build_engine_lock.signature(build_engine_lock.filtered_requirements(ap_dir))
    old_lock = text_of(files.original('lock'))
    old_signature = find_once(old_lock, LOCK_SIGNATURE, 'the lock').group(1)
    result = {'signature': signature, 'old_signature': old_signature, 'rebuilt': signature != old_signature}
    if not result['rebuilt']:
        return result

    out = os.path.join(work, 'new', 'engine_packages.lock.txt')
    record = state['phases'].get('requirements') or {}
    if args.fresh or not lock_still_fits(out, v, signature, record.get('lock_sha256')):
        log = os.path.join(work, 'lock.log')
        # -B: python -I ignores the PYTHONDONTWRITEBYTECODE child_env sets (and this one's too).
        cmd = [sys.executable, '-I', '-B', '-X', 'utf8', os.path.join(HERE, 'build_engine_lock.py'), ap_dir, args.engine_python, out + '.part']
        if args.pypi_base != PYPI:
            cmd += ['--index-url', args.pypi_base + '/simple', '--json-base', args.pypi_base + '/pypi']
        done = run(cmd, env, cwd=work, timeout=1800, log=log)
        if done.returncode != 0:
            raise Stop(6, f"The package lock couldn't be built (exit {done.returncode}); log: {log}\n" + tail(done), inner=done.returncode)
        os.replace(out + '.part', out)
        net.pip_hosts.extend([args.pypi_base] if args.pypi_base != PYPI else ['https://pypi.org', 'https://files.pythonhosted.org'])
    new_lock = text_of(read_bytes(out))
    result['lock_sha256'] = sha256_file(out)
    old, new = dict(PACKAGE.findall(old_lock)), dict(PACKAGE.findall(new_lock))
    result['packages'] = {'added': sorted(f'{n} {new[n]}' for n in new if n not in old),
                          'removed': sorted(f'{n} {old[n]}' for n in old if n not in new),
                          'changed': sorted(f'{n} {old[n]} -> {new[n]}' for n in new if n in old and old[n] != new[n])}
    state['phases']['requirements'] = {'signature': signature, 'lock_sha256': result['lock_sha256']}
    credits, notices = text_of(files.current('credits')), text_of(files.current('notices'))
    missing = [f'| {n} {ver} | in {where}' for n, ver in sorted(new.items(), key=lambda p: p[0].lower())
               for where, text in (('CREDITS.md', credits), ('THIRD_PARTY_NOTICES.md', notices)) if f'| {n} {ver} |' not in text]
    if missing:
        raise Stop(6, "Archipelago's requirements changed. Credit these packages by hand (who made each, its project, its "
                      'license), commit, and run this again:\n    ' + '\n    '.join(missing))
    return result


def lock_still_fits(path, v, signature, recorded):
    if not os.path.isfile(path) or (recorded and sha256_file(path) != recorded):
        return False
    text = text_of(read_bytes(path))
    head = re.search(LOCK_HEADER, text, re.MULTILINE)
    sig = re.search(LOCK_SIGNATURE, text, re.MULTILINE)
    return bool(head and sig and head.group(1) == v and sig.group(1) == signature)


# --- 5. Index ---

def index_phase(args, repo, v, work, env, files, state, zip_path, sha):
    out = os.path.join(work, 'new', 'archipelago_worlds.json')
    recorded = (state['phases'].get('index') or {}).get('sha256')
    index = load_json(out) if not args.fresh and os.path.isfile(out) and (not recorded or sha256_file(out) == recorded) else None
    if not (isinstance(index, dict) and index.get('zip_sha256') == sha and index.get('archipelago') == v):
        log = os.path.join(work, 'index.log')
        cmd = [args.engine_python, '-I', '-B', '-X', 'utf8', os.path.join(HERE, 'make_worlds_index.py'), '--zip', zip_path, '--sha256', sha,
               '--runner', repo.runner, '--out', out + '.part', '--verify']
        done = run(cmd, env, cwd=work, timeout=2 * 3600, log=log)
        if done.returncode != 0 or not os.path.isfile(out + '.part'):
            raise Stop(7, f"The worlds index couldn't be made (exit {done.returncode}); log: {log}\n" + tail(done), inner=done.returncode)
        os.replace(out + '.part', out)
        index = load_json(out)
    state['phases']['index'] = {'sha256': sha256_file(out)}

    old = json.loads(text_of(files.original('index')))
    old_worlds, new_worlds = old.get('worlds', {}), index.get('worlds', {})

    def games(worlds):
        return {g for w in worlds.values() for g in w.get('games', [])}

    diff = {
        'worlds_added': sorted(set(new_worlds) - set(old_worlds)),
        'worlds_removed': sorted(set(old_worlds) - set(new_worlds)),
        'games_added': sorted(games(new_worlds) - games(old_worlds)),
        'games_removed': sorted(games(old_worlds) - games(new_worlds)),
        'core_worlds': index.get('core_worlds', []),
    }
    lost_core = sorted(set(old.get('core_worlds', [])) - set(index.get('core_worlds', [])))
    if lost_core:
        raise Stop(7, f'Worlds the core keeps are gone from {v}: {", ".join(lost_core)}.')
    lost_check = [w for w in SHIPPED_CHECK_WORLDS if w not in new_worlds]
    if lost_check:
        raise Stop(7, f"The shipped-core check's worlds are gone from {v}: {', '.join(lost_check)} (MainTrackerWindow.EngineBuild.cs).")
    return index, diff


def removed_worlds(args, v, diff):
    """A release that removes world folders goes on only with --allow-removed-worlds; a dry run says so and shows the rest."""
    if not diff['worlds_removed'] or args.allow_removed_worlds:
        return
    removed = f'{v} removes these world folders: {", ".join(diff["worlds_removed"])} (games: {", ".join(diff["games_removed"]) or "none"}).'
    if not args.dry_run:
        raise Stop(7, removed + ' Read the release notes, then run again with --allow-removed-worlds.')
    diff['needs_flag'] = True
    print(removed + ' A real run needs --allow-removed-worlds (read the release notes first).', flush=True)


# --- 6. Pins ---

def pin_plan(repo, v, sha, files, requirements, index_path, diff):
    """Every pinned file's bytes now and after, all computed before anything is written: {key: (now, after)}."""
    before = {key: files.current(key) for key in repo.paths}
    texts = {key: text_of(data) for key, data in before.items()}
    # Copied in from new\, each in the file's own line endings.
    texts['index'] = text_of(with_newline_of(read_bytes(index_path), before['index']))
    if requirements['rebuilt']:
        texts['lock'] = text_of(with_newline_of(read_bytes(os.path.join(os.path.dirname(index_path), 'engine_packages.lock.txt')), before['lock']))
    values = {'ArchipelagoVersion': (v,), 'ArchipelagoUrl': (archive_url(GITHUB, v),), 'ArchipelagoSha256': (sha,),
              'Archipelago row': (v, v), 'Archipelago core': (v,), 'lock header': (v,)}
    for key, name, pattern in PINS:
        texts[key] = set_groups(texts[key], find_once(texts[key], pattern, repo.rel(key)), values[name])
    _, end, at = unreleased_section(texts['changelog'], repo.rel('changelog'))
    if not re.search(r'^- \*\*The Atlas Engine runs Archipelago ' + re.escape(v) + r'\*\*', texts['changelog'][at:end], re.MULTILINE):
        nl = '\r\n' if texts['changelog'][:at].endswith('\r\n') else '\n'
        texts['changelog'] = texts['changelog'][:at] + changelog_bullet(v, diff) + nl + texts['changelog'][at:]
    return {key: (before[key], texts[key].encode('utf-8')) for key in before}


def changelog_bullet(v, diff):
    added = ', '.join(diff['worlds_added']) or 'none'
    removed = ', '.join(diff['worlds_removed']) or 'none'
    return (f'- **The Atlas Engine runs Archipelago {v}** (draft by Tools/update_archipelago.py: say what changes for players '
            f'before committing; Builds/ap-update/{v}/report.md has the details). World folders added: {added}; removed: {removed}.')


def pins_diff(repo, plan):
    out = []
    for key, (before, after) in plan.items():
        if before != after:
            out.extend(difflib.unified_diff(text_of(before).splitlines(keepends=True), text_of(after).splitlines(keepends=True),
                                            'a/' + repo.rel(key), 'b/' + repo.rel(key)))
    return ''.join(line if line.endswith('\n') else line + '\n' for line in out)


def write_pins(repo, files, state, plan, save):
    changed = [key for key, (before, after) in plan.items() if before != after]
    for key in changed:
        rel = repo.rel(key)
        if rel not in state['written']:  # still Atlas's own file: keep it for --rollback
            write_atomic(files.backup_path(key), plan[key][0])
            state['backups'][rel] = sha256_bytes(plan[key][0])
        state['written'][rel] = sha256_bytes(plan[key][1])
    save()  # before any file changes, so a rerun knows every byte this writes
    for key in changed:
        write_atomic(repo.paths[key], plan[key][1])
    return changed


# --- 7. Build, 8. Post-check ---

def core_version(core):
    return (load_json(os.path.join(core, 'core.json')) or {}).get('Archipelago')


def skip_old_zips(folder, names):
    # The build cache's copy leaves out every Archipelago zip; the verified one goes in instead.
    if os.path.basename(folder).lower() != 'downloads':
        return []
    return [n for n in names if re.fullmatch(r'Archipelago-.+\.zip', n, re.IGNORECASE)]


def cache_problems(engine, pins, signature, rebuilt):
    """What the build cache's engine (<cache>\\engine) lacks that the engine build would download, and from where: none of
    it is allowed here. Archipelago comes from the verified zip; the packages from PyPI only when the lock was rebuilt."""
    problems = []
    for rel, what in ((('python', 'python.exe'), 'Python (from python.org)'),
                      (('python', 'Lib', 'site-packages', 'pip', '__init__.py'), 'pip (from PyPI)')):
        if not os.path.isfile(os.path.join(engine, *rel)):
            problems.append(f'{os.path.join(*rel)}: {what}')
    tracker = os.path.join(engine, 'ap', 'worlds', 'tracker.apworld')
    if not os.path.isfile(tracker) or sha256_file(tracker) != pins['TrackerSha256'][1][0]:
        problems.append(f"ap\\worlds\\tracker.apworld with AtlasEngine.TrackerSha256: Universal Tracker {pins['TrackerVersion'][1][0]} (from FarisTheAncient's releases)")
    state = load_json(os.path.join(engine, 'engine.json')) or {}
    if not rebuilt and state.get('PackagesSignature') != signature:
        problems.append(f"engine.json's PackagesSignature {state.get('PackagesSignature')}, not the lock's {signature}: Archipelago's packages (from PyPI)")
    return problems


def build_phase(args, repo, v, origin, work, env, zip_path, sha, state, save, pins, requirements):
    log = os.path.join(work, 'build.log')
    core = os.path.join(repo.builds, 'engine-core')
    record = state['phases'].get('build') or {}
    stamp = os.path.join(core, 'core.json')
    if record.get('core_json_sha256') and os.path.isfile(stamp) and sha256_file(stamp) == record['core_json_sha256']:
        return record

    # The new index and lock are built into Atlas. Restored packages only (nothing from NuGet), no build servers left behind.
    done = run(['dotnet', 'build', os.path.join(repo.project, 'AP_Atlas.sln'), '--no-restore', '-nologo', '-nodeReuse:false'], env, cwd=repo.project, timeout=1800, log=log)
    if done.returncode != 0:
        raise Stop(8, f'dotnet build failed (exit {done.returncode}); log: {log}\n' + tail(done), inner=done.returncode)

    # The build cache, before the old core moves: the engine build may download only what this run allows.
    data, cache = os.path.join(repo.builds, 'engine-data'), os.path.join(repo.builds, f'engine-data-{v}')
    if not os.path.isdir(cache):
        if not os.path.isdir(os.path.join(data, 'engine')):
            raise Stop(8, f'{data} is missing: a setup from nothing downloads Python from python.org, which this run may not use.')
        missing = cache_problems(os.path.join(data, 'engine'), pins, requirements['signature'], requirements['rebuilt'])
        if missing:
            raise Stop(8, f"{data}\\engine lacks what the engine build would download, which this run may not:\n    " + '\n    '.join(missing))
        part = cache + '.part'
        if os.path.isdir(part):
            shutil.rmtree(part)  # this tool's own unfinished copy
        shutil.copytree(data, part, ignore=skip_old_zips)
        os.rename(part, cache)
    missing = cache_problems(os.path.join(cache, 'engine'), pins, requirements['signature'], requirements['rebuilt'])
    if missing:
        raise Stop(8, f'{cache}\\engine lacks what the engine build would download, which this run may not (put it there, or move '
                      f'{cache} away and the next run copies {data} again):\n    ' + '\n    '.join(missing))
    downloads = os.path.join(cache, 'engine', 'downloads')
    os.makedirs(downloads, exist_ok=True)
    target = os.path.join(downloads, f'Archipelago-{v}.zip')
    if not os.path.isfile(target) or sha256_file(target) != sha:
        copy_atomic(zip_path, target)

    aside = os.path.join(repo.builds, f'engine-core-{origin}')
    if os.path.isdir(core) and core_version(core) != v:
        if not os.path.exists(aside):
            try:
                os.rename(core, aside)
            except OSError as e:
                raise Stop(8, f"{core} couldn't be set aside as {aside} (is something using it?): {e}")
        elif core_version(aside) != origin:
            raise Stop(8, f'{aside} is in the way: it is not the {origin} core. Move it, then run again.')
        # else: an earlier run set the old core aside; build_engine.ps1 replaces what's left in engine-core.
    # From here engine-core may be half built: --rollback moves whatever is there aside, stamped or not.
    state['phases']['build'] = {'started': now(), 'old_core': aside}
    save()

    scratch = os.path.join(work, 'scratch')
    os.makedirs(scratch, exist_ok=True)
    cmd = ['powershell', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(HERE, 'build_engine.ps1'),
           '-Atlas', args.godot, '-Project', repo.project, '-Out', core, '-Cache', cache, '-ScratchRoot', scratch]
    done = run(cmd, env, cwd=repo.root, timeout=90 * 60, log=log)
    lines = [line.strip() for line in done.stdout.splitlines() if 'DONE' in line or line.strip().startswith('The core:')]
    if done.returncode != 0:
        raise Stop(8, f'build_engine.ps1 failed (exit {done.returncode}); log: {log}\n' + tail(done), inner=done.returncode)
    hosts = sorted({origin_of(u) for name in ('engine-build.log', 'engine-check.log') for u in re.findall(r'https?://[^\s"\'<>)]+', read_text(os.path.join(cache, name)))})
    record = {'started': state['phases']['build']['started'], 'core_json_sha256': sha256_file(stamp) if os.path.isfile(stamp) else None,
              'done_lines': lines, 'cache': cache, 'old_core': aside, 'hosts_in_logs': hosts}
    state['phases']['build'] = record
    save()
    return record


def read_text(path):
    try:
        with open(path, encoding='utf-8', errors='replace') as f:
            return f.read()
    except OSError:
        return ''


def core_snapshot(core):
    # Compiled files aren't the core (build_engine.ps1 never ships them), and a lab run's Python may write them there.
    out = {}
    for dirpath, folders, names in os.walk(core):
        folders[:] = [f for f in folders if f != '__pycache__']
        for name in names:
            if name.lower().endswith(('.pyc', '.pyo')):
                continue
            path = os.path.join(dirpath, name)
            out[os.path.relpath(path, core).replace('\\', '/')] = [os.path.getsize(path), sha256_file(path)]
    return dict(sorted(out.items()))


def post_check(repo, v, work, pins, index, signature):
    core = os.path.join(repo.builds, 'engine-core')
    stamp = load_json(os.path.join(core, 'core.json')) or {}
    tracker = pins['TrackerVersion'][1][0]
    problems = []
    for name, want in (('Archipelago', v), ('Tracker', tracker), ('PackagesSignature', signature)):
        if stamp.get(name) != want:
            problems.append(f'core.json says {name} {stamp.get(name)}, not {want}')
    m = re.search(r'^__version__\s*=\s*["\']([^"\']+)["\']', read_text(os.path.join(core, 'archipelago', 'Utils.py')), re.MULTILINE)
    if not m or m.group(1) != v:
        problems.append(f"the core's Utils.py says {m.group(1) if m else 'nothing'}, not {v}")
    present = [w for w in index.get('worlds', {}) if os.path.isdir(os.path.join(core, 'archipelago', 'worlds', w))]
    if present:
        problems.append('the core holds worlds it should unpack on demand: ' + ', '.join(present))
    if problems:
        raise Stop(9, f'The new core in {core} failed its check: ' + '; '.join(problems) + '.')
    snapshot = core_snapshot(core)
    path = os.path.join(work, 'core_snapshot.json')
    write_json(path, snapshot)
    return {'core': core, 'files': len(snapshot), 'snapshot': path, 'snapshot_sha256': sha256_file(path)}


# --- 9. Report ---

class Report:
    def __init__(self, repo, v, work, args):
        self.repo, self.v, self.work = repo, v, work
        self.data = {'version': v, 'started': now(), 'dry_run': args.dry_run, 'skip_build': args.skip_build, 'arguments': sys.argv[1:]}

    def __setitem__(self, key, value):
        self.data[key] = value

    def __getitem__(self, key):
        return self.data[key]

    def write(self):
        self.data['finished'] = now()
        write_json(os.path.join(self.work, 'report.json'), self.data)
        write_atomic(os.path.join(self.work, 'report.md'), self.markdown().encode('utf-8'))

    def markdown(self):
        d, v = self.data, self.v
        result = d.get('result') or {}
        out = [f'# Archipelago {v}: update report', '',
               f'- Result: exit {result.get("exit")}: {result.get("message", "")}'.rstrip(),
               f'- From: {d.get("from", "?")}' + (' (dry run: nothing tracked was written)' if d['dry_run'] else ''),
               f'- Started {d["started"]}, finished {d.get("finished")}', '']
        if d.get('pins'):
            out += ['## Pins', ''] + [f'- `{p["file"]}:{p["line"]}` {p["name"]}: {p["old"]} -> {p["new"]}' for p in d['pins']] + ['']
        z, release = d.get('zip') or {}, d.get('release') or {}
        if z or release:
            out += ['## Release', '', f'- Zip: {z.get("url")} (fetched from {z.get("from")})', f'- SHA-256: {z.get("sha256")}, {z.get("bytes")} bytes',
                    f'- Commit: {release.get("commit")}', f'- Published: {release.get("published_at")} ({release.get("html_url")})', '']
        r = d.get('requirements')
        if r:
            out += ['## Requirements', '', f'- Signature: {r["old_signature"]} -> {r["signature"]}' + ('' if r['rebuilt'] else ' (unchanged: the lock keeps its hashes)')]
            for kind, rows in (r.get('packages') or {}).items():
                out.append(f'- Packages {kind}: {", ".join(rows) or "none"}')
            out.append('')
        w = d.get('worlds')
        if w:
            out += ['## Worlds', '', f'- World folders added: {", ".join(w["worlds_added"]) or "none"}', f'- World folders removed: {", ".join(w["worlds_removed"]) or "none"}',
                    f'- Games added: {", ".join(w["games_added"]) or "none"}', f'- Games removed: {", ".join(w["games_removed"]) or "none"}']
            if w.get('needs_flag'):
                out.append('- A real run needs --allow-removed-worlds (read the release notes first).')
            out.append('')
        lic = d.get('licences')
        if lic:
            out += ['## Licences (needs a person)', '']
            if lic.get('compared_with'):
                out += [f'- Against {lic["compared_with"]}: {len(lic["added"])} core files added, {len(lic["removed"])} removed (licence_diff.txt)',
                        f'- New licence files or folders: {", ".join(lic["flagged"]) or "none"}']
            else:
                out.append(f'- {lic.get("note")}')
            out.append('')
        t = d.get('tracker')
        out += ['## Universal Tracker', '', '- ' + (('not checked' if not t else f'check failed: {t.get("error")}' if not t.get('checked')
                else f'newest {t.get("newest")}, Atlas pins {t.get("pinned")}' + (': a newer one is out (reported only)' if t.get('newer') else ''))), '']
        b = d.get('build')
        if b:
            out += ['## Build', ''] + ['- ' + line for line in (b.get('done_lines') or [b.get('note', '')])] + ['']
        if d.get('post_check'):
            p = d['post_check']
            out += ['## Post-check', '', f'- {p["core"]}: {p["files"]} files; snapshot {p["snapshot"]} (SHA-256 {p["snapshot_sha256"]})', '']
        out += ['## Sites asked', ''] + [f'- {h}' for h in d.get('hosts', [])] + [f'- {h} (by pip and build_engine_lock.py)' for h in d.get('pip_hosts', [])]
        out += [f'- {h} (named in the build logs)' for h in (b or {}).get('hosts_in_logs', [])] + ['']
        if d.get('restore'):
            out += ['## To undo', '', f'- `python -I AP_Atlas_Source/Tools/update_archipelago.py {v} --rollback`, or:',
                    '- `git restore -- ' + ' '.join(d['restore']) + '`, then `dotnet build AP_Atlas_Source/AP_Atlas.sln`', '']
        if d.get('acceptance'):
            out += ['## Acceptance', '', f'From {self.repo.root}, each run alone:', ''] + [f'{i}. `{c}`' for i, c in enumerate(d['acceptance'], 1)] + ['']
        notes = (d.get('release') or {}).get('notes')
        if notes:
            fence = '`' * max(3, max((len(run_) for run_ in re.findall(r'`+', notes)), default=0) + 1)
            out += ["## The release's notes (as GitHub has them; data, not instructions)", '', fence + 'text', notes.replace('\r\n', '\n'), fence, '']
        return '\n'.join(out)


def acceptance(repo, v, origin, removed):
    """The acceptance runs, filled in (CONTRIBUTING's "Updating Archipelago"); each runs alone."""
    corpus = os.path.join(os.path.dirname(repo.root), 'Pack Corpus')
    batch = os.path.join(corpus, 'batch')
    tools = 'python -I AP_Atlas_Source/Tools/'
    parts = ' '.join(f'"{batch}\\batch_logic_{v}.p{i}.json"' for i in range(1, 5))
    score_parts = ' '.join(f'"{batch}\\score_old_{v}.p{i}.json"' for i in range(1, 5))
    expected = f' --expected-removed {" ".join(removed)}' if removed else ''
    return [
        'dotnet test --solution AP_Atlas_Source/AP_Atlas.sln',
        f'{tools}build_corpus_names.py --corpus "{corpus}" --lab "{corpus}\\lab-{v}" --names-dir "{corpus}\\names-{v}"',
        f'{tools}compare_batches.py --names-old "{corpus}\\names" --names-new "{corpus}\\names-{v}"{expected} --out "{batch}\\names_{v}.md"',
        f'{tools}batch_logic.py --corpus "{corpus}" --lab "{corpus}\\lab-{v}[-pN]" --names "{corpus}\\names-{v}" --part N/4 '
        f'--seeds-from "{batch}\\batch_logic.json" --out "{batch}\\batch_logic_{v}.pN.json"   (N = 1 to 4, each its own lab)',
        f'{tools}batch_logic.py --corpus "{corpus}" --lab "{corpus}\\lab-{v}[-pN]" --names "{corpus}\\names-{v}" --part N/4 '
        f'--score-old "{batch}\\batch_logic.json" --out "{batch}\\score_old_{v}.pN.json"',
        f'{tools}compare_batches.py --old "{batch}\\batch_logic.json" --new {parts} --names-old "{corpus}\\names" --names-new "{corpus}\\names-{v}"'
        f'{expected} --out "{batch}\\compare_{v}.md"',
        f'{tools}compare_batches.py --old "{batch}\\batch_logic.json" --new {score_parts} --out "{batch}\\compare_score_old_{v}.md"',
        f'{tools}batch_logic.py --corpus "{corpus}" --core "{repo.builds}\\engine-core-{origin}" --lab "{corpus}\\lab-{origin}-control" --names "{corpus}\\names" '
        f'--games "{batch}\\compare_{v}.regressions.txt" --seeds-from "{batch}\\batch_logic_{v}.json" --out "{batch}\\batch_control.json"',
        f'{tools}compare_batches.py --old "{batch}\\batch_logic.json" --new {parts} --control "{batch}\\batch_control.json" --names-old "{corpus}\\names" '
        f'--names-new "{corpus}\\names-{v}"{expected} --out "{batch}\\compare_{v}.md"',
        f'AP_Atlas_Source/Tools/run_selftest.ps1 with ATLAS_SELFTEST_PACKS, ATLAS_SELFTEST_PACK_NAMES="{corpus}\\names-{v}" and '
        f'ATLAS_SELFTEST_PACKS_FINDINGS="{corpus}\\lab-{v}\\findings_{v}.json" (never re-record the baseline here)',
        'AP_Atlas_Source/Tools/run_selftest.ps1   (without ATLAS_SELFTEST_SETUP: a setup from nothing goes to python.org)',
        f'{tools}update_archipelago.py {v}   (says "already at {v}" and checks the core against its snapshot)',
    ]


# --- --rollback and --latest ---

def rollback(args, repo, v, work, env):
    state_path = os.path.join(work, 'state.json')
    state = load_json(state_path)
    if not isinstance(state, dict) or state.get('version') != v:
        raise Stop(3, f'There is no run of {v} to roll back ({state_path}).')
    must_run_alone(args, repo, env)
    backups = state.get('backups', {})
    edited = []
    for rel, written in state.get('written', {}).items():
        path = os.path.join(repo.root, *rel.split('/'))
        have = sha256_file(path) if os.path.isfile(path) else None
        if have not in (written, backups.get(rel)):
            edited.append(rel)
    if edited:
        raise Stop(3, 'Changed since this tool wrote them, so nothing was put back: ' + ', '.join(edited))
    restored = []
    for rel, original in backups.items():
        source = os.path.join(work, 'backup', *rel.split('/'))
        data = read_bytes(source)
        if sha256_bytes(data) != original:
            raise Stop(3, f'The backup {source} is not the file this tool kept.')
        path = os.path.join(repo.root, *rel.split('/'))
        if not os.path.isfile(path) or sha256_file(path) != original:
            write_atomic(path, data)
            restored.append(rel)
    core = os.path.join(repo.builds, 'engine-core')
    origin = state.get('from')
    aside = os.path.join(repo.builds, f'engine-core-{origin}')
    # Whatever the build left in engine-core goes aside, stamped or not (a build stopped partway leaves no core.json); a core
    # that is still the old one stays.
    built = bool(state.get('phases', {}).get('build')) or os.path.isdir(aside)
    if os.path.isdir(core) and built and core_version(core) != origin:
        failed, n = core + f'-{v}-failed', 1
        while os.path.exists(failed):
            n += 1
            failed = core + f'-{v}-failed-{n}'
        try:
            os.rename(core, failed)
        except OSError as e:
            raise Stop(3, f"{core} couldn't be moved aside as {failed} (is something using it?), so the old core in {aside} isn't back: {e}")
        print(f'What the build left in Builds\\engine-core is now {failed}.')
    if os.path.isdir(aside):
        if os.path.exists(core):
            raise Stop(3, f"The old core {aside} can't go back: {core} is in the way (its core.json says {core_version(core)}). "
                          'Move one of them, then run this again.')
        try:
            os.rename(aside, core)
        except OSError as e:
            raise Stop(3, f"The old core {aside} couldn't be renamed back to {core} (is something using it?): {e}")
        print(f'{aside} is Builds\\engine-core again.')
    # This run's records stay, renamed: the next run starts from the files as they are now.
    n = 1
    while os.path.exists(os.path.join(work, f'state.rolled-back-{n}.json')):
        n += 1
    os.rename(state_path, os.path.join(work, f'state.rolled-back-{n}.json'))
    if os.path.isdir(os.path.join(work, 'backup')):
        os.rename(os.path.join(work, 'backup'), os.path.join(work, f'backup.rolled-back-{n}'))
    print('Put back: ' + (', '.join(restored) or 'nothing (the files were already their originals)') + '. Then: dotnet build AP_Atlas_Source/AP_Atlas.sln')
    return 0


def latest(args, repo):
    pinned = find_once(text_of(Files(repo, '', {}).current('engine')), PINS[0][2], repo.rel('engine')).group(1)
    try:
        tag = (Net().get_json(f'{args.api_base}/repos/{UPSTREAM}/releases/latest') or {}).get('tag_name') or ''
    except (urllib.error.URLError, OSError, ValueError) as e:
        raise Stop(4, f"GitHub couldn't be asked for the newest release: {e}")
    if not VERSION.match(tag) or not VERSION.match(pinned):
        raise Stop(4, f'The newest release is {tag!r}; Atlas pins {pinned!r}: not both X.Y.Z.')
    if version_key(tag) > version_key(pinned):
        print(f'Archipelago {tag} is out; Atlas pins {pinned}. Update: python -I AP_Atlas_Source/Tools/update_archipelago.py {tag} --dry-run')
        return 20
    print(f'Atlas pins {pinned}, the newest Archipelago release.')
    return 0


# --- The run ---

def update(args, repo, v, work, report):
    dry = args.dry_run
    state_path = os.path.join(work, 'state.json')
    state = load_json(state_path)
    state = state if isinstance(state, dict) and state.get('version') == v else {}
    for key in ('phases', 'written', 'backups'):
        state.setdefault(key, {})
    if args.fresh:
        state['phases'] = {}
    files = Files(repo, work, state)
    env = child_env(os.path.join(work, 'home'))
    # The variables left out by name only (never their values).
    report['isolation'] = dict({k: env[k] for k in ISOLATION}, removed=sorted(k for k in os.environ if left_out(k)))

    def save():
        if not dry:
            write_json(state_path, state)

    # 1. Preflight
    origin = preflight(args, repo, v, files, state, env)
    if origin is None:
        snapshot = load_json(os.path.join(work, 'core_snapshot.json'))
        core = os.path.join(repo.builds, 'engine-core')
        if snapshot and core_snapshot(core) != snapshot:
            raise Stop(9, f'Every pin says {v}, but {core} changed since its post-check ({work}\\core_snapshot.json).')
        print(f'Already at {v}: every pin and Builds\\engine-core say so' + (', and the core matches its snapshot.' if snapshot else '.'))
        return 0
    report['from'] = origin
    before = read_pins(repo, files.original)
    state.update(format='atlas-ap-update/1', version=v)
    state.setdefault('from', origin)
    state.setdefault('started', now())
    save()

    # 2. Release
    net = Net()
    try:
        release = release_phase(args, v, net, work, before['TrackerVersion'][1][0])
        report['release'] = {k: release.get(k) for k in ('commit', 'published_at', 'html_url', 'notes')}
        report['tracker'] = release.get('tracker') if not args.no_tracker_check else None

        # 3. Download
        python_version = before['PythonVersion'][1][0]
        zip_path, sha, facts = download_phase(args, v, net, work, release, python_version)
        report['zip'] = release['zip']
        report['licences'] = licence_diff(repo, origin, v, work, facts)

        # 4. Requirements
        requirements = requirements_phase(args, v, work, env, files, state, facts, net)
        report['requirements'] = requirements

        # 5. Index
        index, diff = index_phase(args, repo, v, work, env, files, state, zip_path, sha)
        report['worlds'] = diff
        removed_worlds(args, v, diff)
    finally:
        report['hosts'] = net.hosts
        report['pip_hosts'] = net.pip_hosts
        save()  # what verified so far, also when a phase refused

    # 6. Pins
    plan = pin_plan(repo, v, sha, files, requirements, os.path.join(work, 'new', 'archipelago_worlds.json'), diff)
    after = read_pins(repo, lambda key: plan[key][1])
    report['pins'] = [{'file': repo.rel(key), 'line': after[name][2], 'name': name, 'old': ' '.join(before[name][1]), 'new': ' '.join(after[name][1])}
                      for key, name, _ in PINS if before[name][1] != after[name][1]]
    if before['index'][1] != after['index'][1]:
        report['pins'].append({'file': repo.rel('index'), 'line': 1, 'name': 'archipelago', 'old': before['index'][1][0], 'new': after['index'][1][0]})
    report['acceptance'] = acceptance(repo, v, origin, diff['worlds_removed'])
    if dry:
        text = pins_diff(repo, plan)
        write_atomic(os.path.join(work, 'new', 'pins.diff'), text.encode('utf-8'))
        print(text.replace('\r\n', '\n'))
        print(f'Dry run: nothing tracked was written. The diff above is {work}\\new\\pins.diff; the report is {work}\\report.md.')
        return 0
    write_pins(repo, files, state, plan, save)
    report['restore'] = sorted(state['written'])
    state['phases']['pins'] = {'done': now()}
    save()

    # 7. Build, 8. Post-check
    if args.skip_build:
        report['build'] = {'note': 'skipped (--skip-build): not ready to ship'}
        print(f'Pinned {v}; the core was not built (--skip-build), so this is not ready to ship. Report: {work}\\report.md')
        return 0
    report['build'] = build_phase(args, repo, v, state['from'], work, env, zip_path, sha, state, save, after, requirements)
    report['post_check'] = post_check(repo, v, work, after, index, requirements['signature'])
    state['phases']['post'] = report['post_check']
    save()
    print(f'Archipelago {v} is pinned and its core built and checked. Report: {work}\\report.md')
    return 0


def main():
    repo_default = os.path.dirname(os.path.dirname(HERE))
    parser = argparse.ArgumentParser(description='Moves the Atlas Engine to a new Archipelago release (see the docstring).')
    parser.add_argument('version', nargs='?')
    parser.add_argument('--dry-run', action='store_true')
    parser.add_argument('--fresh', action='store_true', help="ask GitHub and download again (the zip's recorded SHA-256 still counts)")
    parser.add_argument('--zip', help="Archipelago's release zip, already downloaded")
    parser.add_argument('--allow-removed-worlds', action='store_true')
    parser.add_argument('--no-tracker-check', action='store_true')
    parser.add_argument('--engine-python')
    parser.add_argument('--godot', default=os.environ.get('ATLAS_GODOT'))
    parser.add_argument('--repo', default=repo_default)
    parser.add_argument('--skip-build', action='store_true', help='tests only: the report then says it is not ready to ship')
    parser.add_argument('--api-base', default=GITHUB_API)
    parser.add_argument('--download-base', default=GITHUB)
    parser.add_argument('--pypi-base', default=PYPI)
    parser.add_argument('--latest', action='store_true')
    parser.add_argument('--rollback', action='store_true')
    args = parser.parse_args()
    for name, default in (('api_base', GITHUB_API), ('download_base', GITHUB), ('pypi_base', PYPI)):
        value = getattr(args, name).rstrip('/')
        if value != default and not LOOPBACK.match(value):
            parser.error(f'--{name.replace("_", "-")} {value}: only a site on this PC (http://127.0.0.1:<port>) can stand in for {default}.')
        setattr(args, name, value)
    repo = Repo(args.repo)
    args.engine_python = os.path.abspath(args.engine_python or os.path.join(repo.project, 'PortableData', 'engine', 'python', 'python.exe'))
    args.godot = os.path.abspath(args.godot or os.path.join(repo.root, 'Godot_Engine', 'Godot_v4.7.2-stable_mono_win64', 'Godot_v4.7.2-stable_mono_win64_console.exe'))
    try:
        if args.latest:
            return latest(args, repo)
        if not args.version or not VERSION.match(args.version):
            parser.error('give the version as X.Y.Z (or --latest)')
        v = args.version
        work = os.path.join(repo.builds, 'ap-update', v)
        os.makedirs(work, exist_ok=True)
        if args.rollback:
            return rollback(args, repo, v, work, child_env(os.path.join(work, 'home')))
    except Stop as stop:
        print(f'STOPPED (exit {stop.code}): {stop}')
        return stop.code
    report = Report(repo, v, work, args)
    code = 1
    try:
        code = update(args, repo, v, work, report)
        report['result'] = {'exit': code, 'message': 'done' if not args.dry_run else 'dry run done'}
    except Stop as stop:
        code = stop.code
        report['result'] = {'exit': stop.code, 'message': str(stop), 'inner': stop.inner}
        print(f'STOPPED (exit {stop.code}): {stop}')
    finally:
        if 'result' not in report.data:
            report['result'] = {'exit': 1, 'message': 'crashed (the traceback is above)'}
        # Only a run past the preflight reports. One it stopped, or that found the update done (the acceptance's last
        # step), leaves the last run's report as it is: its pins, zip, licences and restore list stay.
        if 'from' in report.data:
            try:
                report.write()
            except OSError as e:
                print(f"The report couldn't be written: {e}")
    return code


if __name__ == '__main__':
    sys.exit(main())
