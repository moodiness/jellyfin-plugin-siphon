#!/usr/bin/env python3
"""Check release/source alignment before publication (does not modify the catalog).

Use --artifacts after packaging for inert PE/ABI, archive and checksum validation.
--require-clean is mandatory for publication and rejects dirty or untracked source.
Existing catalog entries are historical: source 1.6 must not rewrite published 1.5.
"""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET
import zipfile

# Do not create untracked cache files during a clean-checkout release check.
sys.dont_write_bytecode = True

from package import ROOT, descriptor, release_tag, revision, run, verify_archive


def check(tag, artifacts=None, require_clean=False, release_url=None):
    metadata, _ = descriptor()
    expected = release_tag(metadata)
    if not tag:
        raise ValueError("Supply --tag or RELEASE_TAG (for example " + expected + ")")
    if tag != expected:
        raise ValueError(f"Release tag {tag!r} does not match source version {metadata['version']}; expected {expected}")
    if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise ValueError("Release tag must have the form vMajor.Minor.Patch")
    if require_clean:
        dirty = run("git", "status", "--porcelain", "--untracked-files=normal")
        if dirty:
            raise ValueError("Publication requires a clean source checkout; commit source changes and remove untracked inputs first")
        try:
            tagged = run("git", "rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}")
        except ValueError as error:
            raise ValueError(f"Missing local tag {tag}; fetch the actual release tag before publication") from error
        if tagged != revision():
            raise ValueError(f"Tag {tag} does not point to the source checkout HEAD")
    catalog = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    if not isinstance(catalog, list) or not catalog:
        raise ValueError("Root catalog must contain the existing published plugin, not an unpublished placeholder")
    plugin = next((item for item in catalog if item.get("guid") == metadata["guid"]), None)
    if plugin is None or not plugin.get("versions"):
        raise ValueError("Root catalog is missing the existing published plugin history")
    seen = set()
    for entry in plugin["versions"]:
        version = entry.get("version", "")
        if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version) or version in seen:
            raise ValueError(f"Root catalog has invalid or duplicate version: {version!r}")
        seen.add(version)
        if not re.fullmatch(r"[0-9a-fA-F]{32}", entry.get("checksum", "")):
            raise ValueError(f"Root catalog has an invalid archive checksum for {version}")
        if not entry.get("sourceUrl", "").startswith("https://github.com/"):
            raise ValueError(f"Root catalog has an invalid public release URL for {version}")
        if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", entry.get("targetAbi", "")):
            raise ValueError(f"Root catalog has an invalid ABI for {version}")
    if artifacts is not None:
        verify_archive(artifacts, release_url)
    print(f"Release {tag}: source metadata" + (", clean tagged provenance" if require_clean else "")
          + (", binary ABI, archive and checksums" if artifacts is not None else "") + " verified")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", default=os.environ.get("RELEASE_TAG"))
    parser.add_argument("--artifacts", type=Path, help="Verify a packaged artifacts directory against the current build")
    parser.add_argument("--require-clean", action="store_true")
    parser.add_argument("--release-url", help="Expected public release URL prefix")
    args = parser.parse_args()
    try:
        check(args.tag, args.artifacts, args.require_clean, args.release_url)
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError, zipfile.BadZipFile) as error:
        parser.exit(1, f"Release validation failed: {error}\n")


if __name__ == "__main__":
    main()
