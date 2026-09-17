#!/usr/bin/env python3
"""Package the built plugin and generate a Jellyfin repository manifest."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile


def package(output, release_url, timestamp):
    root = Path(__file__).resolve().parent.parent
    project = ET.parse(root / "src/Jellyfin.Plugin.Siphon/Jellyfin.Plugin.Siphon.csproj").getroot()
    version = project.findtext("PropertyGroup/Version")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        raise ValueError("Plugin version must have four numeric components")
    dll = root / "src/Jellyfin.Plugin.Siphon/bin/Release/net10.0/Jellyfin.Plugin.Siphon.dll"
    if not dll.is_file():
        raise FileNotFoundError("Build the Release plugin before packaging")
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"siphon-{version}.zip"
    description = "Bring Stremio catalogs, metadata, subtitles and stream versions to native Jellyfin libraries."
    metadata = {
        "guid": "b2df1c14-4b7e-4e7b-9a95-8f9ad8d2b0c1", "name": "Siphon",
        "description": description, "overview": "Stremio addons in Jellyfin", "owner": "moodiness",
        "category": "General", "version": version, "targetAbi": "12.1.0.0", "timestamp": timestamp,
        "imagePath": "siphon.png"
    }
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
        for name, data in [(dll.name, dll.read_bytes()), ("LICENSE", (root / "LICENSE").read_bytes()),
                           ("siphon.png", (root / "assets/siphon.png").read_bytes()),
                           ("meta.json", json.dumps(metadata, indent=2).encode())]:
            entry = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            bundle.writestr(entry, data)
    content = archive.read_bytes()
    release = {
        "version": version, "targetAbi": "12.1.0.0",
        "sourceUrl": release_url.rstrip("/") + "/" + archive.name,
        "checksum": hashlib.md5(content).hexdigest(),
        "timestamp": timestamp,
        "changelog": "Fix media-detail loading during catalog and metadata synchronization by separating synchronization from native library writes. Clarify addon-defined catalog filters without changing their parameter names or values."
    }
    manifest = [{k: v for k, v in metadata.items() if k not in ("version", "targetAbi", "timestamp", "imagePath")} | {
        "imageUrl": "https://raw.githubusercontent.com/moodiness/jellyfin-plugin-siphon/main/assets/siphon.png",
        "versions": [release]
    }]
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (output / "SHA256SUMS").write_text(hashlib.sha256(content).hexdigest() + "  " + archive.name + "\n", encoding="utf-8")
    print(archive)
    print(output / "manifest.json")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path("artifacts"))
    parser.add_argument("--release-url", default="https://github.com/moodiness/jellyfin-plugin-siphon/releases/download/v1.4.1")
    parser.add_argument("--timestamp", default="2026-09-17T00:00:00Z")
    args = parser.parse_args()
    package(args.output, args.release_url, args.timestamp)
