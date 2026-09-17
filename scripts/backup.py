#!/usr/bin/env python3
"""Offline, full-tree Jellyfin/Siphon snapshots. Python 3.11+, local POSIX filesystems."""
import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import signal
import sqlite3
import stat
import sys
import tarfile
import tempfile
import unicodedata
import uuid
import xml.etree.ElementTree as ET

if os.name == "posix":
    import fcntl

FORMAT = "siphon-offline-backup"
FORMAT_VERSION = 1
PLUGIN_ID = "b2df1c14-4b7e-4e7b-9a95-8f9ad8d2b0c1"
BLOCK = 1024 * 1024
METADATA_LIMIT = 64 * 1024 * 1024
SQLITE_HEADER = b"SQLite format 3\x00"
SIDECARS = ("-wal", "-shm", "-journal")
MANIFEST = "siphon-backup.json"
CHECKSUM = "siphon-backup.sha256"
VERSION_PATTERN = r"[0-9]{1,5}(?:\.[0-9]{1,5}){2,3}"


class BackupError(Exception):
    """A controlled error whose message never contains stored data or paths."""


def require(condition, message):
    if not condition:
        raise BackupError(message)


def canonical_json(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n").encode("ascii")


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "A JSON document contains duplicate keys.")
        value[key] = item
    return value


def parse_json(data):
    require(len(data) <= METADATA_LIMIT, "A metadata document exceeds the size limit.")
    return json.loads(data, object_pairs_hook=unique_object)


def safe_name(value):
    require(isinstance(value, str) and 0 < len(value.encode("utf-8")) <= 255,
            "An archive path has an invalid length.")
    require(unicodedata.normalize("NFC", value) == value and "\\" not in value and ":" not in value,
            "An archive path is not portable or canonical.")
    parts = value.split("/")
    require(len(parts) <= 64 and all(part not in ("", ".", "..") for part in parts),
            "An archive path contains an unsafe component.")
    for part in parts:
        require(not any(ord(char) < 32 or ord(char) == 127 for char in part)
                and part == part.strip() and not part.endswith("."), "An archive path contains unsafe characters.")
        require(part.split(".")[0].upper() not in
                {"CON", "PRN", "AUX", "NUL", *[f"COM{i}" for i in range(1, 10)], *[f"LPT{i}" for i in range(1, 10)]},
                "An archive path contains a reserved component.")
    return value


def absolute_path(value):
    path = Path(os.path.abspath(value))
    require(path != Path(path.anchor), "A filesystem root cannot be used as a snapshot path.")
    return path


def open_directory(path):
    """Walk every ancestor with O_NOFOLLOW; never resolve a symlink on the caller's behalf."""
    path = absolute_path(path)
    descriptor = os.open(path.anchor, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        for component in path.parts[1:]:
            child = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


@contextmanager
def relative_file(root, name, flags=os.O_RDONLY):
    parts = PurePosixPath(name).parts
    parent = os.dup(root)
    try:
        for part in parts[:-1]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
            os.close(parent)
            parent = child
        descriptor = os.open(parts[-1], flags | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=parent)
        try:
            yield descriptor
        finally:
            os.close(descriptor)
    finally:
        os.close(parent)


def fingerprint(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_size, info.st_mtime_ns, info.st_ctime_ns, info.st_nlink)


def owners(arguments):
    result = set()
    for value in arguments.owner:
        require(re.fullmatch(r"[0-9]{1,10}:[0-9]{1,10}", value) is not None, "Owner must be a numeric UID:GID pair.")
        pair = tuple(map(int, value.split(":")))
        require(max(pair) < 2**32 - 1, "An owner identifier is outside the supported range.")
        result.add(pair)
    return result


def check_info(info, approved):
    require(stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode),
            "Links, sockets, devices and other special entries are not supported.")
    require((info.st_uid, info.st_gid) in approved, "A filesystem entry has an unapproved owner.")
    require(not info.st_mode & 0o7000, "Special permission bits are not supported.")
    require(not stat.S_ISREG(info.st_mode) or info.st_nlink == 1, "Hard-linked files are not supported.")


def check_sidecar(name):
    require(not name.lower().endswith(SIDECARS),
            "A SQLite sidecar remains. Stop Jellyfin cleanly; never delete WAL/journal files to bypass this check.")


