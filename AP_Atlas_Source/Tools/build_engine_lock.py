"""Builds the hash-pinned package lock the Atlas Engine installs Archipelago's Python packages from.

Usage:
    python Tools/build_engine_lock.py <Archipelago source folder> <engine python.exe> <output lock file>
        [--index-url URL] [--json-base URL]   (PyPI's own by default; anything else only http://127.0.0.1:<port>, for the tests)

It applies the same filter as AtlasEngine.FilteredRequirements (no desktop GUI or build tools, plus the packages some
worlds import without listing them), asks pip which exact packages that resolves to for the engine's Python on Windows
(a dry run: nothing is installed), and looks up the SHA-256 of every file PyPI publishes for each of those versions.
The engine then installs with --require-hashes, so only exactly these files can be installed.

Re-run it whenever Archipelago's pinned version (AtlasEngine.ArchipelagoVersion) changes, and commit the new lock.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import tempfile
import time
import urllib.request
from datetime import date

# Keep these in step with AtlasEngine.SkippedRequirements / ExtraRequirements.
SKIPPED = {"kivy", "kivymd", "cython", "pyshortcuts"}
EXTRA = ["requests", "setuptools<81"]
USER_AGENT = "TheArchipelagoAtlas-build-tools (+https://github.com/Lt-Tuttle/archipelago-atlas)"
INDEX_URL = "https://pypi.org/simple"
JSON_BASE = "https://pypi.org/pypi"
LOOPBACK = re.compile(r"^http://127\.0\.0\.1:\d{1,5}(/.*)?$")
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def filtered_requirements(ap_dir):
    lines = []
    with open(os.path.join(ap_dir, "requirements.txt"), encoding="utf-8") as f:
        for raw in f.read().splitlines():
            line = raw.split("#")[0].strip()
            if not line or line.startswith("-"):
                continue
            name = re.match(r"^[A-Za-z0-9_.\-]+", line).group(0).lower()
            if name in SKIPPED or "git+" in line:
                continue
            lines.append(line)
    lines.extend(EXTRA)
    return "\n".join(lines) + "\n"


def signature(text):
    # Same as AtlasEngine.PackagesSignature: the first 16 hex digits (upper case) of the SHA-256 of the UTF-8 text.
    return hashlib.sha256(text.encode("utf-8")).hexdigest().upper()[:16]


def pypi_hashes(name, version, json_base=JSON_BASE):
    url = f"{json_base}/{name}/{version}/json"
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
    # A site on this PC is never reached through a proxy.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({})) if LOOPBACK.match(json_base) else urllib.request.build_opener()
    with opener.open(request, timeout=30) as response:
        data = json.load(response)
    return sorted({f["digests"]["sha256"] for f in data.get("urls", []) if f.get("digests", {}).get("sha256")})


def main(ap_dir, python_exe, out_path, index_url=INDEX_URL, json_base=JSON_BASE):
    text = filtered_requirements(ap_dir)
    with tempfile.TemporaryDirectory() as tmp:
        req = os.path.join(tmp, "requirements.txt")
        report = os.path.join(tmp, "report.json")
        with open(req, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        cmd = [python_exe, "-m", "pip", "install", "--dry-run", "--ignore-installed", "--quiet", "--disable-pip-version-check",
               "--only-binary=:all:", "--report", report, "-r", req]
        if index_url != INDEX_URL:
            cmd[4:4] = ["--index-url", index_url]
        subprocess.run(cmd, check=True, stdin=subprocess.DEVNULL, creationflags=NO_WINDOW)
        with open(report, encoding="utf-8") as f:
            resolved = json.load(f)
    pins = sorted(((p["metadata"]["name"], p["metadata"]["version"]) for p in resolved["install"]), key=lambda x: x[0].lower())

    py_version = subprocess.run([python_exe, "-c", "import platform; print(platform.python_version())"], capture_output=True, text=True, check=True,
                                stdin=subprocess.DEVNULL, creationflags=NO_WINDOW).stdout.strip()
    ap_version = "?"
    try:
        with open(os.path.join(ap_dir, "Utils.py"), encoding="utf-8") as f:
            m = re.search(r'__version__\s*=\s*"([^"]+)"', f.read())
            ap_version = m.group(1) if m else "?"
    except OSError:
        pass

    out = [
        f"# The Atlas Engine's Python packages for Archipelago {ap_version}, Python {py_version} on Windows (64-bit wheels only):",
        "# exact versions, and the SHA-256 of every file PyPI publishes for them. Atlas installs these with --require-hashes,",
        f"# so nothing else can be installed. Built by Tools/build_engine_lock.py on {date.today().isoformat()}; rebuild when",
        "# AtlasEngine.ArchipelagoVersion changes.",
        f"# requirements-signature: {signature(text)}",
        "",
    ]
    for name, version in pins:
        hashes = pypi_hashes(name, version, json_base)
        if not hashes:
            raise SystemExit(f"PyPI lists no files for {name} {version}")
        out.append(f"{name}=={version} \\")
        out.extend(f"    --hash=sha256:{h}" + (" \\" if i < len(hashes) - 1 else "") for i, h in enumerate(hashes))
        time.sleep(0.2)  # one request at a time, gently
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out) + "\n")
    print(f"{len(pins)} packages locked (signature {signature(text)}): " + ", ".join(f"{n} {v}" for n, v in pins))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(usage=__doc__)
    parser.add_argument("ap_dir")
    parser.add_argument("python_exe")
    parser.add_argument("out_path")
    parser.add_argument("--index-url", default=INDEX_URL)
    parser.add_argument("--json-base", default=JSON_BASE)
    args = parser.parse_args()
    for name, value, default in (("--index-url", args.index_url, INDEX_URL), ("--json-base", args.json_base, JSON_BASE)):
        if value.rstrip("/") != default and not LOOPBACK.match(value):
            parser.error(f"{name} {value}: only PyPI, or a site on this PC (http://127.0.0.1:<port>).")
    main(args.ap_dir, args.python_exe, args.out_path, args.index_url.rstrip("/"), args.json_base.rstrip("/"))
