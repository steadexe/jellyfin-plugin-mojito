#!/usr/bin/env python3
"""Build the Mojito plugin, package the release zip and update manifest.json.

Usage:
  python3 scripts/build_release.py                      # build + zip, prints the zip path
  python3 scripts/build_release.py --update-manifest \
      --source-url https://github.com/OWNER/REPO/releases/download/vX/ZIP \
      --zip artifacts/Jellyfin.Plugin.Mojito_X.zip      # upsert the manifest entry

The zip contains the plugin dll and build.yaml (read by Jellyfin on install).
The repository manifest format follows https://jellyfin.org/posts/plugin-updates/
"""

import argparse
import hashlib
import json
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parent.parent
BUILD_YAML = ROOT / "build.yaml"
ARTIFACTS = ROOT / "artifacts"
MANIFEST = ROOT / "manifest.json"
PROJECT = ROOT / "Jellyfin.Plugin.Mojito" / "Jellyfin.Plugin.Mojito.csproj"
DLL = ROOT / "Jellyfin.Plugin.Mojito" / "bin" / "Release" / "net10.0" / "Jellyfin.Plugin.Mojito.dll"


def parse_build_yaml() -> dict:
    """Parse the small YAML subset used by build.yaml (scalars + '>' blocks)."""
    text = BUILD_YAML.read_text(encoding="utf-8")
    result = {}
    key = None
    buffer = []
    for line in text.splitlines():
        match = re.match(r"^(\w+):\s*(.*)$", line)
        if match:
            if key and result[key] == ">" and buffer:
                result[key] = " ".join(" ".join(buffer).split())
            key = match.group(1)
            result[key] = match.group(2).strip().strip('"')
            buffer = []
        elif key and (line.startswith("  ") or not line.strip()):
            if result[key] in (">", "|", ">'", ">-"):
                buffer.append(line.strip())
        elif not line.strip():
            continue
    if key and result[key] in (">", "|", ">'", ">-") and buffer:
        result[key] = " ".join(" ".join(buffer).split())
    return result


def run_build() -> None:
    print("Building plugin (Release)...", file=sys.stderr)
    subprocess.run(
        ["dotnet", "build", str(PROJECT), "-c", "Release"],
        cwd=ROOT,
        check=True,
        stdout=sys.stderr,
    )


def package(meta: dict) -> Path:
    ARTIFACTS.mkdir(exist_ok=True)
    version = meta["version"]
    zip_path = ARTIFACTS / f"Jellyfin.Plugin.Mojito_{version}.zip"
    with ZipFile(zip_path, "w", ZIP_DEFLATED) as zf:
        zf.write(DLL, "Jellyfin.Plugin.Mojito.dll")
        zf.write(BUILD_YAML, "build.yaml")
    print(f"Packaged {zip_path}", file=sys.stderr)
    return zip_path


def md5_of(path: Path) -> str:
    return hashlib.md5(path.read_bytes()).hexdigest()


def update_manifest(meta: dict, zip_path: Path, source_url: str) -> None:
    version = meta["version"]
    entry = {
        "checksum": md5_of(zip_path),
        "changelog": meta.get("changelog", ""),
        "targetAbi": meta["targetAbi"],
        "sourceUrl": source_url,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "version": version,
    }

    manifest = []
    if MANIFEST.exists():
        try:
            manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            manifest = []
        if not isinstance(manifest, list):
            manifest = []

    plugin_entry = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
    if plugin_entry is None:
        plugin_entry = {
            "category": meta.get("category", "General"),
            "guid": meta["guid"],
            "name": meta["name"],
            "description": meta.get("description", ""),
            "owner": meta.get("owner", ""),
            "overview": meta.get("overview", ""),
            "versions": [],
        }
        manifest.append(plugin_entry)

    versions = [v for v in plugin_entry.get("versions", []) if v.get("version") != version]
    versions.insert(0, entry)
    plugin_entry["versions"] = versions

    MANIFEST.write_text(json.dumps(manifest, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"manifest.json updated: {meta['name']} {version} ({entry['checksum']})", file=sys.stderr)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--update-manifest", action="store_true")
    parser.add_argument("--source-url", default="")
    parser.add_argument("--zip", default="")
    args = parser.parse_args()

    meta = parse_build_yaml()
    for required in ("name", "guid", "version", "targetAbi"):
        if required not in meta:
            sys.exit(f"build.yaml is missing required key: {required}")

    zip_path = Path(args.zip) if args.zip else None
    if not args.update_manifest:
        run_build()
        zip_path = package(meta)
        print(zip_path)
        return

    if zip_path is None or not zip_path.exists():
        sys.exit("--update-manifest requires an existing zip (--zip)")
    if not args.source_url:
        sys.exit("--update-manifest requires --source-url")
    update_manifest(meta, zip_path, args.source_url)


if __name__ == "__main__":
    main()
