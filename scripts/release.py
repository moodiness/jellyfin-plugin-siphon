#!/usr/bin/env python3
"""Publish verified immutable assets, then propose a current-main-only catalog PR.

Requires gh authentication, Git, Python 3.10+ and .NET. Run from a clean release-tag
checkout after package.py. Reruns compare every existing asset byte-for-byte; they
never clobber assets or push to main. --assets-only stops after public verification.
Use the Release workflow for repository-wide serialization; a process lock also
serializes local invocations and is released automatically after interruption.
The workflow's recover_run_id reuses original artifacts without rebuilding. A
pre-provenance public release (including 1.5) is never migrated or replaced by a
new provenance-bearing rebuild. Historical unpublished refs use the explicit
Provenance.targets build import without changing their project or source history.
"""
import argparse
from contextlib import contextmanager
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET
import zipfile

# A release invocation must not dirty the checkout merely by importing its helpers.
sys.dont_write_bytecode = True

from package import ROOT, checksums, json_bytes, revision, run, verify_archive


def gh_api(repository, endpoint, method="GET", payload=None, allow_missing=False, binary=False):
    command = ["gh", "api", "--method", method, f"repos/{repository}/{endpoint}"]
    if binary:
        command += ["-H", "Accept: application/octet-stream"]
    else:
        command += ["-H", "Accept: application/vnd.github+json"]
    if payload is not None:
        command += ["--input", "-"]
    result = subprocess.run(command, input=None if payload is None else json_bytes(payload),
                            capture_output=True, cwd=ROOT, check=False)
    if result.returncode:
        detail = result.stderr.decode("utf-8", errors="replace").strip()
        if allow_missing and "(HTTP 404)" in detail:
            return None
        raise ValueError(f"GitHub {method} {endpoint} failed: {detail}")
    if binary:
        return result.stdout
    return json.loads(result.stdout) if result.stdout else None


@contextmanager
def publication_lock():
    common = Path(run("git", "rev-parse", "--git-common-dir"))
    if not common.is_absolute():
        common = ROOT / common
    with (common / "siphon-publication.lock").open("a+b") as lock:
        lock.seek(0)
        lock.write(b"0")
        lock.flush()
        lock.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            raise ValueError("Another local publication is running; wait and rerun (do not remove the lock file)") from error
        try:
            yield
        finally:
            if os.name == "nt":
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(lock.fileno(), fcntl.LOCK_UN)


def check_remote_tag(repository, tag):
    reference = gh_api(repository, f"git/ref/tags/{tag}")
    obj = reference["object"]
    for _ in range(8):
        if obj["type"] == "commit":
            if obj["sha"] != revision():
                raise ValueError("Remote release tag does not match the verified source commit; refusing publication")
            return
        if obj["type"] != "tag":
            break
        obj = gh_api(repository, f"git/tags/{obj['sha']}")["object"]
    raise ValueError("Remote release tag does not resolve to a commit")


def verify_asset(repository, asset, expected):
    data = gh_api(repository, f"releases/assets/{asset['id']}", binary=True)
    if asset.get("state") != "uploaded" or asset.get("size") != len(expected) or data != expected:
        raise ValueError(f"Immutable asset {asset['name']} differs from this build or is incomplete. "
                         "It has NOT been replaced. Recover the original artifact, or release a new version.")


def verify_public(url, expected):
    # Intentionally unauthenticated: catalog consumers must be able to fetch it.
    last_error = None
    for attempt in range(6):
        try:
            request = Request(url, headers={"User-Agent": "Siphon-release-verifier", "Cache-Control": "no-cache"})
            with urlopen(request, timeout=60) as response:
                data = response.read(len(expected) + 1)
            if data != expected:
                raise ValueError(f"Public download differs from the verified asset: {url}")
            return
        except (HTTPError, URLError, TimeoutError) as error:
            last_error = error
            if attempt < 5:
                time.sleep(min(2 ** attempt, 16))
    raise ValueError(f"Public download is not available; catalog remains unchanged: {url}: {last_error}")