class SourceTrees:
    """Retain SQLite lock descriptors: closing any other fd for that inode would drop POSIX locks."""
    def __init__(self, roots, approved, limits):
        self.roots = roots
        self.approved = approved
        self.limits = limits
        self.descriptors = {}
        self.databases = {}
        self.entries = []
        self.inodes = set()
        self.total = 0

    def __enter__(self):
        try:
            for name, path in self.roots.items():
                self.descriptors[name] = open_directory(path)
                self.walk(name, self.descriptors[name], "")
            self.entries.sort(key=lambda item: item["path"])
            validate_entries(self.entries, list(self.roots), self.approved, self.limits, source=True)
            self.lock_databases()
            return self
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, *_):
        for descriptor in self.databases.values():
            os.close(descriptor)
        for descriptor in self.descriptors.values():
            os.close(descriptor)
        self.databases.clear()
        self.descriptors.clear()

    def walk(self, root_name, descriptor, relative):
        path = root_name + ("/" + relative if relative else "")
        info = os.fstat(descriptor)
        self.add(path, info, "directory")
        with os.scandir(descriptor) as children:
            names = sorted(child.name for child in children)
        for name in names:
            child_relative = relative + "/" + name if relative else name
            safe_name(root_name + "/" + child_relative)
            check_sidecar(name)
            child_info = os.stat(name, dir_fd=descriptor, follow_symlinks=False)
            check_info(child_info, self.approved)
            if stat.S_ISDIR(child_info.st_mode):
                child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=descriptor)
                try:
                    require(fingerprint(os.fstat(child)) == fingerprint(child_info), "A source directory changed during inspection.")
                    self.walk(root_name, child, child_relative)
                finally:
                    os.close(child)
            else:
                self.add(root_name + "/" + child_relative, child_info, "file")
        require(fingerprint(os.fstat(descriptor)) == fingerprint(info), "A source directory changed during inspection.")

    def add(self, name, info, kind):
        safe_name(name)
        check_info(info, self.approved)
        inode = (info.st_dev, info.st_ino)
        require(inode not in self.inodes, "Source roots overlap or contain aliased filesystem entries.")
        self.inodes.add(inode)
        size = info.st_size if kind == "file" else 0
        self.total += size
        require(size <= self.limits.max_file_bytes and self.total <= self.limits.max_bytes,
                "Source data exceeds the configured size limit.")
        require(len(self.entries) < self.limits.max_members, "Source data exceeds the configured member limit.")
        self.entries.append({"path": name, "kind": kind, "size": size, "uid": info.st_uid, "gid": info.st_gid,
                             "mode": stat.S_IMODE(info.st_mode), "mtime_ns": info.st_mtime_ns,
                             "sha256": None, "sqlite": False, "_fingerprint": fingerprint(info)})

    def lock_databases(self):
        for entry in self.entries:
            if entry["kind"] != "file":
                continue
            root, relative = entry["path"].split("/", 1)
            with relative_file(self.descriptors[root], relative) as descriptor:
                require(fingerprint(os.fstat(descriptor)) == entry["_fingerprint"], "A source file changed during inspection.")
                is_database = os.pread(descriptor, 16, 0) == SQLITE_HEADER
            require(is_database or not relative.lower().endswith((".db", ".sqlite", ".sqlite3")),
                    "A database-named file is not a supported SQLite database.")
            if not is_database:
                continue
            # Open once, retain until every copy and source recheck is complete, and never dup/close it.
            with relative_file(self.descriptors[root], relative, os.O_RDWR) as temporary:
                descriptor = os.dup(temporary)
            try:
                require(fingerprint(os.fstat(descriptor)) == entry["_fingerprint"], "A source database changed during inspection.")
                # SQLite's pending, reserved and shared POSIX lock bytes. This also refuses active readers.
                fcntl.lockf(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB, 512, 0x40000000, os.SEEK_SET)
                self.databases[entry["path"]] = descriptor
                entry["sqlite"] = True
            except BaseException:
                os.close(descriptor)
                raise BackupError("A SQLite database is busy or cannot be exclusively locked; keep the instance stopped.") from None
        require(self.databases, "No native SQLite databases were found.")

    @contextmanager
    def file(self, entry):
        if entry["sqlite"]:
            yield self.databases[entry["path"]]
        else:
            root, relative = entry["path"].split("/", 1)
            with relative_file(self.descriptors[root], relative) as descriptor:
                yield descriptor

    def copy(self, entry, output=None):
        digest = hashlib.sha256()
        with self.file(entry) as descriptor:
            require(fingerprint(os.fstat(descriptor)) == entry["_fingerprint"], "A source file changed during backup.")
            offset = 0
            while offset < entry["size"]:
                data = os.pread(descriptor, min(BLOCK, entry["size"] - offset), offset)
                require(data, "A source file was truncated during backup.")
                digest.update(data)
                if output is not None:
                    output.write(data)
                offset += len(data)
            require(fingerprint(os.fstat(descriptor)) == entry["_fingerprint"], "A source file changed during backup.")
        return digest.hexdigest()

    def recheck(self):
        observed = {}

        def walk(root_name, descriptor, relative):
            name = root_name + ("/" + relative if relative else "")
            observed[name] = fingerprint(os.fstat(descriptor))
            with os.scandir(descriptor) as children:
                names = [child.name for child in children]
            for child_name in names:
                child_relative = relative + "/" + child_name if relative else child_name
                info = os.stat(child_name, dir_fd=descriptor, follow_symlinks=False)
                check_sidecar(child_name)
                if stat.S_ISDIR(info.st_mode):
                    child = os.open(child_name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=descriptor)
                    try:
                        walk(root_name, child, child_relative)
                    finally:
                        os.close(child)
                else:
                    observed[root_name + "/" + child_relative] = fingerprint(info)

        for name, descriptor in self.descriptors.items():
            with_directory = open_directory(self.roots[name])
            try:
                require(os.fstat(with_directory).st_ino == os.fstat(descriptor).st_ino
                        and os.fstat(with_directory).st_dev == os.fstat(descriptor).st_dev,
                        "A source root was replaced during backup.")
            finally:
                os.close(with_directory)
            walk(name, descriptor, "")
        require(observed == {entry["path"]: entry["_fingerprint"] for entry in self.entries},
                "Source contents changed during backup; no snapshot was published.")


