#!/usr/bin/env python3
"""Owned native-reader boundary experiment; no personal services or external media.

1. Start under the harness process supervisor:
   python3 tests/Jellyfin.Plugin.Siphon.Tests/Playback/native_reader_smoke.py serve
   The first JSON line gives an ephemeral loopback origin and a control secret.
2. Baseline: save that JSON to origin.json, then run:
   python3 tests/Jellyfin.Plugin.Siphon.Tests/Playback/native_reader_smoke.py probe origin.json
3. Isolated Jellyfin: allow only that exact origin host, add its /manifest.json,
   and import the fixture catalog. As a fixture user, resolve EACH native selected
   version. Make a copy of origin.json replacing sources values with that version's
   /Siphon/media/<token>/stream... URL, keeping origin/control unchanged. Run probe
   on the copy. Do not put a Jellyfin API key in these media URLs or the report.

The script never creates a Jellyfin user, changes an instance, or guesses a source.
For Docker Desktop, serve with --reference-host host.docker.internal, then probe
with --container <owned-name>. The container must carry the explicit label
org.siphon.owned-fixture=true. Native root URLs are translated from host loopback
to the Docker host address; observation requests remain on host loopback.
Every reference points only to this owned origin. No custom FFmpeg protocol
whitelist or forced demuxer is passed: those would change the boundary being tested.
A failed probe is NOT proof of confinement: inspect recorded callback requests.
Reports contain no media capability URLs. For actual Jellyfin encoder evidence,
also open the native versions via PlaybackInfo, and compare the owned origin log.
"""

import argparse
import collections
import http.server
import json
import pathlib
import secrets
import shutil
import signal
import subprocess
import tempfile
import threading
import urllib.error
import urllib.parse
import urllib.request


CASES = ("mp4", "hls", "dash", "smil", "ffconcat", "reference-playlist")


def command(arguments, timeout=30):
    try:
        result = subprocess.run(arguments, capture_output=True, timeout=timeout, check=False)
        return {"exit": result.returncode, "stdout": result.stdout.decode("utf-8", "replace"),
                "stderr": result.stderr.decode("utf-8", "replace")}
    except subprocess.TimeoutExpired:
        return {"exit": None, "timeout": True, "stdout": "", "stderr": ""}