def publish_assets(repository, tag, artifacts, metadata):
    release = gh_api(repository, f"releases/tags/{tag}", allow_missing=True)
    if release is None:
        notes = (ROOT / "RELEASE_NOTES.txt").read_text(encoding="utf-8")
        heading = f"## Siphon {metadata['version']}\n"
        if not notes.startswith(heading) or not notes[len(heading):].strip():
            raise ValueError(f"RELEASE_NOTES.txt must contain detailed notes headed {heading.strip()!r}")
        try:
            release = gh_api(repository, "releases", "POST", {
                "tag_name": tag, "target_commitish": revision(), "name": f"v{metadata['version']}",
                "body": notes, "draft": True, "prerelease": False})
        except ValueError:
            # Another creator may have won; compare assets below, never overwrite.
            release = gh_api(repository, f"releases/tags/{tag}", allow_missing=True)
            if release is None:
                raise
    if release.get("prerelease"):
        raise ValueError("A prerelease cannot be published into the stable catalog")
    archive = artifacts / f"siphon-{metadata['version']}.zip"
    # The candidate manifest stays in workflow recovery artifacts, not on the release.
    # Public checksums cover only public files; the internal artifact checksum is unchanged.
    with tempfile.TemporaryDirectory(prefix="siphon-public-assets-") as temporary:
        sums = Path(temporary) / "SHA256SUMS"
        sums.write_text(checksums(artifacts, [archive.name]), encoding="utf-8")
        for path in (archive, sums):
            name = path.name
            expected = path.read_bytes()
            release = gh_api(repository, f"releases/{release['id']}")
            matching = [asset for asset in release["assets"] if asset["name"] == name]
            if len(matching) > 1:
                raise ValueError(f"Release has duplicate assets named {name}; manual recovery required")
            if matching and matching[0].get("state") == "starter":
                if not release["draft"]:
                    raise ValueError(f"Published release has an incomplete asset {name}; manual recovery is required")
                # GitHub can retain an empty starter record after an interrupted
                # upload. Only this never-published record may be removed on retry.
                gh_api(repository, f"releases/assets/{matching[0]['id']}", "DELETE")
                matching = []
            if not matching:
                result = subprocess.run(["gh", "release", "upload", tag, str(path), "--repo", repository],
                                        capture_output=True, text=True, cwd=ROOT, check=False)
                release = gh_api(repository, f"releases/{release['id']}")
                matching = [asset for asset in release["assets"] if asset["name"] == name]
                if len(matching) != 1:
                    raise ValueError(f"Asset upload failed for {name}; safely rerun after recovery: {result.stderr.strip()}")
            verify_asset(repository, matching[0], expected)
        check_remote_tag(repository, tag)
        if release["draft"]:
            release = gh_api(repository, f"releases/{release['id']}", "PATCH", {"draft": False, "make_latest": "legacy"})
        if release["draft"]:
            raise ValueError("Release remained a draft; catalog has not been changed")
        for path in (archive, sums):
            verify_public(f"https://github.com/{repository}/releases/download/{tag}/{path.name}", path.read_bytes())
    print(f"Published assets verified: https://github.com/{repository}/releases/tag/{tag}")


def merged_catalog(current, candidate):
    if not isinstance(current, list) or not current:
        raise ValueError("Current main has no plugin catalog; refusing to replace it")
    incoming = candidate[0]
    matches = [item for item in current if item.get("guid") == incoming["guid"]]
    if len(matches) != 1:
        raise ValueError("Current main must contain exactly one matching plugin catalog entry")
    plugin = matches[0]
    versions = plugin.get("versions")
    if not isinstance(versions, list) or not versions:
        raise ValueError("Current main plugin has no published release history")
    release = incoming["versions"][0]
    existing = [item for item in versions if item.get("version") == release["version"]]
    if len(existing) > 1:
        raise ValueError("Current main contains duplicate catalog versions")
    if existing:
        if existing[0] != release:
            raise ValueError("Current main already contains different immutable metadata for this version; refusing to replace it")
        return current, False
    for item in versions:
        if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", item.get("version", "")):
            raise ValueError("Current main has an invalid historical version")
    # Keep current-main descriptive fields and every historical version intact.
    versions.append(release)
    versions.sort(key=lambda item: tuple(map(int, item["version"].split("."))), reverse=True)
    return current, True


