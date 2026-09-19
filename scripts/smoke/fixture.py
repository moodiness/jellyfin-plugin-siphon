#!/usr/bin/env python3
"""Owned, offline Stremio/media/IntroDB/webhook fixture; never a public service."""
import hashlib
import json
import os
from pathlib import Path
import re
import ssl
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlsplit

ROOT = Path(os.environ.get("FIXTURE_ROOT", "/fixture"))
LOCK = threading.Lock()
EVENTS = []
EPISODES = 1
STREAMS_ENABLED = True
COUNTERS = {"addon": 0, "media": 0, "segments": 0, "receiver": 0}
BASE = "http://fixture:8080"


def movie(identifier="tt9910001"):
    return {"id": identifier, "type": "movie", "name": "Siphon Generated Discovery" if identifier.endswith("2") else "Siphon Generated Movie",
            "imdb_id": identifier, "description": "Locally generated color and sine-wave media. No third-party media.",
            "releaseInfo": "2020", "released": "2020-01-01T00:00:00Z", "runtime": "1 min"}


def series(identifier="tt9910010", *, catalog=False):
    discovery = identifier == "tt9910011"
    item = {"id": identifier, "imdb_id": identifier, "type": "series",
            "name": "Siphon Generated Discovery Series" if discovery else "Siphon Generated Series",
            "releaseInfo": "2020"}
    if not catalog:
        item["videos"] = [{"id": f"{identifier}:1:{n}", "title": f"Generated Episode {n}",
                          "season": 1, "episode": n, "released": f"2020-01-{n:02d}T00:00:00Z"}
                         for n in range(1, EPISODES + 1)]
        if discovery:
            item["poster"] = BASE + "/media/series-poster.png"
    return item


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass  # Requests can contain capabilities: do not log them.

    def reply(self, status, body=b"", content_type="application/json", headers=None):
        if not isinstance(body, bytes):
            body = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        for key, value in (headers or {}).items():
            self.send_header(key, str(value))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        path = unquote(urlsplit(self.path).path)
        if path == "/control/counters":
            with LOCK:
                return self.reply(200, dict(COUNTERS))
        category = "addon" if path.startswith(("/shared/", "/personal/")) else "media" if path.startswith("/media/") else "segments" if path == "/segments" else None
        if category:
            with LOCK:
                COUNTERS[category] += 1
        if path == "/health":
            return self.reply(200, {"ready": True})
        if path == "/control/events":
            with LOCK:
                return self.reply(200, list(EVENTS))
        if path == "/segments":
            query = parse_qs(urlsplit(self.path).query)
            result = {"imdb_id": query.get("imdb_id", [""])[0], "is_movie": query.get("is_movie") == ["true"],
                      "intro": {"start_ms": 1000, "end_ms": 3000}, "outro": {"start_ms": 30000, "end_ms": 35000},
                      "post_credits": {"start_ms": 36000, "end_ms": 39000}}
            if not result["is_movie"]:
                result.update(season=int(query.get("season", [0])[0]), episode=int(query.get("episode", [0])[0]))
            return self.reply(200, result)
        match = re.fullmatch(r"/(shared|personal)/(.*)", path)
        if match:
            profile, resource = match.groups()
            if resource == "manifest.json":
                return self.reply(200, {"id": "org.siphon.smoke." + profile, "version": "1.0.0", "name": profile,
                    "description": "Owned generated fixture", "resources": ["catalog", "meta", "stream", "subtitles"],
                    "types": ["movie", "series"], "idPrefixes": ["tt991"], "catalogs": [
                    {"type": kind, "id": "generated", "name": "Generated", "extra": [{"name": "search", "isRequired": False}]} for kind in ("movie", "series")]})
            if resource.startswith("catalog/"):
                kind = resource.split("/")[1]
                searching = "search=" in resource
                if kind == "movie":
                    metas = [movie("tt9910002")] if searching else [movie()]
                else:
                    metas = [series("tt9910011", catalog=True)] if searching else [series()]
                return self.reply(200, {"metas": metas})
            if resource.startswith("meta/"):
                kind, identifier = resource.split("/")[1:3]
                identifier = identifier.removesuffix(".json")
                return self.reply(200, {"meta": series(identifier) if kind == "series" else movie(identifier)})
            if resource.startswith("stream/"):
                if not STREAMS_ENABLED:
                    return self.reply(200, {"streams": []})
                return self.reply(200, {"streams": [
                    {"name": profile + " HTTP", "url": BASE + "/media/movie.mp4", "behaviorHints": {"filename": "generated.mp4", "videoSize": (ROOT / "media/movie.mp4").stat().st_size}},
                    {"name": profile + " HLS", "url": BASE + "/media/master.m3u8"},
                    {"name": profile + " Slow", "url": BASE + "/media/slow.mp4"}]})
            if resource.startswith("subtitles/"):
                return self.reply(200, {"subtitles": [{"id": "generated-en", "lang": "eng", "url": BASE + "/media/en.vtt"}]})
        if path.startswith("/media/"):
            name = path.removeprefix("/media/")
            if "/" in name or name in ("", ".", ".."):
                return self.reply(404)
            target = ROOT / "media" / ("movie.mp4" if name == "slow.mp4" else name)
            if not target.is_file():
                return self.reply(404)
            data = target.read_bytes()
            tag = '"' + hashlib.sha256(data).hexdigest() + '"'
            headers = {"ETag": tag, "Accept-Ranges": "bytes"}
            status = 200
            range_value = self.headers.get("Range")
            if range_value and self.headers.get("If-Range", tag) == tag:
                match = re.fullmatch(r"bytes=(\d+)-(\d*)", range_value)
                if not match:
                    return self.reply(416, headers={"Content-Range": f"bytes */{len(data)}"})
                start, end = int(match[1]), int(match[2]) if match[2] else len(data) - 1
                if start > end or start >= len(data):
                    return self.reply(416, headers={"Content-Range": f"bytes */{len(data)}"})
                end = min(end, len(data) - 1)
                headers["Content-Range"] = f"bytes {start}-{end}/{len(data)}"
                data, status = data[start:end + 1], 206
            content_type = {".mp4": "video/mp4", ".m3u8": "application/vnd.apple.mpegurl", ".ts": "video/mp2t", ".vtt": "text/vtt", ".png": "image/png"}.get(target.suffix, "application/octet-stream")
            if name != "slow.mp4" or self.command == "HEAD":
                return self.reply(status, data, content_type, headers)
            self.send_response(status)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Content-Type", content_type)
            for key, value in headers.items():
                self.send_header(key, value)
            self.end_headers()
            try:
                for offset in range(0, len(data), 1024):
                    self.wfile.write(data[offset:offset + 1024])
                    self.wfile.flush()
                    time.sleep(.08)
            except (BrokenPipeError, ConnectionResetError):
                pass
            return
        self.reply(404)

    def do_POST(self):
        global EPISODES, STREAMS_ENABLED
        length = int(self.headers.get("Content-Length", 0))
        if not 0 <= length <= 1024 * 1024:
            return self.reply(413)
        body = self.rfile.read(length)
        path = urlsplit(self.path).path
        if path == "/control/streams":
            STREAMS_ENABLED = bool(json.loads(body)["enabled"])
            return self.reply(200, {"enabled": STREAMS_ENABLED})
        if path == "/control/episodes":
            EPISODES = max(1, min(5, int(json.loads(body)["count"])))
            return self.reply(200, {"count": EPISODES})
        if path.startswith("/receiver/"):
            # Kept only in ephemeral fixture memory. Runner exports counts/hashes, never secrets.
            with LOCK:
                COUNTERS["receiver"] += 1
                adapter = "Ntfy" if path == "/receiver/" else path.rsplit("/", 1)[-1]
                EVENTS.append({"adapter": adapter, "body": body.decode(),
                               "signature": self.headers.get("X-Siphon-Signature"),
                               "eventId": self.headers.get("X-Siphon-Event-Id")})
            return self.reply(200, b"ok", "text/plain")
        self.reply(404)


if __name__ == "__main__":
    http = ThreadingHTTPServer(("0.0.0.0", 8080), Handler)
    https = ThreadingHTTPServer(("0.0.0.0", 443), Handler)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(ROOT / "tls/cert.pem", ROOT / "tls/key.pem")
    https.socket = context.wrap_socket(https.socket, server_side=True)
    threading.Thread(target=https.serve_forever, daemon=True).start()
    http.serve_forever()