def generate(directory, ffmpeg):
    common = [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-y"]
    movie = directory / "fixture.mp4"
    jobs = [common + ["-f", "lavfi", "-i", "color=c=blue:s=160x90:r=12", "-f", "lavfi", "-i",
                      "sine=frequency=440:sample_rate=48000", "-t", "2", "-c:v", "libx264", "-preset",
                      "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", str(movie)],
            common + ["-i", str(movie), "-c", "copy", "-f", "hls", "-hls_time", "1", "-hls_list_size", "0",
                      "-hls_segment_filename", str(directory / "segment%d.ts"), str(directory / "fixture.m3u8")],
            common + ["-i", str(movie), "-c", "copy", "-f", "dash", "-seg_duration", "1", str(directory / "fixture.mpd")]]
    for job in jobs:
        result = command(job)
        if result["exit"] != 0:
            raise RuntimeError("Owned media generation failed: " + result["stderr"])


def serve(args):
    def stop(_signal, _frame):
        raise KeyboardInterrupt()

    signal.signal(signal.SIGTERM, stop)
    ffmpeg = shutil.which(args.ffmpeg)
    if not ffmpeg:
        raise RuntimeError("FFmpeg is required; supply --ffmpeg with the isolated server's binary.")
    with tempfile.TemporaryDirectory(prefix="siphon-native-reader-") as temporary:
        directory = pathlib.Path(temporary)
        generate(directory, ffmpeg)
        events = collections.deque(maxlen=10000)
        gate = threading.Lock()
        control = secrets.token_hex(24)
        fixtures = {}

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_HEAD(self):
                self.do_GET()

            def do_GET(self):
                parsed = urllib.parse.urlsplit(self.path)
                path = parsed.path
                if path == "/observations":
                    if self.headers.get("X-Fixture-Control") != control:
                        self.send_error(403)
                        return
                    with gate:
                        body = json.dumps(list(events)).encode()
                        events.clear()
                    self.reply(body, "application/json")
                    return
                with gate:
                    events.append({"method": self.command, "path": path,
                                   "range": self.headers.get("Range"), "agent": self.headers.get("User-Agent", "")[:200]})
                if path in fixtures:
                    self.reply(fixtures[path], "application/octet-stream")
                elif path.startswith("/callback/"):
                    name = pathlib.PurePosixPath(path).name
                    if name not in generated:
                        self.send_error(404)
                    else:
                        self.reply(generated[name], "application/octet-stream")
                elif path == "/manifest.json":
                    self.json_reply({"id": "org.siphon.owned.native-reader", "version": "1.0.0", "name": "Owned native reader fixtures",
                                     "description": "Isolated locally generated boundary experiment", "types": ["movie"],
                                     "resources": ["catalog", "meta", "stream"], "catalogs": [{"type": "movie", "id": "owned"}]})
                elif path == "/catalog/movie/owned.json":
                    self.json_reply({"metas": [meta(case) for case in CASES]})
                elif path.startswith("/meta/movie/") and path.endswith(".json"):
                    case = path.removeprefix("/meta/movie/").removesuffix(".json").removeprefix("siphon-native-")
                    self.json_reply({"meta": meta(case)}) if case in CASES else self.send_error(404)
                elif path.startswith("/stream/movie/") and path.endswith(".json"):
                    case = path.removeprefix("/stream/movie/").removesuffix(".json").removeprefix("siphon-native-")
                    if case not in CASES:
                        self.send_error(404)
                    else:
                        self.json_reply({"streams": [{"name": "Owned " + case, "url": media_origin + "/media/" + case,
                                                      "behaviorHints": {"filename": "owned-" + case + ".mp4"}}]})
                else:
                    self.send_error(404)

            def json_reply(self, value):
                self.reply(json.dumps(value).encode(), "application/json")

            def reply(self, body, media_type):
                start, end = 0, len(body) - 1
                status = 200
                value = self.headers.get("Range")
                if value:
                    try:
                        unit, value = value.split("=", 1)
                        first, last = value.split("-", 1)
                        if unit != "bytes" or "," in value:
                            raise ValueError()
                        if first:
                            start = int(first)
                            end = min(int(last) if last else end, end)
                        else:
                            amount = int(last)
                            if amount <= 0:
                                raise ValueError()
                            start = max(0, len(body) - amount)
                        if start < 0 or start > end:
                            raise ValueError()
                        status = 206
                    except ValueError:
                        self.send_response(416)
                        self.send_header("Content-Range", "bytes */" + str(len(body)))
                        self.send_header("Content-Length", "0")
                        self.end_headers()
                        return
                self.send_response(status)
                self.send_header("Content-Type", media_type)
                self.send_header("Content-Length", str(end - start + 1))
                self.send_header("Accept-Ranges", "bytes")
                if status == 206:
                    self.send_header("Content-Range", f"bytes {start}-{end}/{len(body)}")
                self.end_headers()
                if self.command != "HEAD":
                    try:
                        self.wfile.write(body[start:end + 1])
                    except (BrokenPipeError, ConnectionResetError):
                        pass

        def meta(case):
            return {"id": "siphon-native-" + case, "type": "movie", "name": "Owned native reader " + case,
                    "description": "Locally generated fixture, not external media.", "releaseInfo": "2026"}

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        origin = "http://127.0.0.1:" + str(server.server_port)
        media_origin = "http://" + args.reference_host + ":" + str(server.server_port)
        generated = {entry.name: entry.read_bytes() for entry in directory.iterdir() if entry.is_file()}
        fixtures["/media/mp4"] = generated["fixture.mp4"]
        playlist = generated["fixture.m3u8"].decode()
        fixtures["/media/hls"] = "\n".join(media_origin + "/callback/hls/" + line if line and not line.startswith("#") else line
                                                  for line in playlist.splitlines()).encode()
        dash = generated["fixture.mpd"].decode().replace('initialization="', 'initialization="' + media_origin + '/callback/dash/').replace('media="', 'media="' + media_origin + '/callback/dash/')
        fixtures["/media/dash"] = dash.encode()
        fixtures["/media/smil"] = ('<?xml version="1.0"?><smil><body><switch><video src="' + media_origin + '/callback/smil/fixture.mp4"/></switch></body></smil>').encode()
        fixtures["/media/ffconcat"] = ("ffconcat version 1.0\nfile '" + media_origin + "/callback/ffconcat/fixture.mp4'\n").encode()
        fixtures["/media/reference-playlist"] = ("#EXTM3U\n#EXTINF:2,Owned\n" + media_origin + "/callback/reference-playlist/fixture.mp4\n").encode()
        print(json.dumps({"origin": origin, "control": control, "sources": {case: origin + "/media/" + case for case in CASES}}), flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass
        finally:
            server.server_close()


def probe(args):
    settings = json.loads(pathlib.Path(args.mapping).read_text())
    origin = settings["origin"]
    parsed = urllib.parse.urlsplit(origin)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.path not in ("", "/") or parsed.username:
        raise ValueError("The observation endpoint must be the owned loopback HTTP origin.")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    docker = shutil.which("docker") if args.container else None
    if args.container:
        if not docker:
            raise RuntimeError("Docker is required for --container.")
        identity = command([docker, "inspect", "--format", '{{ index .Config.Labels "org.siphon.owned-fixture" }}', args.container])
        if identity["exit"] != 0 or identity["stdout"].strip() != "true":
            raise ValueError("The named container is not explicitly marked as an owned Siphon fixture.")

    def observations():
        request = urllib.request.Request(origin + "/observations", headers={"X-Fixture-Control": settings["control"]})
        with opener.open(request, timeout=5) as response:
            return json.load(response)

    report = {"scope": "Owned fixtures only; no general native-reader confinement claim.", "results": []}
    for case in CASES:
        url = settings["sources"][case]
        parsed_url = urllib.parse.urlsplit(url)
        if parsed_url.scheme not in ("http", "https") or parsed_url.hostname not in ("127.0.0.1", "localhost", "::1") or parsed_url.username:
            raise ValueError("Source URLs must point at the isolated server on loopback.")
        if args.container:
            url = urllib.parse.urlunsplit(parsed_url._replace(netloc="host.docker.internal:" + str(parsed_url.port or (443 if parsed_url.scheme == "https" else 80))))
        for tool in ("ffprobe", "ffmpeg"):
            observations()
            executable = docker if args.container else shutil.which(getattr(args, tool))
            if not executable:
                raise RuntimeError(tool + " is required.")
            prefix = [executable, "exec", args.container, "/usr/lib/jellyfin-ffmpeg/" + tool] if args.container else [executable]
            common = prefix + ["-hide_banner", "-loglevel", "error", "-rw_timeout", "5000000"]
            arguments = (common + ["-count_frames", "-show_entries", "stream=codec_name,nb_read_frames", "-of", "json", url]
                         if tool == "ffprobe" else common + ["-nostdin", "-i", url, "-t", "2", "-f", "null", "-"])
            outcome = command(arguments, timeout=20)
            # Native stderr can contain media capabilities. Keep counts, not raw diagnostics.
            report["results"].append({"case": case, "tool": tool, "exit": outcome["exit"],
                                      "timeout": outcome.get("timeout", False), "stderrBytes": len(outcome["stderr"].encode()),
                                      "streams": json.loads(outcome["stdout"]).get("streams", []) if tool == "ffprobe" and outcome["exit"] == 0 else [],
                                      "originRequests": observations()})
    serialized = json.dumps(report, indent=2)
    if args.output:
        target = pathlib.Path(args.output)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(serialized + "\n", encoding="utf-8")
    print(serialized)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    actions = parser.add_subparsers(dest="action", required=True)
    server = actions.add_parser("serve")
    server.add_argument("--ffmpeg", default="ffmpeg")
    server.add_argument("--reference-host", choices=("127.0.0.1", "host.docker.internal"), default="127.0.0.1")
    client = actions.add_parser("probe")
    client.add_argument("mapping")
    client.add_argument("--ffmpeg", default="ffmpeg")
    client.add_argument("--ffprobe", default="ffprobe")
    client.add_argument("--container", help="Explicitly labelled owned Jellyfin container supplying its native media tools")
    client.add_argument("--output", help="Write the credential-free observations report to this path")
    args = parser.parse_args()
    serve(args) if args.action == "serve" else probe(args)


if __name__ == "__main__":
    main()