def propose_catalog(repository, tag, candidate):
    # Fetch main explicitly; never use the historical tag's tree as a main update.
    run("git", "fetch", "origin", "+refs/heads/main:refs/remotes/origin/main")
    main = run("git", "rev-parse", "refs/remotes/origin/main")
    # A proposal belongs to a release, not a moving main commit. Keep an existing
    # proposal intact when main advances; never create a second PR on a retry.
    branch = f"automation/siphon-catalog-{tag}"
    current = json.loads(run("git", "show", f"{main}:manifest.json"))
    catalog, changed = merged_catalog(current, candidate)
    if not changed:
        print("Current main already catalogs these verified assets; nothing to update")
        return
    content = json_bytes(catalog)
    remote_branches = run("git", "ls-remote", "--heads", "origin",
                          f"refs/heads/{branch}", f"refs/heads/{branch}-*")
    # Adopt a proposal from the former main-SHA naming scheme as well. Multiple
    # existing proposals need human reconciliation, not another automatic PR.
    proposals = [line.split()[1].removeprefix("refs/heads/") for line in remote_branches.splitlines()
                 if re.fullmatch(rf"refs/heads/{re.escape(branch)}(?:-[0-9a-f]{{12}})?", line.split()[1])]
    if len(proposals) > 1:
        raise ValueError("Multiple catalog proposals already exist for this release; reconcile them before retrying")
    if proposals:
        branch = proposals[0]
        run("git", "fetch", "origin", f"refs/heads/{branch}")
        remote = run("git", "rev-parse", "FETCH_HEAD")
        base = run("git", "merge-base", main, remote)
        changed_files = run("git", "diff", "--name-only", base, remote)
        base_catalog = json.loads(run("git", "show", f"{base}:manifest.json"))
        expected, adds_version = merged_catalog(base_catalog, candidate)
        existing = json.loads(run("git", "show", f"{remote}:manifest.json"))
        if not adds_version or changed_files != "manifest.json" or existing != expected:
            raise ValueError(f"Existing catalog branch {branch} differs; it has not been overwritten")
    else:
        with tempfile.TemporaryDirectory(prefix="siphon-catalog-") as temporary:
            worktree = Path(temporary) / "main"
            run("git", "worktree", "add", "--detach", str(worktree), main)
            try:
                (worktree / "manifest.json").write_bytes(content)
                run("git", "add", "--", "manifest.json", cwd=worktree)
                run("git", "-c", "user.name=github-actions[bot]", "-c",
                    "user.email=41898282+github-actions[bot]@users.noreply.github.com",
                    "commit", "-m", f"chore(release): catalog verified Siphon {tag}", cwd=worktree)
                # Only this dedicated branch is written. No force push and no main push.
                run("git", "push", "origin", f"HEAD:refs/heads/{branch}", cwd=worktree)
            finally:
                run("git", "worktree", "remove", str(worktree))
    prs = json.loads(run("gh", "pr", "list", "--repo", repository, "--head", branch,
                         "--base", "main", "--state", "open", "--json", "url"))
    if prs:
        print("Catalog pull request: " + prs[0]["url"])
        return
    try:
        url = run("gh", "pr", "create", "--repo", repository, "--head", branch, "--base", "main",
                  "--title", f"chore(release): catalog verified Siphon {tag}",
                  "--body", "Adds only the publicly downloaded and byte-verified release metadata. "
                  "The branch is based on current main; existing published versions and source files are preserved.")
    except ValueError as error:
        raise ValueError(f"Assets are published and verified; catalog branch {branch} is ready. "
                         f"Token/repository policy prevented PR creation. Open https://github.com/{repository}/compare/main...{branch} "
                         "or enable workflow PR creation, then rerun. Main has not been changed.") from error
    print("Catalog pull request: " + url)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "moodiness/jellyfin-plugin-siphon"))
    parser.add_argument("--artifacts", type=Path, default=ROOT / "artifacts")
    parser.add_argument("--assets-only", action="store_true", help="Publish/verify assets without proposing a catalog PR")
    args = parser.parse_args()
    try:
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repo):
            raise ValueError("--repo must be owner/repository")
        if not re.fullmatch(r"v\d+\.\d+\.\d+", args.tag):
            raise ValueError("--tag must have the form vMajor.Minor.Patch")
        spec = importlib.util.spec_from_file_location("check_release", Path(__file__).with_name("check-release.py"))
        checker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(checker)
        release_url = f"https://github.com/{args.repo}/releases/download/{args.tag}"
        with publication_lock():
            checker.check(args.tag, require_clean=True)
            metadata, candidate = verify_archive(args.artifacts, release_url)
            origin = run("git", "remote", "get-url", "origin")
            allowed = (f"https://github.com/{args.repo}", f"git@github.com:{args.repo}")
            if origin.removesuffix(".git").rstrip("/") not in allowed:
                raise ValueError("origin must match --repo before proposing a catalog branch")
            check_remote_tag(args.repo, args.tag)
            publish_assets(args.repo, args.tag, args.artifacts.resolve(), metadata)
            if not args.assets_only:
                propose_catalog(args.repo, args.tag, candidate)
    except (OSError, ValueError, KeyError, TypeError, AttributeError, IndexError, ET.ParseError, zipfile.BadZipFile) as error:
        parser.exit(1, f"Release publication stopped safely: {error}\n")


if __name__ == "__main__":
    main()