def validate_entries(entries, roots, approved, limits, source=False):
    require(isinstance(entries, list) and 0 < len(entries) <= limits.max_members, "Invalid archive member count.")
    known, folded, total = {}, set(), 0
    for entry in entries:
        require(isinstance(entry, dict), "Invalid archive entry metadata.")
        keys = {"path", "kind", "size", "uid", "gid", "mode", "mtime_ns", "sha256", "sqlite"}
        require(set(entry) == keys | ({"_fingerprint"} if source else set()), "Unsupported archive entry metadata.")
        name = safe_name(entry["path"])
        require(name.casefold() not in folded, "Duplicate or case-colliding archive members are not supported.")
        folded.add(name.casefold())
        check_sidecar(name)
        require(entry["kind"] in ("directory", "file") and type(entry["sqlite"]) is bool, "Invalid archive entry type.")
        for field in ("uid", "gid", "mode", "mtime_ns", "size"):
            require(type(entry[field]) is int and entry[field] >= 0, "Invalid numeric archive metadata.")
        require(entry["mode"] <= 0o777 and entry["mtime_ns"] < 2**63
                and (entry["uid"], entry["gid"]) in approved, "Archive permissions or ownership are not approved.")
        require(entry["size"] <= limits.max_file_bytes, "An archive member exceeds the configured size limit.")
        if "/" in name:
            parent = name.rsplit("/", 1)[0]
            require(parent in known and known[parent]["kind"] == "directory", "An archive member has no declared directory parent.")
        else:
            require(name in roots and entry["kind"] == "directory", "An archive contains an undeclared root.")
        if entry["kind"] == "directory":
            require(entry["size"] == 0 and entry["sha256"] is None and not entry["sqlite"], "Invalid directory metadata.")
        elif not source:
            require(isinstance(entry["sha256"], str) and re.fullmatch(r"[0-9a-f]{64}", entry["sha256"]),
                    "Missing or invalid file checksum.")
        total += entry["size"]
        require(total <= limits.max_bytes, "An archive exceeds the configured total size limit.")
        known[name] = entry
    require(set(name for name in known if "/" not in name) == set(roots), "An archive root is missing.")
    return known


def source_roots(arguments):
    program = absolute_path(arguments.program_data)
    config = absolute_path(arguments.config_dir) if arguments.config_dir else program / "config"
    roots = {"program": program}
    if config == program or program in config.parents:
        config_member = "program" + ("/" + config.relative_to(program).as_posix() if config != program else "")
    else:
        require(config not in program.parents, "Configuration cannot contain the program-data root.")
        roots["config"] = config
        config_member = "config"
    for value in arguments.extra_root:
        name, separator, path = value.partition("=")
        require(separator and re.fullmatch(r"[a-z][a-z0-9-]{0,31}", name) and name not in roots
                and name not in ("program", "config"), "Extra root must have a unique lowercase NAME=PATH.")
        roots[name] = absolute_path(path)
    for name, path in roots.items():
        require(not any(other != name and (path == candidate or path in candidate.parents or candidate in path.parents)
                        for other, candidate in roots.items()), "Snapshot roots must not overlap.")
    return roots, config_member


def make_header(name, size=0, entry=None):
    info = tarfile.TarInfo(name + ("/" if entry and entry["kind"] == "directory" else ""))
    info.size = size
    info.mode = entry["mode"] if entry else 0o600
    info.uid = entry["uid"] if entry else 0
    info.gid = entry["gid"] if entry else 0
    info.mtime = entry["mtime_ns"] // 1_000_000_000 if entry else 0
    info.type = tarfile.DIRTYPE if entry and entry["kind"] == "directory" else tarfile.REGTYPE
    try:
        # Canonical, bounded PAX headers retain long UTF-8 paths and files larger than USTAR's 8 GiB limit.
        # Validation compares these exact bytes; arbitrary extension records are never parsed or trusted.
        header = info.tobuf(tarfile.PAX_FORMAT, "utf-8", "strict")
        require(len(header) <= 2048, "An archive member needs excessive extension metadata.")
        return header
    except (ValueError, OverflowError, UnicodeError):
        raise BackupError("A path, owner or timestamp cannot be represented in the canonical archive format.") from None


