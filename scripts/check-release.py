#!/usr/bin/env python3
"""Reject tags that do not match the plugin assembly version."""
import os
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
project = ET.parse(root / "src/Jellyfin.Plugin.Siphon/Jellyfin.Plugin.Siphon.csproj").getroot()
version = project.findtext("PropertyGroup/Version")
expected = "v" + ".".join(version.split(".")[:3])
if os.environ["RELEASE_TAG"] != expected:
    raise SystemExit("Release tag must match plugin version: " + expected)
print("Release version verified: " + expected)
