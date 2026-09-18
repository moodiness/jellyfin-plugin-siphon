#!/usr/bin/env python3
"""Run real, isolated Jellyfin 12.1 smoke scenarios. See USAGE.txt and --help."""
import argparse
import base64
from contextlib import contextmanager
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[2]
PLUGIN = "b2df1c14-4b7e-4e7b-9a95-8f9ad8d2b0c1"
FFMPEG = "/usr/lib/jellyfin-ffmpeg/ffmpeg"
FFPROBE = "/usr/lib/jellyfin-ffmpeg/ffprobe"
SCENARIOS = ("install", "playback", "exports", "notifications", "segments", "ui", "upgrade", "full")


class Unavailable(RuntimeError):
    pass


class HttpFailure(RuntimeError):
    def __init__(self, method, path, status):
        # Drop query/capability paths and response bodies from errors/evidence.
        super().__init__(f"Native request {method} failed with HTTP {status}")
        self.status = status


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def failure_reason(error):
    reason = str(error) if isinstance(error, (RuntimeError, AssertionError)) else type(error).__name__
    frames = traceback.extract_tb(error.__traceback__, limit=-3)
    locations = " -> ".join(f"{Path(frame.filename).name}:{frame.lineno}" for frame in frames)
    return f"{reason} at {locations}"