def write_bytes(output, name, data):
    output.write(make_header(name, len(data)))
    output.write(data)
    output.write(b"\x00" * (-len(data) % 512))


def file_digest(descriptor, size):
    digest, offset = hashlib.sha256(), 0
    while offset < size:
        data = os.pread(descriptor, min(BLOCK, size - offset), offset)
        require(data, "The archive is truncated.")
        digest.update(data)
        offset += len(data)
    return digest.hexdigest()


def read_exact(descriptor, size, offset):
    data = bytearray()
    while len(data) < size:
        chunk = os.pread(descriptor, min(BLOCK, size - len(data)), offset + len(data))
        require(chunk, "The archive is truncated.")
        data.extend(chunk)
    return bytes(data)


def read_member(descriptor, offset, expected_header, size):
    header_size = len(expected_header)
    require(read_exact(descriptor, header_size, offset) == expected_header,
            "An archive header is invalid, unsafe or inconsistent with its manifest.")
    padding = -size % 512
    if padding:
        require(read_exact(descriptor, padding, offset + header_size + size) == b"\x00" * padding, "Invalid archive member padding.")
    return offset + header_size, offset + header_size + size + padding


def read_control(descriptor, offset, name, maximum):
    header = read_exact(descriptor, 512, offset)
    info = tarfile.TarInfo.frombuf(header, "utf-8", "strict")
    require(type(info.size) is int and 0 <= info.size <= maximum, "Archive metadata exceeds its size limit.")
    start, end = read_member(descriptor, offset, make_header(name, info.size), info.size)
    return read_exact(descriptor, info.size, start), end


def parse_xml(data, expected):
    require(len(data) <= METADATA_LIMIT, "Configuration XML exceeds its size limit.")
    text = data.decode("utf-8-sig")
    require("<!DOCTYPE" not in text.upper() and "<!ENTITY" not in text.upper(),
            "Configuration XML contains unsupported declarations.")
    require(ET.fromstring(text).tag == expected, "A required configuration document has an unexpected root.")


def required_members(manifest, entries):
    config = safe_name(manifest["configuration"])
    require(config in entries and entries[config]["kind"] == "directory", "The configuration root is missing.")
    paths = {"system": config + "/system.xml", "identity": "program/data/device.txt",
             "state": "program/data/siphon/state.json", "key": "program/data/siphon/signing.key",
             "plugin_config": "program/plugins/configurations/Jellyfin.Plugin.Siphon.xml",
             "native": "program/data/jellyfin.db"}
    for path in paths.values():
        require(path in entries and entries[path]["kind"] == "file", "The snapshot is incomplete: required native/Siphon state is missing.")
    require(entries[paths["native"]]["sqlite"], "The native Jellyfin database is not SQLite.")
    require(entries[paths["key"]]["size"] == 32 and entries[paths["key"]]["mode"] == 0o600,
            "The original 32-byte Siphon signing key with private permissions is required.")
    require(0 < entries[paths["identity"]]["size"] <= 4096, "The native server identity is missing or invalid.")
    dlls = [name for name in entries if name.startswith("program/plugins/") and name.endswith("/Jellyfin.Plugin.Siphon.dll")]
    require(len(dlls) == 1 and entries[dlls[0]]["size"] > 0, "Exactly one installed Siphon plugin binary is required.")
    paths["plugin_meta"] = dlls[0].rsplit("/", 1)[0] + "/meta.json"
    require(paths["plugin_meta"] in entries and entries[paths["plugin_meta"]]["kind"] == "file", "Installed Siphon version metadata is missing.")
    if "program/data/siphon/downloads" in entries:
        require("program/data/siphon/download-queue.json" in entries, "Private downloads are missing their ownership ledger.")
    return paths


def inspect_required(manifest, entries, read_content):
    paths = required_members(manifest, entries)
    parse_xml(read_content(paths["system"]), "ServerConfiguration")
    parse_xml(read_content(paths["plugin_config"]), "PluginConfiguration")
    plugin = parse_json(read_content(paths["plugin_meta"]))
    require(isinstance(plugin, dict) and plugin.get("guid", "").lower() == PLUGIN_ID
            and plugin.get("version") == manifest["siphon_version"], "The installed plugin identity/version does not match the snapshot.")
    state = parse_json(read_content(paths["state"]))
    require(isinstance(state, dict) and type(state.get("Version")) is int and state["Version"] in (1, 2)
            and isinstance(state.get("Items"), list), "Siphon state is corrupt or its schema is unsupported.")
    if state["Items"]:
        require("program/data/siphon/catalogs" in entries
                and entries["program/data/siphon/catalogs"]["kind"] == "directory",
                "Managed catalog backing directories are missing.")
    require(hashlib.sha256(read_content(paths["identity"])).hexdigest() == manifest["instance_sha256"],
            "The snapshot's native server identity is inconsistent.")
    return state["Version"]


