"""Adds a release to manifest.json so Jellyfin offers it as an update.

Usage: update_manifest.py <manifest> <zip> <version> <source_url> <target_abi>
"""
import datetime
import hashlib
import json
import sys

manifest_path, zip_path, version, source_url, target_abi = sys.argv[1:6]

with open(zip_path, "rb") as f:
    checksum = hashlib.md5(f.read()).hexdigest()

with open(manifest_path, encoding="utf-8") as f:
    manifest = json.load(f)

plugin = manifest[0]
plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
plugin["versions"].insert(0, {
    "version": version,
    "changelog": f"See https://github.com/brudertomate/jellyfin-plugin-mergeversions-bato/releases/tag/v{version}",
    "targetAbi": target_abi,
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})

with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