def command(args, *, timeout=180, input=None):
    process = subprocess.run([str(value) for value in args], input=input, text=True,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
    if process.returncode:
        raise RuntimeError(f"{Path(str(args[0])).name} command failed (exit {process.returncode}); output withheld to protect credentials")
    return process.stdout.strip()


def eventually(action, predicate=bool, timeout=120, description="condition"):
    until = time.monotonic() + timeout
    while time.monotonic() < until:
        try:
            result = action()
            if predicate(result):
                return result
        except (urllib.error.URLError, ConnectionError, HttpFailure):
            pass
        time.sleep(.5)
    raise AssertionError(f"Timed out waiting for {description}")


class Api:
    def __init__(self, base, token=None, user_id=None):
        self.base, self.token, self.user_id = base, token, user_id

    def call(self, method, path, body=None, *, raw=False, headers=None, expected=(200, 201, 202, 204)):
        url = path if path.startswith(("http://", "https://")) else self.base + "/" + path.lstrip("/")
        # Never send native user credentials to a fixture/external host.
        require(urllib.parse.urlsplit(url).netloc == urllib.parse.urlsplit(self.base).netloc, "Cross-origin API request refused")
        auth = 'MediaBrowser Client="Siphon Smoke", Device="Owned Docker", DeviceId="siphon-smoke", Version="1.0"'
        if self.token:
            auth += ', Token="' + self.token + '"'
        request = urllib.request.Request(url, method=method,
            data=None if body is None else json.dumps(body).encode(),
            headers={"Authorization": auth, "Content-Type": "application/json", **(headers or {})})
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                status, content = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, content = error.code, error.read(16384)
        if status not in expected:
            raise HttpFailure(method, path, status)
        if raw:
            return content
        if status >= 400:
            return None  # Expected refusals need no interpretation of an arbitrary error body.
        return json.loads(content) if content else None

    def login(self, username, password):
        result = self.call("POST", "/Users/AuthenticateByName", {"Username": username, "Pw": password})
        return Api(self.base, result["AccessToken"], result["User"]["Id"])


class Harness:
    def __init__(self, args):
        self.args = args
        self.name = "siphon-smoke-" + uuid.uuid4().hex[:12]
        self.temp = Path(tempfile.mkdtemp(prefix=self.name + "-"))
        self.containers = []
        self.network_created = False
        self.steps = []
        self.password = secrets.token_urlsafe(24)
        self.admin = self.alice = self.bob = None
        self.current_package = None
        self.current_version = None
        self.fixture_api = None

    @contextmanager
    def step(self, name):
        start = time.monotonic()
        row = {"name": name, "status": "running"}
        self.steps.append(row)
        print(name, flush=True)
        try:
            yield row
            row["status"] = "passed"
        except Unavailable as error:
            row.update(status="skipped", reason=str(error))
            raise
        except Exception as error:
            row.update(status="failed", reason=failure_reason(error))
            raise
        finally:
            row["seconds"] = round(time.monotonic() - start, 3)

    def docker(self, *args, timeout=180):
        transient = args and args[0] == "run" and "--rm" in args and "--name" not in args
        name = self.name + "-tool-" + uuid.uuid4().hex[:8] if transient else None
        if name:
            self.containers.append(name)
            args = (args[0], "--name", name, *args[1:])
        result = command(["docker", *args], timeout=timeout)
        if name:
            self.containers.remove(name)
        return result

    def prepare_package(self):
        with self.step("package") as evidence:
            if self.args.package:
                package = self.args.package.resolve()
            else:
                if not shutil.which("dotnet"):
                    raise Unavailable(".NET SDK from global.json is required when --package is omitted")
                command(["dotnet", "build", ROOT / "src/Jellyfin.Plugin.Siphon", "-c", "Release"], timeout=600)
                output = self.temp / "package"
                command([sys.executable, ROOT / "scripts/package.py", "--output", output], timeout=180)
                archives = list(output.glob("siphon-*.zip"))
                require(len(archives) == 1, "Packaging did not produce exactly one plugin archive")
                package = archives[0]
            self.current_version = self.archive_metadata(package)["version"]
            self.current_package = package
            evidence.update(version=self.current_version, sha256=hashlib.sha256(package.read_bytes()).hexdigest())

    @staticmethod
    def archive_metadata(package):
        with zipfile.ZipFile(package) as archive:
            require("Jellyfin.Plugin.Siphon.dll" in archive.namelist(), "Archive has no plugin assembly")
            metadata = json.loads(archive.read("meta.json"))
            require(uuid.UUID(metadata["guid"]) == uuid.UUID(PLUGIN) and metadata["targetAbi"].startswith("12.1."), "Unexpected archive plugin or ABI")
            return metadata

    def install_archive(self, package):
        metadata = self.archive_metadata(package)
        plugins = self.temp / "config/plugins"
        plugins.mkdir(parents=True, exist_ok=True)
        # Owned instance only, stopped for replacement. Never operate on caller paths.
        for old in plugins.glob("Siphon_*"):
            require(old.is_dir() and not old.is_symlink(), "Unexpected plugin directory")
            shutil.rmtree(old)
        target = plugins / ("Siphon_" + metadata["version"])
        target.mkdir()
        with zipfile.ZipFile(package) as archive:
            for entry in archive.infolist():
                path = Path(entry.filename)
                require(not path.is_absolute() and ".." not in path.parts and "\\" not in entry.filename,
                        "Unsafe plugin archive member")
                require((entry.external_attr >> 16) & 0o170000 != 0o120000, "Plugin archive contains a symlink")
                require(entry.file_size <= 128 * 1024 * 1024, "Plugin archive member exceeds size limit")
            require(sum(entry.file_size for entry in archive.infolist()) <= 256 * 1024 * 1024, "Plugin archive exceeds size limit")
            archive.extractall(target)
        return metadata["version"]

    def prerequisites(self):
        with self.step("runtime-prerequisites"):
            for binary in ("docker", "openssl"):
                if not shutil.which(binary):
                    raise Unavailable(f"Missing prerequisite: {binary}")
            try:
                self.docker("info", "--format", "{{.ServerVersion}}")
            except RuntimeError as error:
                raise Unavailable("Docker daemon is unavailable") from error
            for image in (self.args.image, "python:3.12-alpine", "nginx:stable-alpine"):
                try:
                    self.docker("image", "inspect", image)
                except RuntimeError:
                    try:
                        self.docker("pull", image, timeout=600)
                    except RuntimeError as error:
                        raise Unavailable(f"Required container image unavailable: {image}") from error

    def ffmpeg(self, *args):
        self.docker("run", "--rm", "--network", "none", "--cpus", "2", "--memory", "768m", "--pids-limit", "128",
                    "--entrypoint", FFMPEG, "-v", f"{self.temp / 'fixture/media'}:/media", self.args.image,
                    "-hide_banner", "-loglevel", "error", "-threads", "2", "-y", *args, timeout=180)

    def fixture(self):
        with self.step("owned-local-fixtures"):
            media = self.temp / "fixture/media"
            tls = self.temp / "fixture/tls"
            media.mkdir(parents=True)
            tls.mkdir()
            shutil.copyfile(ROOT / "scripts/smoke/fixture.py", self.temp / "fixture/fixture.py")
            command(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "2", "-sha256",
                     "-keyout", tls / "key.pem", "-out", tls / "cert.pem", "-subj", "/CN=Siphon Smoke Owned Fixture",
                     "-addext", "subjectAltName=DNS:fixture,DNS:api.introdb.app",
                     "-addext", "basicConstraints=critical,CA:TRUE"])
            self.ffmpeg("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                        "-t", "40", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", "/media/movie.mp4")
            self.ffmpeg("-f", "lavfi", "-i", "testsrc2=size=240x360:rate=1", "-frames:v", "1", "-threads", "1", "/media/series-poster.png")
            self.ffmpeg("-i", "/media/movie.mp4", "-an", "-c:v", "copy", "-hls_time", "4", "-hls_list_size", "0", "-hls_playlist_type", "vod",
                        "-hls_segment_filename", "/media/video-%03d.ts", "/media/video.m3u8")
            for language, frequency in (("en", 440), ("fr", 880)):
                self.ffmpeg("-f", "lavfi", "-i", f"sine=frequency={frequency}:sample_rate=48000", "-t", "40", "-c:a", "aac",
                            "-hls_time", "4", "-hls_list_size", "0", "-hls_playlist_type", "vod",
                            "-hls_segment_filename", f"/media/{language}-%03d.ts", f"/media/{language}.m3u8")
                (media / f"{language}.vtt").write_text(f"WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nGenerated {language} subtitle\n", encoding="utf-8")
                (media / f"sub-{language}.m3u8").write_text(f"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:40\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:40,\n{language}.vtt\n#EXT-X-ENDLIST\n", encoding="utf-8")
            (media / "master.m3u8").write_text('#EXTM3U\n#EXT-X-VERSION:3\n'
                '#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="English",LANGUAGE="en",DEFAULT=YES,AUTOSELECT=YES,URI="en.m3u8"\n'
                '#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="French",LANGUAGE="fr",DEFAULT=NO,AUTOSELECT=YES,URI="fr.m3u8"\n'
                '#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",LANGUAGE="en",DEFAULT=NO,AUTOSELECT=YES,URI="sub-en.m3u8"\n'
                '#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="French",LANGUAGE="fr",DEFAULT=NO,AUTOSELECT=YES,URI="sub-fr.m3u8"\n'
                '#EXT-X-STREAM-INF:BANDWIDTH=1000000,CODECS="avc1.42c01e,mp4a.40.2",AUDIO="audio",SUBTITLES="subs"\nvideo.m3u8\n', encoding="utf-8")
            self.docker("network", "create", "--internal", self.name)
            self.network_created = True
            name = self.name + "-fixture"
            self.containers.append(name)
            self.docker("run", "-d", "--name", name, "--network", self.name, "--network-alias", "fixture",
                        "--network-alias", "api.introdb.app", "--cpus", "1", "--memory", "192m", "--pids-limit", "128",
                        "-v", f"{self.temp / 'fixture'}:/fixture:ro",
                        "python:3.12-alpine", "python", "/fixture/fixture.py")
            # Docker internal networks deliberately provide no host port mapping.
            # Only this fixed-upstream ingress joins the default bridge; Jellyfin
            # and the media/notification fixtures retain their internal-only network.
            ingress_config = self.temp / "ingress.conf"
            ingress_config.write_text("""
worker_processes 1;
events { worker_connections 256; }
http {
    access_log off;
    resolver 127.0.0.11 valid=1s;
    map $http_upgrade $connection_upgrade { default upgrade; '' close; }
    proxy_http_version 1.1;
    proxy_set_header Host $http_host;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_buffering off;
    proxy_read_timeout 180s;
    server {
        listen 8080;
        location / { set $backend fixture:8080; proxy_pass http://$backend$request_uri; }
    }
    server {
        listen 8096;
        location / { set $backend jellyfin:8096; proxy_pass http://$backend$request_uri; }
    }
}
""", encoding="utf-8")
            self.ingress = self.name + "-ingress"
            self.containers.append(self.ingress)
            self.docker("run", "-d", "--name", self.ingress, "--network", "bridge",
                        "--cpus", "1", "--memory", "96m", "--pids-limit", "32",
                        "-p", "127.0.0.1::8080", "-p", "127.0.0.1::8096",
                        "-v", f"{ingress_config}:/etc/nginx/nginx.conf:ro", "nginx:stable-alpine")
            self.docker("network", "connect", self.name, self.ingress)
            port = self.docker("port", self.ingress, "8080/tcp").rsplit(":", 1)[1]
            self.fixture_api = Api("http://127.0.0.1:" + port)
            eventually(lambda: self.fixture_api.call("GET", "/health"), description="fixture readiness")

    def start(self):
        name = self.name + "-jellyfin"
        self.containers.append(name)
        (self.temp / "cache").mkdir(exist_ok=True)
        self.docker("run", "-d", "--name", name, "--network", self.name, "--network-alias", "jellyfin",
                    "--cpus", "2", "--memory", "2g", "--pids-limit", "512",
                    "--user", f"{os.getuid()}:{os.getgid()}",
                    "-e", "SSL_CERT_FILE=/fixture/tls/cert.pem", "-e", "JELLYFIN_FFMPEG=" + FFMPEG,
                    "-v", f"{self.temp / 'config'}:/config", "-v", f"{self.temp / 'cache'}:/cache",
                    "-v", f"{self.temp / 'fixture'}:/fixture:ro", self.args.image)
        port = self.docker("port", self.ingress, "8096/tcp").rsplit(":", 1)[1]
        self.api = Api("http://127.0.0.1:" + port)
        eventually(lambda: self.api.call("GET", "/health", raw=True),
                   lambda body: body.strip() == b"Healthy", timeout=180, description="main Jellyfin host health")
        public = eventually(lambda: self.api.call("GET", "/System/Info/Public"),
                            lambda info: isinstance(info, dict) and isinstance(info.get("Version"), str),
                            timeout=180, description="versioned Jellyfin readiness response")
        require(public["Version"].startswith("12.1."), "Smoke harness requires an actual Jellyfin 12.1 server")

    def stop(self):
        name = self.name + "-jellyfin"
        self.docker("stop", "--time", "30", name)
        self.docker("rm", name)
        self.containers.remove(name)

    def relogin(self):
        self.admin = self.api.login("smoke-admin", self.password)
        self.alice = self.api.login("smoke-alice", self.password)
        self.bob = self.api.login("smoke-bob", self.password)

    def bootstrap(self, version):
        with self.step("native-installation-and-users") as evidence:
            self.start()
            self.api.call("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
            self.api.call("GET", "/Startup/User")
            self.api.call("POST", "/Startup/User", {"Name": "smoke-admin", "Password": self.password})
            self.api.call("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
            self.api.call("POST", "/Startup/Complete")
            self.admin = self.api.login("smoke-admin", self.password)
            for name in ("smoke-alice", "smoke-bob"):
                user = self.admin.call("POST", "/Users/New", {"Name": name, "Password": self.password})
                policy = user["Policy"]
                policy.update(EnableAllFolders=True, EnableMediaPlayback=True, EnableContentDownloading=True)
                self.admin.call("POST", f"/Users/{user['Id']}/Policy", policy)
            self.relogin()
            plugins = self.admin.call("GET", "/Plugins")
            plugin = next((item for item in plugins if uuid.UUID(item["Id"]) == uuid.UUID(PLUGIN)), None)
            observed = [{key: item.get(key) for key in ("Id", "Name", "Version", "Status")} for item in plugins if "siphon" in item.get("Name", "").lower()]
            require(plugin is not None and plugin["Version"] == version, f"Native plugin registry expected Siphon {version}; observed {observed}")
            require(plugin.get("Status") not in ("Malfunctioned", "NotSupported", "Deleted"), "Plugin failed to load")
            evidence.update(serverVersion=self.api.call("GET", "/System/Info/Public")["Version"], pluginVersion=plugin["Version"], nativeUsers=3)

    def config(self):
        return self.admin.call("GET", f"/Plugins/{PLUGIN}/Configuration")

    def save_config(self, config):
        self.admin.call("POST", f"/Plugins/{PLUGIN}/Configuration", config)

    def configure(self, legacy=False):
        with self.step("shared-catalog-configuration"):
            shared = {"Id": uuid.uuid4().hex, "ManifestUrl": "http://fixture:8080/shared/manifest.json", "DisplayName": "Shared fixture", "Enabled": True,
                      "Catalogs": [{"Type": kind, "Id": "generated", "Enabled": True, "Key": uuid.uuid4().hex, "Presentation": "Both"} for kind in ("movie", "series")]}
            personal = {"Id": uuid.uuid4().hex, "ManifestUrl": "http://fixture:8080/personal/manifest.json", "DisplayName": "Personal fixture", "Enabled": True, "Catalogs": []}
            config = self.config()
            config.update(PublicBaseUrl="http://jellyfin:8096", AllowedPrivateHosts=["fixture", "api.introdb.app", "jellyfin"], Addons=[shared])
            if not legacy:
                self.operations_config(config, personal)
            self.save_config(config)
            self.sync()
            items = eventually(lambda: self.items("Movie,Episode,Series"), lambda rows: any(row["Type"] == "Episode" for row in rows), description="native catalog publication")
            self.movie = next(row["Id"] for row in items if row["Type"] == "Movie")
            self.series = next(row["Id"] for row in items if row["Type"] == "Series")

    def operations_config(self, config, personal=None):
        personal = personal or {"Id": uuid.uuid4().hex, "ManifestUrl": "http://fixture:8080/personal/manifest.json",
                                "DisplayName": "Personal fixture", "Enabled": True, "Catalogs": []}
        config.update(UserProfiles=[{"UserId": self.alice.user_id, "OverrideAddons": True, "Addons": [personal]}],
                      EnableDownloadQueue=True, DownloadMaxStorageMiB=256, DownloadMaxStoragePerUserMiB=128, DownloadMaxFileMiB=64,
                      EnableCalendarNotifications=True, EnableNotificationWebhooks=True, EnableIntroDb=True, DefaultSearchMode="All")

    def health(self):
        with self.step("health-local-observations-and-redaction") as evidence:
            # Stabilize outstanding publication/probe requests before measuring this read-only call.
            last = self.fixture_api.call("GET", "/control/counters")
            def stable():
                nonlocal last
                value = self.fixture_api.call("GET", "/control/counters")
                equal, last = value == last, value
                return equal
            eventually(stable, description="fixture request counters settle")
            before = self.fixture_api.call("GET", "/control/counters")
            health = self.admin.call("GET", "/Siphon/Diagnostics/Health")
            after = self.fixture_api.call("GET", "/control/counters")
            require(before == after, "Health refresh initiated an upstream request")
            require(health["SourceVersion"] == self.current_version, "Health source version does not match installed archive")
            # The embedded catalog may already publish this source version after a release.
            serialized = json.dumps(health)
            for forbidden in (self.password, self.admin.token, self.alice.token, "http://", "https://", "/config", "/fixture", "Generated Movie", "smoke-alice"):
                require(forbidden not in serialized, "Health exposed private runtime data")
            self.alice.call("GET", "/Siphon/Diagnostics/Health", expected=(403,))
            evidence.update(noUpstreamRequests=True, sourceVersion=health["SourceVersion"],
                            repositoryVersionAtBuild=health["RepositoryVersionAtBuild"], redacted=True)

    def sync(self):
        tasks = self.admin.call("GET", "/ScheduledTasks")
        task = next((task for task in tasks if task.get("Key") == "SiphonCatalogSync"), None)
        require(task is not None, "Native catalog scheduled task was not registered")
        previous = (task.get("LastExecutionResult") or {}).get("EndTimeUtc")
        self.admin.call("POST", "/ScheduledTasks/Running/" + task["Id"])
        result = eventually(lambda: self.admin.call("GET", "/ScheduledTasks/" + task["Id"]),
            lambda value: value["State"] == "Idle" and (value.get("LastExecutionResult") or {}).get("EndTimeUtc") != previous,
            description="native Siphon catalog task", timeout=180)
        require(result["LastExecutionResult"]["Status"] == "Completed", "Native catalog task failed")

    def items(self, types="Movie,Episode,Series", api=None):
        api = api or self.alice
        query = urllib.parse.urlencode({"UserId": api.user_id, "Recursive": "true", "IncludeItemTypes": types, "Fields": "ProviderIds,MediaSources", "Limit": "200"})
        return api.call("GET", "/Items?" + query)["Items"]

    def sources(self, api):
        return api.call("GET", f"/Siphon/Items/{self.movie}/Sources")["Sources"]

    def source(self, api, suffix):
        return next(source for source in self.sources(api) if source["Name"].endswith(suffix))

    def playback(self):
        with self.step("search-import-profile-precedence-and-native-playback") as evidence:
            a, b = self.sources(self.alice), self.sources(self.bob)
            require(a and b and all("personal" in row["Name"] for row in a) and all("shared" in row["Name"] for row in b), "Personal addon override did not replace shared playback providers")
            require(not {row["MediaSourceId"] for row in a} & {row["MediaSourceId"] for row in b}, "Native source identity leaked across users")
            found = self.admin.call("GET", "/Siphon/Search?searchTerm=Generated%20Discovery")["Items"]
            candidate = next((item for item in found if item.get("CanAdd") and item["Type"] == "Movie"), None)
            require(candidate is not None, "Addon discovery did not return an importable native preview")
            adopted = self.admin.call("POST", f"/Siphon/Search/Items/{candidate['Id']}/Add")
            require(adopted["IsAdded"] and adopted["Id"] == candidate["Id"], "Import changed native identity or failed adoption")
            native = self.alice.call("GET", f"/Users/{self.alice.user_id}/Items/{candidate['Id']}")
            require(uuid.UUID(native["Id"]) == uuid.UUID(candidate["Id"]), "Imported item is not visible through native API")
            source = self.source(self.alice, " HTTP")
            info = self.alice.call("POST", f"/Items/{self.movie}/PlaybackInfo?UserId={self.alice.user_id}",
                                   {"UserId": self.alice.user_id, "MediaSourceId": source["MediaSourceId"], "EnableDirectPlay": True, "EnableDirectStream": True})
            require(any(item["Id"] == source["MediaSourceId"] for item in info["MediaSources"]), "Native PlaybackInfo lost explicitly selected source")
            selected = next(item for item in info["MediaSources"] if item["Id"] == source["MediaSourceId"])
            # PlaybackInfo advertises unopened sources; native readers need the selected
            # source opened and probed before deriving a progressive output container.
            opened = self.alice.call("POST", "/LiveStreams/Open", {"OpenToken": selected["OpenToken"],
                "ItemId": self.movie, "UserId": self.alice.user_id, "PlaySessionId": info.get("PlaySessionId")})["MediaSource"]
            require(opened["Id"] == source["MediaSourceId"], "Native progressive opening substituted source identity")
            try:
                data = self.alice.call("GET", f"/Videos/{self.movie}/stream?Static=true&MediaSourceId={source['MediaSourceId']}", raw=True)
            finally:
                if opened.get("RequiresClosing") and opened.get("LiveStreamId"):
                    self.alice.call("POST", "/LiveStreams/Close?" + urllib.parse.urlencode({"LiveStreamId": opened["LiveStreamId"]}))
            expected = (self.temp / "fixture/media/movie.mp4").read_bytes()
            require(hashlib.sha256(data).digest() == hashlib.sha256(expected).digest(), "Native progressive playback bytes differ from generated fixture")
            evidence.update(profileIsolation=True, importedIdentityPreserved=True, progressiveSha256=hashlib.sha256(data).hexdigest())
        with self.step("series-search-artwork-add-remove-and-catalog-retention") as evidence:
            series_preview = next((item for item in found if item.get("CanAdd") and item["Type"] == "Series"), None)
            require(series_preview is not None, "Series discovery did not return an importable preview")
            discovered = series_preview["Id"]
            preview_children = self.admin.call("GET", "/Items?" + urllib.parse.urlencode({
                "UserId": self.admin.user_id, "ParentId": discovered, "Recursive": "true", "IncludeItemTypes": "Episode"}))
            require(preview_children["TotalRecordCount"] == 0, "Poster lookup eagerly expanded the entire series")
            poster = self.admin.call("GET", f"/Items/{discovered}/Images/Primary?format=Png", raw=True)
            require(poster.startswith(b"\x89PNG\r\n\x1a\n"), "Native series preview did not serve its generated poster")
            adopted = self.admin.call("POST", f"/Siphon/Search/Items/{discovered}/Add")
            require(adopted["IsAdded"] and uuid.UUID(adopted["Id"]) == uuid.UUID(discovered), "Series adoption did not retain native identity")
            children = self.admin.call("GET", "/Items?" + urllib.parse.urlencode({
                "UserId": self.admin.user_id, "ParentId": discovered, "Recursive": "true", "IncludeItemTypes": "Episode"}))["Items"]
            require(any(row.get("IndexNumber") == 1 and row.get("ParentIndexNumber") == 1 for row in children),
                    "Series adoption did not publish the generated episode")
            added = self.admin.call("GET", "/Siphon/Search/Items")["Items"]
            require(any(uuid.UUID(row["Id"]) == uuid.UUID(discovered) for row in added), "Added series is missing from saved additions")
            history_item = children[0]["Id"]
            history_before = self.alice.call("POST", f"/UserItems/{history_item}/UserData?userId={self.alice.user_id}", {
                "IsFavorite": True, "Played": True, "PlayCount": 3, "PlaybackPositionTicks": 12000000,
                "LastPlayedDate": "2020-01-02T03:04:05Z"})
            removed = self.admin.call("DELETE", f"/Siphon/Search/Items/{discovered}")
            require(uuid.UUID(removed["Id"]) == uuid.UUID(discovered) and removed.get("RemainingItem") is None,
                    "Removing a discovery-only series retained its native item")
            self.admin.call("GET", f"/Users/{self.admin.user_id}/Items/{discovered}", expected=(404,))
            for child in children:
                self.admin.call("GET", f"/Users/{self.admin.user_id}/Items/{child['Id']}", expected=(404,))
            added = self.admin.call("GET", "/Siphon/Search/Items")["Items"]
            require(all(uuid.UUID(row["Id"]) != uuid.UUID(discovered) for row in added), "Removed series remains in saved additions")
            rediscovered = self.admin.call("GET", "/Siphon/Search?searchTerm=Generated%20Discovery")["Items"]
            require(any(uuid.UUID(row["Id"]) == uuid.UUID(discovered) for row in rediscovered), "Rediscovery changed the canonical series identity")
            self.admin.call("POST", f"/Siphon/Search/Items/{discovered}/Add")
            history_after = self.alice.call("GET", f"/UserItems/{history_item}/UserData?userId={self.alice.user_id}")
            for field in ("IsFavorite", "Played", "PlayCount", "PlaybackPositionTicks", "LastPlayedDate"):
                require(history_after.get(field) == history_before.get(field), "Removing and re-adding a discovery lost native watch state: " + field)
            shared = self.admin.call("POST", f"/Siphon/Search/Items/{self.series}/Add")
            require(shared["IsAdded"], "Shared catalog series could not acquire manual ownership")
            retained = self.admin.call("DELETE", f"/Siphon/Search/Items/{self.series}")
            remaining = retained.get("RemainingItem")
            require(uuid.UUID(retained["Id"]) == uuid.UUID(self.series) and remaining is not None
                    and uuid.UUID(remaining["Id"]) == uuid.UUID(self.series) and not remaining["IsAdded"],
                    "Removing manual ownership did not retain the shared catalog item")
            require(uuid.UUID(self.bob.call("GET", f"/Users/{self.bob.user_id}/Items/{self.series}")["Id"]) == uuid.UUID(self.series),
                    "Removing manual ownership hid the shared series from another user")
            shared_children = self.bob.call("GET", "/Items?" + urllib.parse.urlencode({
                "UserId": self.bob.user_id, "ParentId": self.series, "Recursive": "true", "IncludeItemTypes": "Episode"}))["Items"]
            require(any(row.get("IndexNumber") == 1 and row.get("ParentIndexNumber") == 1 for row in shared_children),
                    "Removing manual ownership deleted the shared catalog episode")
            added = self.admin.call("GET", "/Siphon/Search/Items")["Items"]
            require(all(uuid.UUID(row["Id"]) != uuid.UUID(self.series) for row in added), "Shared series retained manual ownership")
            evidence.update(metadataPosterHydrated=True, nativePosterSha256=hashlib.sha256(poster).hexdigest(),
                            seriesIdentityPreserved=True, seriesEpisodePublished=True, discoveryRemoval=True,
                            removalReadditionHistoryPreserved=True, sharedCatalogRetained=True)
        with self.step("native-finite-hls-reader-and-playback-revocation") as evidence:
            selected = self.source(self.alice, " HLS")
            info = self.alice.call("POST", f"/Items/{self.movie}/PlaybackInfo?UserId={self.alice.user_id}",
                                  {"UserId": self.alice.user_id, "MediaSourceId": selected["MediaSourceId"]})
            source = next(row for row in info["MediaSources"] if row["Id"] == selected["MediaSourceId"])
            opened = self.alice.call("POST", "/LiveStreams/Open", {"OpenToken": source["OpenToken"],
                "ItemId": self.movie, "UserId": self.alice.user_id, "PlaySessionId": info.get("PlaySessionId")})["MediaSource"]
            require(opened["Id"] == selected["MediaSourceId"], "Native HLS opening substituted source identity")
            require(any(row["Type"] == "Video" for row in opened["MediaStreams"]) and any(row["Type"] == "Audio" for row in opened["MediaStreams"]),
                    "Native reader did not probe video and audio from controlled HLS")
            self.docker("exec", self.name + "-jellyfin", FFMPEG, "-v", "error", "-i", opened["Path"],
                        "-t", "2", "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-", timeout=90)
            policy = self.admin.call("GET", "/Users/" + self.alice.user_id)["Policy"]
            policy["EnableMediaPlayback"] = False
            self.admin.call("POST", f"/Users/{self.alice.user_id}/Policy", policy)
            parsed = urllib.parse.urlsplit(opened["Path"])
            self.api.call("GET", parsed.path + ("?" + parsed.query if parsed.query else ""), raw=True, expected=(401, 403, 404, 410))
            policy["EnableMediaPlayback"] = True
            self.admin.call("POST", f"/Users/{self.alice.user_id}/Policy", policy)
            if opened.get("RequiresClosing") and opened.get("LiveStreamId"):
                self.alice.call("POST", "/LiveStreams/Close?" + urllib.parse.urlencode({"LiveStreamId": opened["LiveStreamId"]}))
            evidence.update(nativeProbed=True, decodedSeconds=2, revokedPlayback=True)

    def job(self, api, identifier):
        return next(row for row in api.call("GET", "/Siphon/Downloads")["Jobs"] if row["Id"] == identifier)

    def complete_job(self, api, identifier):
        result = eventually(lambda: self.job(api, identifier), lambda row: row["State"] in ("Completed", "Failed", "Cancelled"), timeout=240, description="offline export completion")
        require(result["State"] == "Completed", "Offline export did not complete")
        return result

    def exports(self):
        with self.step("direct-and-queued-export-revocation") as evidence:
            source = self.source(self.alice, " HTTP")
            request = {"ItemId": self.movie, "MediaSourceId": source["MediaSourceId"]}
            ticket = self.alice.call("POST", f"/Siphon/Items/{self.movie}/DownloadTicket", request)
            data = self.api.call("GET", ticket["Url"], raw=True)
            expected = (self.temp / "fixture/media/movie.mp4").read_bytes()
            require(data == expected, "Direct export is not byte-identical")
            direct = self.alice.call("GET", f"/Siphon/Items/{self.movie}/Download?MediaSourceId={source['MediaSourceId']}", raw=True)
            native = self.alice.call("GET", f"/Items/{source['MediaSourceId']}/Download", raw=True)
            require(direct == expected and native == expected, "Direct/native selected-version download bytes differ")
            job = self.alice.call("POST", "/Siphon/Downloads", request)
            self.complete_job(self.alice, job["Id"])
            require(not any(row["Id"] == job["Id"] for row in self.bob.call("GET", "/Siphon/Downloads")["Jobs"]), "Private job appeared for another user")
            self.bob.call("POST", f"/Siphon/Downloads/{job['Id']}/Ticket", expected=(403, 404))
            queued_ticket = self.alice.call("POST", f"/Siphon/Downloads/{job['Id']}/Ticket")
            require(self.api.call("GET", queued_ticket["Url"], raw=True) == expected, "Queued export bytes differ")
            user = self.admin.call("GET", "/Users/" + self.alice.user_id)
            policy = user["Policy"]
            policy["EnableContentDownloading"] = False
            self.admin.call("POST", f"/Users/{self.alice.user_id}/Policy", policy)
            for url in (ticket["Url"], queued_ticket["Url"]):
                self.api.call("GET", url, raw=True, expected=(401, 403, 404, 410))
            self.alice.call("POST", "/Siphon/Downloads", request, expected=(403, 404))
            policy["EnableContentDownloading"] = True
            self.admin.call("POST", f"/Users/{self.alice.user_id}/Policy", policy)
            evidence.update(directSha256=hashlib.sha256(data).hexdigest(), revokedLinks=2, ownerIsolation=True)
        with self.step("queue-pause-priority-and-restart"):
            slow = self.source(self.alice, " Slow")
            job = self.alice.call("POST", "/Siphon/Downloads", {"ItemId": self.movie, "MediaSourceId": slow["MediaSourceId"]})
            eventually(lambda: self.job(self.alice, job["Id"]), lambda row: row["State"] == "Running", description="slow job starts")
            self.alice.call("POST", f"/Siphon/Downloads/{job['Id']}/Pause")
            self.alice.call("PUT", f"/Siphon/Downloads/{job['Id']}/Priority", {"Priority": 7})
            paused = eventually(lambda: self.job(self.alice, job["Id"]), lambda row: row["State"] == "Paused", description="pause becomes durable")
            self.stop()
            self.start()
            self.relogin()
            restored = self.job(self.alice, job["Id"])
            require(restored["State"] == "Paused" and restored["Priority"] == 7, "Restart lost explicit pause or priority")
            require(restored["MediaSourceId"] == paused["MediaSourceId"], "Restart substituted the selected version")
            self.alice.call("POST", f"/Siphon/Downloads/{job['Id']}/Resume")
            self.alice.call("POST", f"/Siphon/Downloads/{job['Id']}/Cancel")
            eventually(lambda: self.job(self.alice, job["Id"]), lambda row: row["State"] == "Cancelled", description="resumed job cancellation")
        with self.step("finite-hls-selected-audio-and-subtitles") as evidence:
            source = self.source(self.alice, " HLS")
            request = {"ItemId": self.movie, "MediaSourceId": source["MediaSourceId"]}
            preparation = self.alice.call("POST", "/Siphon/Downloads/Preparation", request)
            require({"en", "fr"} <= set(preparation["AudioLanguages"]) and {"en", "fr"} <= set(preparation["SubtitleLanguages"]), "HLS preparation lost advertised tracks")
            self.alice.call("POST", "/Siphon/Downloads", {**request, "AudioLanguages": ["unadvertised"], "SubtitleLanguages": []}, expected=(400, 409, 415))
            job = self.alice.call("POST", "/Siphon/Downloads", {**request, "AudioLanguages": ["fr"], "SubtitleLanguages": ["en"]})
            self.complete_job(self.alice, job["Id"])
            ticket = self.alice.call("POST", f"/Siphon/Downloads/{job['Id']}/Ticket")
            output = self.temp / "export"
            output.mkdir(exist_ok=True)
            (output / "selected.mkv").write_bytes(self.api.call("GET", ticket["Url"], raw=True))
            probe = json.loads(self.docker("run", "--rm", "--network", "none", "--cpus", "1", "--memory", "256m", "--pids-limit", "128",
                "--entrypoint", FFPROBE, "-v", f"{output}:/export:ro", self.args.image, "-v", "error", "-show_streams", "-of", "json", "/export/selected.mkv"))
            streams = probe["streams"]
            audio = [row for row in streams if row["codec_type"] == "audio"]
            subtitles = [row for row in streams if row["codec_type"] == "subtitle"]
            require(len(audio) == 1 and audio[0].get("tags", {}).get("language") in ("fr", "fra", "fre"), "Export retained the wrong audio language")
            require(len(subtitles) == 1 and subtitles[0].get("tags", {}).get("language") in ("en", "eng"), "Export retained the wrong subtitle language")
            self.docker("run", "--rm", "--network", "none", "--cpus", "1", "--memory", "512m", "--pids-limit", "128", "--entrypoint", FFMPEG,
                "-v", f"{output}:/export:ro", self.args.image, "-v", "error", "-i", "/export/selected.mkv", "-map", "0:v", "-map", "0:a", "-f", "null", "-", timeout=120)
            evidence.update(audio="fr", subtitles="en", decoded=True)
        with self.step("native-season-batch-and-utc-window") as evidence:
            preview = self.alice.call("POST", "/Siphon/Downloads/BatchPreview", {"ItemId": self.series})
            require(preview["Items"] and not preview["Truncated"], "Native series preview did not expose generated episodes")
            selections = [{"ItemId": row["ItemId"],
                           "MediaSourceId": next(source["MediaSourceId"] for source in row["Sources"] if source["Name"].endswith(" HTTP"))}
                          for row in preview["Items"]]
            request = {"Token": preview["Token"], "Items": selections}
            self.bob.call("POST", "/Siphon/Downloads/Batch", request, expected=(403, 409))
            config = self.config()
            hour = time.gmtime().tm_hour
            config.update(DownloadWindowEnabled=True, DownloadWindowStartUtcHour=(hour + 6) % 24, DownloadWindowEndUtcHour=(hour + 7) % 24)
            self.save_config(config)
            result = self.alice.call("POST", "/Siphon/Downloads/Batch", request)
            require(not result["Rejected"] and len(result["Jobs"]) == len(selections), "Native episode batch was not accepted completely")
            for job in result["Jobs"]:
                waiting = self.job(self.alice, job["Id"])
                require(waiting["State"] == "Queued" and waiting["Phase"] == "WaitingWindow" and waiting["NextEligibleUtc"],
                        "Closed UTC window did not defer the queued writer")
            self.alice.call("POST", "/Siphon/Downloads/Batch", request, expected=(409,))
            config["DownloadWindowEnabled"] = False
            self.save_config(config)
            for job in result["Jobs"]:
                completed = self.complete_job(self.alice, job["Id"])
                require(any(completed["ItemId"] == selection["ItemId"] and completed["MediaSourceId"] == selection["MediaSourceId"] for selection in selections),
                        "Batch worker substituted the selected episode version")
            evidence.update(episodes=len(selections), ownerBoundPreview=True, singleUsePreview=True, windowReopened=True)

    def notifications(self):
        def observation():
            paths = list((self.temp / "config").glob("**/siphon/calendar-notifications.json"))
            require(len(paths) == 1, "Owned notification cursor path is ambiguous")
            users = json.loads(paths[0].read_text())["Users"]
            cursor = next(value for key, value in users.items() if uuid.UUID(key) == uuid.UUID(self.alice.user_id))
            calendar = self.alice.call("GET", "/Siphon/Calendar?from=2020-01-01&to=2020-01-31")["Items"]
            return {
                "observedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "cursorUpdatedUtc": cursor["UpdatedUtc"], "enabled": cursor["Enabled"],
                "observedEpisodes": sum(uuid.UUID(row["SeriesId"]) == uuid.UUID(self.series) for row in cursor["Observations"]),
                "calendarEpisodes": [row["EpisodeNumber"] for row in calendar if uuid.UUID(row["SeriesId"]) == uuid.UUID(self.series)],
                "inbox": [{"kind": row["Kind"], "episode": row["Episode"]["EpisodeNumber"]} for row in cursor["Inbox"]]
            }

        with self.step("signed-notification-adapters") as evidence:
            self.alice.call("POST", f"/Users/{self.alice.user_id}/FavoriteItems/{self.series}")
            self.alice.call("PUT", "/Siphon/Preferences", {"SearchMode": "Inherit", "NotificationsEnabled": True})
            initial_episode = next(row["Id"] for row in self.items("Episode")
                if uuid.UUID(row["SeriesId"]) == uuid.UUID(self.series) and row["IndexNumber"] == 1)

            def baseline_ready():
                # The observer deliberately suppresses the first snapshot as historical
                # backlog. Adapter-test duration is not proof that this snapshot exists.
                paths = list((self.temp / "config").glob("**/siphon/calendar-notifications.json"))
                if not paths:
                    return False
                require(len(paths) == 1, "Owned notification baseline path is ambiguous")
                users = json.loads(paths[0].read_text())["Users"]
                state = next((value for key, value in users.items() if uuid.UUID(key) == uuid.UUID(self.alice.user_id)), None)
                return state is not None and state["Enabled"] and any(
                    uuid.UUID(row["Id"]) == uuid.UUID(initial_episode) for row in state["Observations"])

            eventually(baseline_ready, timeout=150, description="durable followed-series baseline before adding an episode")
            evidence["initialEpisodeObserved"] = True
            counts = {}
            last_test = None
            for adapter in ("Json", "Discord", "Slack", "Ntfy"):
                self.alice.call("PUT", "/Siphon/Notifications/Webhook", {"Enabled": True, "Url": "http://fixture:8080/receiver/" + adapter,
                    "Adapter": adapter, "Kinds": ["NewEpisode", "DateChanged", "AnnouncedRelease"], "DigestMinutes": 0})
                secret = self.alice.call("POST", "/Siphon/Notifications/Webhook/Secret")["Secret"]
                before = len(self.fixture_api.call("GET", "/control/events"))
                if last_test is not None:
                    # The real per-user one-minute test limit must not be bypassed by adapter changes.
                    time.sleep(max(0, 61 - (time.monotonic() - last_test)))
                result = self.alice.call("POST", "/Siphon/Notifications/Webhook/Test")
                last_test = time.monotonic()
                require(result["Success"], "Notification test reported non-delivery")
                events = eventually(lambda: self.fixture_api.call("GET", "/control/events"), lambda rows: len(rows) > before, description="local adapter receiver delivery")
                event = events[-1]
                require(event["adapter"] == adapter and event["eventId"], "Receiver lost adapter routing or event identity")
                signature = "sha256=" + hmac.new(base64.b64decode(secret), event["body"].encode(), hashlib.sha256).hexdigest()
                require(hmac.compare_digest(event["signature"] or "", signature), "Receiver signature does not match exact payload bytes")
                body = json.loads(event["body"])
                required = {"Json": "Type", "Discord": "content", "Slack": "text", "Ntfy": "message"}[adapter]
                require(required in body, "Adapter payload is not receiver-specific")
                counts[adapter] = len(events) - before
            status = self.alice.call("GET", "/Siphon/Notifications/Webhook")
            require(status["LastSuccessUtc"] and status["LastOutcome"] == "Delivered", "Notification status did not record successful delivery")
            evidence.update(received=counts, exactBodySignatures=True)
        with self.step("native-followed-series-event-and-private-inbox") as evidence:
            self.alice.call("PUT", "/Siphon/Notifications/Webhook", {"Enabled": True,
                "Url": "http://fixture:8080/receiver/Json", "Adapter": "Json", "Kinds": ["NewEpisode"], "DigestMinutes": 0})
            secret = self.alice.call("POST", "/Siphon/Notifications/Webhook/Secret")["Secret"]
            require(not self.alice.call("GET", "/Siphon/Notifications")["Items"], "Opt-in unexpectedly delivered historical backlog")
            before = len(self.fixture_api.call("GET", "/control/events"))
            evidence["beforePublication"] = observation()
            self.fixture_api.call("POST", "/control/episodes", {"count": 2})
            self.sync()
            eventually(lambda: self.items("Episode"),
                lambda rows: any(uuid.UUID(row["SeriesId"]) == uuid.UUID(self.series) and row["IndexNumber"] == 2 for row in rows),
                description="second native episode publication")
            evidence["secondEpisodePublished"] = True
            evidence["afterPublication"] = observation()
            try:
                inbox = eventually(lambda: self.alice.call("GET", "/Siphon/Notifications"),
                    lambda value: any(row["Kind"] == "NewEpisode" and row["Episode"]["EpisodeNumber"] == 2 for row in value["Items"]),
                    timeout=150, description="native followed-series notification")
            finally:
                evidence["afterObservation"] = observation()
            require(not self.bob.call("GET", "/Siphon/Notifications")["Items"], "Private notification appeared for an unfollowing user")
            events = eventually(lambda: self.fixture_api.call("GET", "/control/events"), lambda rows: len(rows) > before,
                                timeout=90, description="actual calendar event webhook")
            event = events[-1]
            body = json.loads(event["body"])
            require(body["Type"] == "calendar.notification" and body["Kind"] == "NewEpisode" and body["Test"] is False,
                    "Receiver did not receive the real native event")
            require(uuid.UUID(body["EventId"]) in {uuid.UUID(row["Id"]) for row in inbox["Items"]},
                    "Webhook event does not correspond to private native inbox")
            signature = "sha256=" + hmac.new(base64.b64decode(secret), event["body"].encode(), hashlib.sha256).hexdigest()
            require(hmac.compare_digest(event["signature"] or "", signature), "Actual event signature is invalid")
            evidence.update(realCalendarEvent=True, inboxPrivate=True, exactBodySignature=True)

    def segments(self):
        with self.step("native-media-segments-and-protected-chapters") as evidence:
            selected = self.source(self.alice, " HTTP")
            info = self.alice.call("POST", f"/Items/{self.movie}/PlaybackInfo?UserId={self.alice.user_id}",
                                  {"UserId": self.alice.user_id, "MediaSourceId": selected["MediaSourceId"]})
            source = next(row for row in info["MediaSources"] if row["Id"] == selected["MediaSourceId"])
            opened = self.alice.call("POST", "/LiveStreams/Open", {"OpenToken": source["OpenToken"],
                "ItemId": self.movie, "UserId": self.alice.user_id, "PlaySessionId": info.get("PlaySessionId")})["MediaSource"]
            try:
                require(opened["Id"] == selected["MediaSourceId"], "Native segment probe substituted source identity")
                runtime = opened.get("RunTimeTicks", 0)
                require(390000000 <= runtime <= 410000000, "Native probe did not observe the generated forty-second runtime")
                # Jellyfin persists probe results on the selected native version, not on an arbitrary catalog DTO.
                item_id = opened["Id"]
                detail = self.alice.call("GET", f"/Users/{self.alice.user_id}/Items/{item_id}")
                require(detail.get("RunTimeTicks") == runtime, "Selected native version did not persist the probed runtime")
                inspection = self.alice.call("GET", f"/Siphon/Items/{item_id}/Segments")
                require(inspection["Status"] == "Available", "Controlled IntroDB timings were not available")
                native = self.alice.call("GET", "/MediaSegments/" + item_id)
                rows = native["Items"] if isinstance(native, dict) else native
                require(any(row["Type"] == "Intro" and row["StartTicks"] == 10000000 and row["EndTicks"] == 30000000 for row in rows), "Native MediaSegments API does not expose generated Intro")
                require(all(not (row["StartTicks"] < 390000000 and row["EndTicks"] > 360000000) for row in rows), "Skip segment overlaps protected post-credit scene")
                detail = self.alice.call("GET", f"/Users/{self.alice.user_id}/Items/{item_id}")
                require(any(chapter["StartPositionTicks"] == 360000000 for chapter in detail.get("Chapters", [])), "Protected post-credit scene is not a native chapter")
                evidence.update(nativeIntro=True, protectedPostCredits=True, nativeProbedRuntimeTicks=runtime)
            finally:
                if opened.get("RequiresClosing") and opened.get("LiveStreamId"):
                    self.alice.call("POST", "/LiveStreams/Close?" + urllib.parse.urlencode({"LiveStreamId": opened["LiveStreamId"]}))

    def ui(self):
        with self.step("browser-native-admin-and-personal-paths") as evidence:
            if not shutil.which("node"):
                raise Unavailable("Node.js and scripts/smoke browser dependencies are required for ui/full")
            process = subprocess.run(["node", str(ROOT / "scripts/smoke/browser.mjs")], input=json.dumps({
                "base": self.api.base, "password": self.password, "userId": self.alice.user_id,
                "output": str(self.temp / "browser"),
                "chromiumExecutable": str(self.args.chromium_executable.resolve()) if self.args.chromium_executable else None}),
                text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=180)
            if process.returncode == 77:
                raise Unavailable("Playwright/browser unavailable; install scripts/smoke dependencies and the pinned Chromium, or supply --chromium-executable with an installed Chromium binary")
            stage = process.stderr.strip().removeprefix("SMOKE_STAGE=")
            safe_stage = stage if stage and len(stage) < 80 and all(character.isalpha() or character == "-" for character in stage) else "unknown"
            require(process.returncode == 0, "Browser scenario failed at " + safe_stage + "; raw browser output withheld")
            evidence.update(json.loads(process.stdout))
            if self.args.screenshots:
                destination = self.args.screenshots.resolve()
                destination.mkdir(parents=True, exist_ok=True)
                files = sorted((self.temp / "browser").glob("*.png"))
                require(len(files) == 4, "Browser did not produce all four viewport screenshots")
                for image in files:
                    shutil.copyfile(image, destination / image.name)
                evidence["screenshots"] = [{"name": image.name, "sha256": hashlib.sha256(image.read_bytes()).hexdigest()} for image in files]

    def previous_package(self):
        with self.step("locate-real-1.4.1-binary") as evidence:
            if self.args.previous_package:
                package = self.args.previous_package.resolve()
                origin = "provided-archive"
            else:
                origin = "published-v1.4.1-release"
                try:
                    request = urllib.request.Request("https://api.github.com/repos/moodiness/jellyfin-plugin-siphon/releases/tags/v1.4.1", headers={"User-Agent": "siphon-owned-smoke"})
                    with urllib.request.urlopen(request, timeout=30) as response:
                        release = json.load(response)
                    asset = next(asset for asset in release["assets"] if asset["name"] == "siphon-1.4.1.0.zip")
                    package = self.temp / "siphon-1.4.1.0.zip"
                    with urllib.request.urlopen(asset["browser_download_url"], timeout=60) as response:
                        data = response.read(128 * 1024 * 1024 + 1)
                    require(len(data) <= 128 * 1024 * 1024, "Prior release archive exceeds limit")
                    require(hashlib.sha256(data).hexdigest() == "7b7ee5f591272133bd2662e6df518c6d0c722f32339e1b563126cc240347ce60",
                            "Published v1.4.1 archive differs from the verified historical binary")
                    package.write_bytes(data)
                except (urllib.error.URLError, KeyError, StopIteration) as error:
                    raise Unavailable("Real v1.4.1 release ZIP unavailable; supply --previous-package with a verified 1.4.1.0 historical binary. Upgrade was NOT exercised.") from error
            require(self.archive_metadata(package)["version"] == "1.4.1.0", "Upgrade must begin with a real 1.4.1.0 archive")
            require(hashlib.sha256(package.read_bytes()).hexdigest() == "7b7ee5f591272133bd2662e6df518c6d0c722f32339e1b563126cc240347ce60",
                    "Previous package is not the verified published v1.4.1 binary")
            evidence.update(origin=origin, version="1.4.1.0", sha256=hashlib.sha256(package.read_bytes()).hexdigest())
            return package

    def seed_upgrade(self):
        with self.step("seed-native-1.4.1-user-state") as evidence:
            self.alice.call("POST", f"/Users/{self.alice.user_id}/FavoriteItems/{self.series}")
            # Seed persisted user data through Jellyfin's real 12.1 API, not a simulated playback session.
            self.alice.call("POST", f"/UserItems/{self.movie}/UserData?userId={self.alice.user_id}", {
                "PlaybackPositionTicks": 120000000, "PlayCount": 2, "IsFavorite": True,
                "LastPlayedDate": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "Played": False})
            native_user = self.alice.call("GET", "/Users/" + self.alice.user_id)
            native_preferences = native_user["Configuration"]
            native_preferences.update(AudioLanguagePreference="fr", SubtitleLanguagePreference="en", PlayDefaultAudioTrack=False)
            self.alice.call("POST", f"/Users/{self.alice.user_id}/Configuration", native_preferences)
            collection = self.admin.call("POST", "/Collections?" + urllib.parse.urlencode({"Name": "Siphon Smoke Manual Collection", "Ids": self.movie, "IsLocked": "true"}))
            self.upgrade_collection = collection["Id"]
            self.upgrade_items = {row["Id"] for row in self.items()}
            self.upgrade_config = self.config()
            self.upgrade_preferences = self.alice.call("GET", "/Users/" + self.alice.user_id)["Configuration"]
            before = self.alice.call("GET", f"/Users/{self.alice.user_id}/Items/{self.movie}")["UserData"]
            require(before["IsFavorite"] and before["PlaybackPositionTicks"] == 120000000 and before["PlayCount"] == 2, "Prior binary/native server did not persist upgrade seed")
            require(before.get("LastPlayedDate") and before["Played"] is False, "Prior native server did not persist last-played/unplayed seed")
            self.upgrade_user_data = {key: before[key] for key in ("IsFavorite", "PlaybackPositionTicks", "PlayCount", "LastPlayedDate", "Played")}
            evidence.update(seedMethod="native-user-data-api", completedPlaybackSimulated=False)

    def upgrade(self):
        with self.step("upgrade-real-binary-preserving-native-state") as evidence:
            self.stop()
            self.install_archive(self.current_package)
            self.start()
            self.relogin()
            plugins = self.admin.call("GET", "/Plugins")
            require(any(uuid.UUID(row["Id"]) == uuid.UUID(PLUGIN) and row["Version"] == self.current_version for row in plugins), "New binary did not load after upgrade")
            self.sync()
            require(self.upgrade_items <= {row["Id"] for row in self.items()}, "Upgrade changed or dropped native identities")
            data = self.alice.call("GET", f"/Users/{self.alice.user_id}/Items/{self.movie}")["UserData"]
            require(data["IsFavorite"] and data["PlaybackPositionTicks"] == 120000000 and data["PlayCount"] == 2, "Upgrade lost native favorite/resume/history")
            require(all(data.get(key) == value for key, value in self.upgrade_user_data.items()), "Upgrade changed persisted native user data")
            config = self.config()
            def retains(previous, current):
                if isinstance(previous, dict):
                    return isinstance(current, dict) and all(key in current and retains(value, current[key]) for key, value in previous.items())
                if isinstance(previous, list):
                    return isinstance(current, list) and len(previous) == len(current) and all(retains(a, b) for a, b in zip(previous, current))
                return previous == current
            for key in ("Addons", "AllowedPrivateHosts", "MetadataUpdateMode", "MetadataCacheHours"):
                require(retains(self.upgrade_config[key], config[key]), "Upgrade lost historical plugin settings")
            require(self.alice.call("GET", "/Users/" + self.alice.user_id)["Configuration"] == self.upgrade_preferences, "Upgrade lost native personal settings")
            collection = self.alice.call("GET", "/Items?" + urllib.parse.urlencode({"ParentId": self.upgrade_collection, "UserId": self.alice.user_id}))
            require(any(row["Id"] == self.movie for row in collection["Items"]), "Upgrade lost manual native collection membership")
            evidence.update(previousVersion="1.4.1.0", installedVersion=self.current_version, preserved=["native-identities", "favorites", "resume", "play-count", "manual-collection", "addon-settings", "native-personal-settings"])
            # Profiles and Siphon preferences were introduced after 1.4.1; do not pretend to migrate nonexistent state.
            self.operations_config(config)
            self.save_config(config)

    def cleanup(self):
        errors = []
        for name in reversed(self.containers):
            try:
                self.docker("rm", "-f", name, timeout=45)
            except Exception:
                errors.append("owned-container-cleanup-failed")
        if self.network_created:
            try:
                self.docker("network", "rm", self.name, timeout=30)
            except Exception:
                errors.append("owned-network-cleanup-failed")
        try:
            shutil.rmtree(self.temp)
        except OSError:
            errors.append("owned-temporary-data-cleanup-failed")
        return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__, epilog="Exit 0=all selected assertions passed; 1=failure; 77=explicit unavailable prerequisite/history. Full includes real upgrade and Chromium UI. No external addon/account is used.")
    parser.add_argument("scenario", choices=SCENARIOS)
    parser.add_argument("--package", type=Path, help="Current package ZIP; omitted builds Release and packages in an owned temporary directory")
    parser.add_argument("--previous-package", type=Path, help="Real 1.4.1.0 package for upgrade; otherwise fetch the published v1.4.1 ZIP")
    parser.add_argument("--image", default="jellyfin/jellyfin:12.1", help="Jellyfin 12.1 image/tag or digest; actual server version is checked")
    parser.add_argument("--evidence", type=Path, help="Credential-free JSON output (default artifacts/smoke-<unique>.json)")
    parser.add_argument("--screenshots", type=Path, help="Retain credential-free successful admin/personal screenshots at two viewport widths")
    parser.add_argument("--chromium-executable", type=Path, help="Explicit Chromium/Chrome binary for ui/full; default is pinned Playwright Chromium. Uses a fresh isolated browser profile.")
    args = parser.parse_args()
    harness = Harness(args)
    evidence = args.evidence or ROOT / "artifacts" / (harness.name + ".json")
    status, code, failure = "failed", 1, None
    def interrupted(_signum, _frame):
        raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, interrupted)
    try:
        harness.prerequisites()
        harness.prepare_package()
        old = harness.previous_package() if args.scenario in ("upgrade", "full") else None
        harness.fixture()
        version = harness.install_archive(old or harness.current_package)
        harness.bootstrap(version)
        harness.configure(legacy=bool(old))
        if old:
            harness.seed_upgrade()
            harness.upgrade()
        harness.health()
        if args.scenario in ("playback", "full"):
            harness.playback()
        if args.scenario in ("exports", "full"):
            harness.exports()
        if args.scenario in ("notifications", "full"):
            harness.notifications()
        if args.scenario in ("segments", "full"):
            harness.segments()
        if args.scenario in ("ui", "full"):
            harness.ui()
        status, code = "passed", 0
    except Unavailable as error:
        status, code, failure = "skipped", 77, str(error)
    except KeyboardInterrupt:
        status, code, failure = "interrupted", 130, "Interrupted; cleaning owned runtime"
    except Exception as error:
        failure = failure_reason(error)
    finally:
        cleanup_errors = harness.cleanup()
        if cleanup_errors:
            status, code = "failed", 1
        evidence.parent.mkdir(parents=True, exist_ok=True)
        evidence.write_text(json.dumps({"schemaVersion": 1, "scenario": args.scenario, "status": status, "reason": failure,
            "image": args.image, "steps": harness.steps, "cleanupErrors": cleanup_errors}, indent=2) + "\n", encoding="utf-8")
        print(f"{status}: {evidence}", flush=True)
        if failure:
            print(failure, file=sys.stderr)
    return code


if __name__ == "__main__":
    sys.exit(main())