def sqlite_check(path):
    connection = sqlite3.connect(path.as_uri() + "?mode=ro&immutable=1", uri=True, timeout=0)
    try:
        connection.execute("PRAGMA trusted_schema=OFF")
        connection.execute("PRAGMA query_only=ON")
        check = connection.execute("PRAGMA quick_check")
        require(check.fetchone() == ("ok",) and check.fetchone() is None, "A copied SQLite database failed its integrity check.")
    finally:
        connection.close()


def validate_archive(descriptor, arguments, scratch):
    before = os.fstat(descriptor)
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1, "The archive must be a regular, non-hard-linked file.")
    maximum = arguments.max_bytes + METADATA_LIMIT + arguments.max_members * 2560 + 16384
    require(2048 <= before.st_size <= maximum and before.st_size % 512 == 0, "Invalid or excessive archive size.")
    raw, offset = read_control(descriptor, 0, MANIFEST, METADATA_LIMIT)
    checksum, offset = read_control(descriptor, offset, CHECKSUM, 65)
    require(checksum == (hashlib.sha256(raw).hexdigest() + "\n").encode("ascii"), "The snapshot metadata checksum does not match.")
    manifest = parse_json(raw)
    require(isinstance(manifest, dict) and set(manifest) == {
        "format", "format_version", "created_utc", "jellyfin_version", "siphon_version", "instance_sha256",
        "configuration", "roots", "entries"}, "Unsupported snapshot metadata.")
    require(manifest["format"] == FORMAT and type(manifest["format_version"]) is int
            and manifest["format_version"] == FORMAT_VERSION, "Unsupported snapshot format/version.")
    require(isinstance(manifest["created_utc"], str) and re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\+00:00", manifest["created_utc"]),
            "Invalid snapshot timestamp.")
    require(manifest["jellyfin_version"] == arguments.jellyfin_version and manifest["siphon_version"] == arguments.siphon_version,
            "Snapshot versions differ from the explicitly selected Jellyfin/Siphon versions; no migration is performed.")
    require(isinstance(manifest["instance_sha256"], str) and re.fullmatch(r"[0-9a-f]{64}", manifest["instance_sha256"]),
            "Invalid native identity fingerprint.")
    roots = manifest["roots"]
    require(isinstance(roots, list) and 1 <= len(roots) <= 32 and all(isinstance(name, str)
            and re.fullmatch(r"[a-z][a-z0-9-]{0,31}", name) for name in roots)
            and len(set(roots)) == len(roots) and "program" in roots, "Invalid snapshot roots.")
    entries = validate_entries(manifest["entries"], roots, owners(arguments), arguments)
    required_members(manifest, entries)
    offsets = {}
    for entry in manifest["entries"]:
        name, size = entry["path"], entry["size"]
        start, offset = read_member(descriptor, offset, make_header("payload/" + name, size, entry), size)
        require(offset <= before.st_size - 1024, "An archive member exceeds the archive boundary.")
        offsets[name] = start
        if entry["kind"] == "directory":
            continue
        digest, consumed, prefix = hashlib.sha256(), 0, b""
        database = scratch / "database.sqlite"
        with open(database, "xb") if entry["sqlite"] else _no_output() as output:
            while consumed < size:
                data = read_exact(descriptor, min(BLOCK, size - consumed), start + consumed)
                if consumed == 0:
                    prefix = data[:16]
                digest.update(data)
                if output is not None:
                    output.write(data)
                consumed += len(data)
        require(digest.hexdigest() == entry["sha256"], "An archive file checksum does not match.")
        require((prefix == SQLITE_HEADER) == entry["sqlite"], "SQLite content is inconsistent with the snapshot metadata.")
        require(entry["sqlite"] or not name.lower().endswith((".db", ".sqlite", ".sqlite3")),
                "An archive contains an unsupported database file.")
        if entry["sqlite"]:
            sqlite_check(database)
            database.unlink()
    require(1024 <= before.st_size - offset <= 10240, "The archive has missing terminators or unexpected trailing data.")
    require(read_exact(descriptor, before.st_size - offset, offset) == b"\x00" * (before.st_size - offset),
            "The archive has unexpected trailing members or data.")

    def content(name):
        require(entries[name]["size"] <= METADATA_LIMIT, "A required metadata file exceeds the size limit.")
        return read_exact(descriptor, entries[name]["size"], offsets[name])

    state_version = inspect_required(manifest, entries, content)
    digest = file_digest(descriptor, before.st_size)
    require(not arguments.sha256 or digest == arguments.sha256.lower(), "The archive does not match the expected external SHA-256.")
    require(fingerprint(os.fstat(descriptor)) == fingerprint(before), "The archive changed during validation.")
    return manifest, offsets, digest, state_version, fingerprint(before)


