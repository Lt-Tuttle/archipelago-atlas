"""Zip the AP_Atlas_Source project (minus build output) into <workspace>/_Backups/."""
import datetime
import os
import zipfile

# Paths are resolved relative to this script: <workspace>/AP_Atlas_Source/Scripts/Tools/CreateBackup.py
source_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
backup_dir = os.path.join(os.path.dirname(source_dir), "_Backups")

os.makedirs(backup_dir, exist_ok=True)

timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
zip_path = os.path.join(backup_dir, f"AP_Atlas_Backup_{timestamp}.zip")

# Build output, editor caches and downloaded packs/logs are regenerated, so leave them out.
exclude_dirs = {".godot", ".mono", "bin", "obj"}
exclude_portable = {"packs", "logs"}

with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zipf:
    for root, dirs, files in os.walk(source_dir):
        rel_root = os.path.relpath(root, source_dir)
        dirs[:] = [
            d for d in dirs
            if d not in exclude_dirs and not (rel_root == "PortableData" and d in exclude_portable)
        ]
        for file in files:
            file_path = os.path.join(root, file)
            zipf.write(file_path, os.path.relpath(file_path, source_dir))

print(f"Backup created successfully: {zip_path}")
