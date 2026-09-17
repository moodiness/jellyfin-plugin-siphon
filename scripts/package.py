#!/usr/bin/env python3
"""Package the built plugin and generate a Jellyfin repository manifest."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile


def runtime_dependencies(root, build):
    """Bundle only MonoTorrent's runtime closure, not Jellyfin's server assemblies."""
    assets = json.loads((root / "src/Jellyfin.Plugin.Siphon/obj/project.assets.json").read_text(encoding="utf-8"))
    target = assets["targets"][build.name]
    packages = {name.split("/")[0].lower(): (name, item)
                for name, item in target.items() if item.get("type") == "package"}
    pending, visited, payload = ["monotorrent"], set(), {}
    while pending:
        name = pending.pop()
        if name in visited:
            continue
        visited.add(name)
        key, package = packages[name]
        pending.extend(dependency.lower() for dependency in package.get("dependencies", {}))
        for asset in package.get("runtime", {}):
            if asset.endswith("/_._"):
                continue
            filename = Path(asset).name
            payload[filename] = (build / filename).read_bytes()
        for asset in package.get("runtimeTargets", {}):
            payload[asset] = (build / asset).read_bytes()
        package_path = assets["libraries"][key]["path"]
        license_file = next((Path(folder) / package_path / filename
                             for folder in assets["packageFolders"]
                             for filename in ("LICENSE", "LICENSE.md", "LICENSE.txt", "LICENCE", "LICENSE.TXT")
                             if (Path(folder) / package_path / filename).is_file()), None)
        if license_file is None:
            raise FileNotFoundError(f"Missing dependency license for {key}")
        payload[f"licenses/{key.split('/')[0]}.txt"] = license_file.read_bytes()
    return payload


def package(output, release_url, timestamp):
    root = Path(__file__).resolve().parent.parent
    project = ET.parse(root / "src/Jellyfin.Plugin.Siphon/Jellyfin.Plugin.Siphon.csproj").getroot()
    version = project.findtext("PropertyGroup/Version")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        raise ValueError("Plugin version must have four numeric components")
    dll = root / "src/Jellyfin.Plugin.Siphon/bin/Release/net10.0/Jellyfin.Plugin.Siphon.dll"
    if not dll.is_file():
        raise FileNotFoundError("Build the Release plugin before packaging")
    payload = runtime_dependencies(root, dll.parent)
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
        payload.update({dll.name: dll.read_bytes(), "LICENSE": (root / "LICENSE").read_bytes(),
                        "siphon.png": (root / "assets/siphon.png").read_bytes(),
                        "meta.json": json.dumps(metadata, indent=2).encode()})
        for name, data in sorted(payload.items()):
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
        "changelog": "Unreleased source build: add the opt-in bounded MonoTorrent engine, tracker/peer destination validation, safe file selection, lifecycle cleanup, runtime dependencies and third-party licenses. Native playback and per-user integration follow in dependent changes."
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