@contextmanager
def _no_output():
    yield None


def source_content(trees, entries, name):
    entry = entries[name]
    require(entry["size"] <= METADATA_LIMIT, "A required metadata file exceeds the size limit.")
    with trees.file(entry) as descriptor:
        return read_exact(descriptor, entry["size"], 0)


def create(arguments):
    roots, config = source_roots(arguments)
    output = absolute_path(arguments.output)
    require(not any(output == root or root in output.parents for root in roots.values()), "The archive must be outside every source tree.")
    parent = open_directory(output.parent)
    temporary = ".siphon-backup-" + uuid.uuid4().hex + ".tmp"
    created = False
    try:
        require(not entry_exists(parent, output.name), "An existing archive will never be overwritten.")
        with SourceTrees(roots, owners(arguments), arguments) as trees:
            for entry in trees.entries:
                if entry["kind"] == "file":
                    entry["sha256"] = trees.copy(entry)
            entries = {entry["path"]: entry for entry in trees.entries}
            identity = "program/data/device.txt"
            require(identity in entries and entries[identity]["size"] <= 4096, "The native server identity is missing or invalid.")
            manifest = {"format": FORMAT, "format_version": FORMAT_VERSION,
                        "created_utc": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
                        "jellyfin_version": arguments.jellyfin_version, "siphon_version": arguments.siphon_version,
                        "instance_sha256": hashlib.sha256(source_content(trees, entries, identity)).hexdigest(),
                        "configuration": config, "roots": sorted(roots),
                        "entries": [{key: value for key, value in entry.items() if not key.startswith("_")} for entry in trees.entries]}
            inspect_required(manifest, entries, lambda name: source_content(trees, entries, name))
            raw = canonical_json(manifest)
            require(len(raw) <= METADATA_LIMIT, "The snapshot manifest exceeds its size limit.")
            for entry in trees.entries:
                make_header("payload/" + entry["path"], entry["size"], entry)
            descriptor = os.open(temporary, os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=parent)
            created = True
            with os.fdopen(descriptor, "w+b") as archive:
                write_bytes(archive, MANIFEST, raw)
                write_bytes(archive, CHECKSUM, (hashlib.sha256(raw).hexdigest() + "\n").encode("ascii"))
                for entry in trees.entries:
                    archive.write(make_header("payload/" + entry["path"], entry["size"], entry))
                    if entry["kind"] == "file":
                        require(trees.copy(entry, archive) == entry["sha256"], "A source file changed while the snapshot was written.")
                        archive.write(b"\x00" * (-entry["size"] % 512))
                archive.write(b"\x00" * 1024)
                archive.flush()
                os.fsync(archive.fileno())
                trees.recheck()
                with tempfile.TemporaryDirectory(prefix="siphon-verify-") as scratch:
                    manifest, _, digest, state_version, _ = validate_archive(archive.fileno(), arguments, Path(scratch))
                trees.recheck()
                # link is an atomic no-replace publication, unlike rename of an existing output file.
                os.link(temporary, output.name, src_dir_fd=parent, dst_dir_fd=parent, follow_symlinks=False)
            os.unlink(temporary, dir_fd=parent)
            created = False
            os.fsync(parent)
            summary("created", manifest, digest, state_version)
    finally:
        if created:
            os.unlink(temporary, dir_fd=parent)
        os.close(parent)


def entry_exists(parent, name):
    try:
        os.stat(name, dir_fd=parent, follow_symlinks=False)
        return True
    except FileNotFoundError:
        return False


def empty_destination(parent, name):
    if not entry_exists(parent, name):
        return None
    info = os.stat(name, dir_fd=parent, follow_symlinks=False)
    require(stat.S_ISDIR(info.st_mode), "The restore destination must be absent or an empty real directory.")
    descriptor = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
    try:
        with os.scandir(descriptor) as children:
            require(next(children, None) is None, "A populated restore destination is never overwritten or merged.")
        require(fingerprint(os.fstat(descriptor)) == fingerprint(info), "The restore destination changed during inspection.")
    finally:
        os.close(descriptor)
    return fingerprint(info)


def set_attributes(path, entry):
    info = os.stat(path, follow_symlinks=False)
    if (info.st_uid, info.st_gid) != (entry["uid"], entry["gid"]):
        os.chown(path, entry["uid"], entry["gid"], follow_symlinks=False)
    os.chmod(path, entry["mode"], follow_symlinks=False)
    os.utime(path, ns=(entry["mtime_ns"], entry["mtime_ns"]), follow_symlinks=False)


def restore(descriptor, arguments, manifest, offsets, archive_fingerprint):
    destination = absolute_path(arguments.destination)
    parent = open_directory(destination.parent)
    stage_name = ".siphon-restore-" + uuid.uuid4().hex
    stage = destination.parent / stage_name
    created = False
    try:
        before = empty_destination(parent, destination.name)
        require(os.geteuid() == 0 or all(entry["uid"] == os.geteuid()
                and entry["gid"] in {os.getegid(), *os.getgroups()} for entry in manifest["entries"]),
                "Preserving the approved ownership requires root or the original user/group memberships.")
        os.mkdir(stage_name, 0o700, dir_fd=parent)
        created = True
        for entry in manifest["entries"]:
            path = stage.joinpath(*entry["path"].split("/"))
            if entry["kind"] == "directory":
                path.mkdir(mode=0o700)
                continue
            digest, consumed = hashlib.sha256(), 0
            with open(path, "xb") as output:
                os.chmod(path, 0o600)
                while consumed < entry["size"]:
                    data = read_exact(descriptor, min(BLOCK, entry["size"] - consumed), offsets[entry["path"]] + consumed)
                    digest.update(data)
                    output.write(data)
                    consumed += len(data)
                output.flush()
                os.fsync(output.fileno())
            require(digest.hexdigest() == entry["sha256"], "The archive changed while restoring a file.")
        require(fingerprint(os.fstat(descriptor)) == archive_fingerprint, "The archive changed during restore.")
        for entry in reversed(manifest["entries"]):
            path = stage.joinpath(*entry["path"].split("/"))
            if entry["kind"] == "directory":
                directory = open_directory(path)
                try:
                    set_attributes(path, entry)
                    os.fsync(directory)
                finally:
                    os.close(directory)
            else:
                set_attributes(path, entry)
        stage_descriptor = open_directory(stage)
        try:
            os.fsync(stage_descriptor)
        finally:
            os.close(stage_descriptor)
        require(empty_destination(parent, destination.name) == before, "The restore destination changed; nothing was published.")
        # POSIX rename cannot replace a populated directory, including one populated after the check.
        os.rename(stage_name, destination.name, src_dir_fd=parent, dst_dir_fd=parent)
        created = False
        os.fsync(parent)
    finally:
        if created:
            # Only this invocation's unpredictable private staging tree is removed.
            # Original permission restoration can leave it read-only; make owned directories removable first.
            for directory, _, _ in os.walk(stage, topdown=True, followlinks=False):
                os.chmod(directory, 0o700)
            shutil.rmtree(stage)
        os.close(parent)


def verify_restored(arguments, manifest):
    destination = absolute_path(arguments.destination)
    root = open_directory(destination)
    try:
        with os.scandir(root) as children:
            require({child.name for child in children} == set(manifest["roots"]), "Restored roots differ from the snapshot.")
    finally:
        os.close(root)
    roots = {name: destination / name for name in manifest["roots"]}
    with SourceTrees(roots, owners(arguments), arguments) as trees:
        require(len(trees.entries) == len(manifest["entries"]), "Restored contents differ from the snapshot.")
        for current, expected in zip(trees.entries, sorted(manifest["entries"], key=lambda entry: entry["path"])):
            require(all(current[field] == expected[field] for field in ("path", "kind", "size", "uid", "gid", "mode", "sqlite")),
                    "Restored metadata differs from the snapshot.")
            if current["kind"] == "file":
                require(trees.copy(current) == expected["sha256"], "Restored file contents differ from the snapshot.")
        trees.recheck()


def summary(action, manifest, digest, state_version):
    print(json.dumps({"result": action, "format_version": FORMAT_VERSION, "created_utc": manifest["created_utc"],
                      "jellyfin_version": manifest["jellyfin_version"], "siphon_version": manifest["siphon_version"],
                      "siphon_state_version": state_version, "roots": len(manifest["roots"]),
                      "files": sum(entry["kind"] == "file" for entry in manifest["entries"]),
                      "bytes": sum(entry["size"] for entry in manifest["entries"]), "archive_sha256": digest,
                      "offline_boundary": "Operator stop acknowledgement plus local SQLite locks/sidecar checks; not process absence proof."},
                     sort_keys=True))


def process_archive(arguments):
    archive = absolute_path(arguments.archive)
    parent = open_directory(archive.parent)
    try:
        with relative_file(parent, archive.name) as descriptor:
            with tempfile.TemporaryDirectory(prefix="siphon-verify-") as scratch:
                manifest, offsets, digest, state_version, original = validate_archive(descriptor, arguments, Path(scratch))
            if arguments.command == "plan":
                destination = absolute_path(arguments.destination)
                target_parent = open_directory(destination.parent)
                try:
                    empty_destination(target_parent, destination.name)
                finally:
                    os.close(target_parent)
            elif arguments.command == "restore":
                restore(descriptor, arguments, manifest, offsets, original)
            elif arguments.command == "verify-restored":
                verify_restored(arguments, manifest)
            summary({"validate": "valid", "plan": "ready-for-empty-destination", "restore": "restored",
                     "verify-restored": "restored-contents-match"}[arguments.command], manifest, digest, state_version)
    finally:
        os.close(parent)


def parser():
    result = argparse.ArgumentParser(description=__doc__, epilog=(
        "Stop Jellyfin and disable automatic restart first. --ack-stopped is an operator assertion, not a process detector. "
        "SQLite byte locks and zero-sidecar checks require local POSIX filesystem semantics. Never remove WAL/journals manually. "
        "Archives contain credentials/private history and are not encrypted. Restore retains bytes, paths within each root, "
        "numeric owners and ordinary permissions; mount roots at their original runtime paths. No history merge or version migration."))
    commands = result.add_subparsers(dest="command", required=True)
    for command in ("create", "validate", "plan", "restore", "verify-restored"):
        child = commands.add_parser(command)
        child.add_argument("--jellyfin-version", required=True, help="Exact stopped server version; restore does not migrate databases.")
        child.add_argument("--siphon-version", required=True, help="Exact installed plugin version, verified against its meta.json.")
        child.add_argument("--owner", action="append", required=True, help="Approved numeric UID:GID; repeat for mixed ownership. Preserved, never remapped.")
        child.add_argument("--max-bytes", type=int, default=128 * 1024**3, help="Maximum total payload bytes (default: 128 GiB).")
        child.add_argument("--max-file-bytes", type=int, default=32 * 1024**3, help="Maximum bytes per file (default: 32 GiB).")
        child.add_argument("--max-members", type=int, default=200000, help="Maximum declared filesystem entries (default: 200000).")
        child.add_argument("--sha256", help="Expected whole-archive SHA-256 from trusted, separately retained evidence.")
        if command in ("create", "restore", "verify-restored"):
            child.add_argument("--ack-stopped", required=True, action="store_true", help="I stopped this instance and disabled all restarters/writers.")
        if command == "create":
            child.add_argument("--program-data", required=True, help="Entire Jellyfin program-data tree, containing data/ and plugins/.")
            child.add_argument("--config-dir", help="Jellyfin configuration tree; default: PROGRAM_DATA/config.")
            child.add_argument("--extra-root", action="append", default=[], help="Additional complete non-overlapping tree: NAME=PATH (custom metadata, etc.).")
            child.add_argument("--output", required=True, help="New uncompressed .tar outside every source tree; never overwritten.")
        else:
            child.add_argument("archive", help="Snapshot created by this tool, not a plugin release ZIP or arbitrary tar.")
        if command in ("plan", "restore", "verify-restored"):
            child.add_argument("--destination", required=True, help="Restore envelope containing program/, optional config/ and extra roots.")
    return result


def interrupted(_signal, _frame):
    # Let structured cleanup finish even if another signal arrives during it.
    for item in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(item, signal.SIG_IGN)
    raise KeyboardInterrupt


def main():
    arguments = parser().parse_args()
    try:
        require(sys.version_info >= (3, 11) and os.name == "posix" and hasattr(os, "O_NOFOLLOW"),
                "This utility requires Python 3.11+ and a local POSIX filesystem (Linux/macOS), not Windows or a network share.")
        require(re.fullmatch(VERSION_PATTERN, arguments.jellyfin_version) and re.fullmatch(VERSION_PATTERN, arguments.siphon_version),
                "Versions must contain three or four numeric components.")
        require(0 < arguments.max_file_bytes <= arguments.max_bytes <= 16 * 1024**4
                and 0 < arguments.max_members <= 1000000, "Requested archive limits are outside supported bounds.")
        require(not arguments.sha256 or re.fullmatch(r"[0-9a-fA-F]{64}", arguments.sha256), "Expected SHA-256 must contain 64 hexadecimal characters.")
        owners(arguments)
        for item in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            signal.signal(item, interrupted)
        os.umask(0o077)
        if arguments.command == "create":
            create(arguments)
        else:
            process_archive(arguments)
        return 0
    except BackupError as error:
        print("Backup refused: " + str(error), file=sys.stderr)
    except KeyboardInterrupt:
        print("Backup interrupted; private incomplete staging is removed where cleanup can run. No source data was changed.", file=sys.stderr)
    except (OSError, ValueError, TypeError, KeyError, AttributeError, RecursionError, tarfile.TarError, sqlite3.Error, ET.ParseError):
        # Stored paths, XML/JSON fragments and OS errors may contain credentials; do not print exception details.
        print("Backup refused: filesystem, archive or database validation failed. Check ownership, free space, layout and a clean stop.", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
