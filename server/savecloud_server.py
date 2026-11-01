#!/usr/bin/env python3
"""SaveCloud server: stores game-save revisions per Tailscale user.

Every request is identified by the caller's Tailscale login (``tailscale whois`` on the connection's source
address), so PCs need no keys, passwords or pairing. Only logins listed in the allowlist file get access.

Two storage modes (--mode):
  shared    (default) every allowed login reads and writes the same saves
  per-user  each login has its own saves and can't see anyone else's

Python 3.9+ standard library only. Layout under --data:

    shared/games/<slug>/head.json             (or users/<login>/games/... in per-user mode)
    shared/games/<slug>/rev-000042.zip        one archive per revision, newest N kept
"""

import argparse
import hashlib
import http.server
import ipaddress
import json
import logging
import os
import re
import shutil
import socket
import socketserver
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse
from datetime import datetime, timezone

API_VERSION = 1
MAX_UPLOAD = 1024 * 1024 * 1024          # 1 GiB per upload
MAX_META = 16 * 1024 * 1024              # metadata JSON in an upload
MAX_HISTORY = 50
SLUG_RE = re.compile(r"^[A-Za-z0-9_.()\-]{1,120}$")
TAILSCALE_NETS = [ipaddress.ip_network("100.64.0.0/10"), ipaddress.ip_network("fd7a:115c:a1e0::/48")]

log = logging.getLogger("savecloud")


def utc_now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def login_dir_name(login):
    """Folder name for a login: lowercase, filesystem-safe, never '.' or '..'."""
    name = re.sub(r"[^a-z0-9._@\-]", "_", login.strip().lower())
    return name if name.strip(".") else "_"


class Identity:
    """Maps a connection's source IP to a Tailscale login, with a short cache."""

    def __init__(self, tailscale="tailscale", test_login=None, ttl=60):
        self.tailscale = tailscale
        self.test_login = test_login
        self.ttl = ttl
        self._cache = {}
        self._lock = threading.Lock()

    def login_for(self, ip):
        if self.test_login is not None:
            return self.test_login
        try:
            addr = ipaddress.ip_address(ip)
        except ValueError:
            return None
        if not any(addr in net for net in TAILSCALE_NETS):
            return None
        now = time.monotonic()
        with self._lock:
            hit = self._cache.get(ip)
            if hit and now - hit[1] < self.ttl:
                return hit[0]
        login = None
        try:
            out = subprocess.run([self.tailscale, "whois", "--json", ip], capture_output=True, text=True, timeout=10)
            if out.returncode == 0:
                login = (json.loads(out.stdout).get("UserProfile") or {}).get("LoginName") or None
        except (OSError, subprocess.TimeoutExpired, ValueError) as e:
            log.warning("whois %s failed: %s", ip, e)
        with self._lock:
            self._cache[ip] = (login, now)
        return login


class Allowlist:
    """Logins allowed to use the server, one per line ('#' comments). Re-read when the file changes."""

    def __init__(self, path):
        self.path = path
        self._mtime = None
        self._logins = set()
        self._lock = threading.Lock()

    def allows(self, login):
        if not login:
            return False
        with self._lock:
            try:
                mtime = os.stat(self.path).st_mtime
            except OSError:
                log.error("allowlist %s missing: nobody is allowed", self.path)
                return False
            if mtime != self._mtime:
                with open(self.path, encoding="utf-8") as f:
                    self._logins = {l.split("#", 1)[0].strip().lower() for l in f} - {""}
                self._mtime = mtime
                log.info("allowlist loaded: %d logins", len(self._logins))
            return login.lower() in self._logins


class Store:
    def __init__(self, root, per_user=False):
        self.root = root
        self.per_user = per_user
        self._locks = {}
        self._locks_guard = threading.Lock()
        os.makedirs(root, exist_ok=True)

    def games_dir(self, login):
        if self.per_user:
            return os.path.join(self.root, "users", login_dir_name(login), "games")
        return os.path.join(self.root, "shared", "games")

    def game_dir(self, login, slug):
        return os.path.join(self.games_dir(login), slug)

    def lock(self, login, slug):
        key = (self.games_dir(login), slug)
        with self._locks_guard:
            return self._locks.setdefault(key, threading.Lock())

    def read_head(self, login, slug):
        try:
            with open(os.path.join(self.game_dir(login, slug), "head.json"), encoding="utf-8") as f:
                return json.load(f)
        except FileNotFoundError:
            return None

    def all_heads(self, login):
        base = self.games_dir(login)
        heads = []
        if not os.path.isdir(base):
            return heads
        for slug in sorted(os.listdir(base)):
            if not SLUG_RE.match(slug):
                continue
            try:
                head = self.read_head(login, slug)
            except (OSError, ValueError) as e:
                log.warning("unreadable head %s/%s: %s", login, slug, e)
                continue
            if head:
                heads.append(head)
        return heads

    def publish(self, login, slug, expected, keep, meta, archive_tmp, archive_sha, archive_size, machine):
        """Compare-and-swap a new revision in. Returns (True, head) or (False, current_head)."""
        with self.lock(login, slug):
            d = self.game_dir(login, slug)
            os.makedirs(d, exist_ok=True)
            current = self.read_head(login, slug)
            current_rev = current["Revision"] if current else 0
            if current_rev != expected:
                return False, current

            rev = current_rev + 1
            archive = "rev-%06d.zip" % rev
            os.replace(archive_tmp, os.path.join(d, archive))
            head = {
                "Format": 1,
                "Game": meta["Game"],
                "Revision": rev,
                "Fingerprint": meta["Fingerprint"],
                "Archive": archive,
                "ArchiveSha256": archive_sha,
                "ArchiveSize": archive_size,
                "UploadedUtc": utc_now(),
                "UploadedBy": machine,
                "UploadedLogin": login,
                "Files": meta.get("Files") or [],
            }
            if isinstance(meta.get("Rules"), list) and all(isinstance(r, str) for r in meta["Rules"]):
                head["Rules"] = meta["Rules"][:50]   # folder patterns of games added by hand on a PC
            tmp = os.path.join(d, ".head.json.tmp")
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(head, f, indent=1)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp, os.path.join(d, "head.json"))

            old = sorted(n for n in os.listdir(d) if n.startswith("rev-") and n.endswith(".zip") and n != archive)
            for n in old[: max(0, len(old) - (keep - 1))]:
                try:
                    os.remove(os.path.join(d, n))
                except OSError as e:
                    log.warning("prune %s: %s", n, e)
            return True, head


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "SaveCloud/%d" % API_VERSION

    # set on the server instance
    @property
    def app(self):
        return self.server.app

    def log_message(self, fmt, *args):
        log.info("%s %s", self.client_address[0], fmt % args)

    # ---- responses ---------------------------------------------------------------------------------

    def send_json(self, status, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def error(self, status, message):
        self.send_json(status, {"error": message})

    def drain(self, length=None):
        """Discard the unread part of the request body so the connection can be reused."""
        if length is None:
            length = int(self.headers.get("Content-Length") or 0)
        while length > 0:
            chunk = self.rfile.read(min(length, 1 << 16))
            if not chunk:
                break
            length -= len(chunk)

    # ---- routing -----------------------------------------------------------------------------------

    def identify(self):
        login = self.app.identity.login_for(self.client_address[0])
        return login, self.app.allowlist.allows(login)

    def do_GET(self):
        self.route("GET")

    def do_PUT(self):
        self.route("PUT")

    def route(self, method):
        url = urllib.parse.urlsplit(self.path)
        parts = [urllib.parse.unquote(p) for p in url.path.split("/") if p]
        query = urllib.parse.parse_qs(url.query)
        try:
            login, allowed = self.identify()

            if method == "GET" and parts in (["health"], ["api", "v1", "me"]):
                return self.send_json(200, {"service": "savecloud", "api": API_VERSION, "login": login,
                                            "authorized": allowed,
                                            "mode": "per-user" if self.app.store.per_user else "shared"})

            if not allowed:
                self.drain()
                return self.error(403, "%s is not allowed on this SaveCloud server" % (login or "This device"))

            if parts[:3] != ["api", "v1", "games"]:
                self.drain()
                return self.error(404, "not found")

            if method == "GET" and len(parts) == 3:
                return self.send_json(200, self.app.store.all_heads(login))

            if len(parts) < 5 or not SLUG_RE.match(parts[3]) or parts[3].startswith("."):
                self.drain()
                return self.error(400, "bad game id")
            slug, action = parts[3], parts[4]

            if method == "GET" and action == "head" and len(parts) == 5:
                head = self.app.store.read_head(login, slug)
                return self.send_json(200, head) if head else self.error(404, "no such game")

            if method == "GET" and action == "archive" and len(parts) == 5:
                return self.send_archive(login, slug, query)

            if method == "PUT" and action == "revisions" and len(parts) == 5:
                return self.receive_revision(login, slug, query)

            self.drain()
            return self.error(404, "not found")
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception:  # noqa: BLE001 - last line of defence; never leak a traceback to the client
            log.exception("request failed: %s %s", method, self.path)
            self.close_connection = True  # the request body may be partly unread
            try:
                self.error(500, "internal error")
            except OSError:
                pass

    def send_archive(self, login, slug, query):
        head = self.app.store.read_head(login, slug)
        if not head:
            return self.error(404, "no such game")
        try:
            rev = int((query.get("rev") or [head["Revision"]])[0])
        except ValueError:
            return self.error(400, "bad revision")
        path = os.path.join(self.app.store.game_dir(login, slug), "rev-%06d.zip" % rev)
        try:
            f = open(path, "rb")
        except FileNotFoundError:
            return self.error(404, "revision not kept")
        with f:
            size = os.fstat(f.fileno()).st_size
            self.send_response(200)
            self.send_header("Content-Type", "application/zip")
            self.send_header("Content-Length", str(size))
            self.end_headers()
            shutil.copyfileobj(f, self.wfile, 1 << 16)

    def receive_revision(self, login, slug, query):
        """Body: 4-byte big-endian metadata length, metadata JSON, then the zip."""
        try:
            expected = int(query["expected"][0])
            keep = max(1, min(MAX_HISTORY, int((query.get("keep") or ["10"])[0])))
            length = int(self.headers["Content-Length"])
        except (KeyError, ValueError, TypeError):
            self.drain()
            return self.error(400, "expected, keep and Content-Length are required")
        if length < 4 or length > MAX_UPLOAD + MAX_META:
            self.drain()
            return self.error(413, "upload too large")

        meta_len = struct.unpack(">I", self.rfile.read(4))[0]
        if meta_len > MAX_META or meta_len > length - 4:
            self.drain(length - 4)
            return self.error(400, "bad metadata length")
        remaining = length - 4 - meta_len
        try:
            meta = json.loads(self.rfile.read(meta_len).decode("utf-8"))
            if not isinstance(meta, dict) or not isinstance(meta.get("Game"), str) \
                    or not isinstance(meta.get("Fingerprint"), str):
                raise ValueError("Game and Fingerprint required")
        except ValueError as e:
            self.drain(remaining)
            return self.error(400, "bad metadata: %s" % e)

        if shutil.disk_usage(self.app.store.root).free < remaining + 256 * 1024 * 1024:
            self.drain(remaining)
            return self.error(507, "server disk is full")

        tmp_dir = os.path.join(self.app.store.root, ".incoming")
        os.makedirs(tmp_dir, exist_ok=True)
        fd, tmp = tempfile.mkstemp(dir=tmp_dir, suffix=".zip")
        sha = hashlib.sha256()
        try:
            with os.fdopen(fd, "wb") as out:
                while remaining > 0:
                    chunk = self.rfile.read(min(remaining, 1 << 16))
                    if not chunk:
                        raise ConnectionResetError("upload cut off")
                    sha.update(chunk)
                    out.write(chunk)
                    remaining -= len(chunk)
                out.flush()
                os.fsync(out.fileno())
            digest = sha.hexdigest()
            if meta.get("ArchiveSha256") and meta["ArchiveSha256"].lower() != digest:
                return self.error(400, "archive checksum mismatch (upload corrupted)")

            machine = re.sub(r"[^\w .\-]", "_", self.headers.get("X-SaveCloud-Machine", "unknown"))[:64]
            ok, head = self.app.store.publish(login, slug, expected, keep, meta, tmp,
                                              digest, length - 4 - meta_len, machine)
            if ok:
                log.info("%s: %s revision %d from %s", login, meta["Game"], head["Revision"], machine)
                return self.send_json(200, head)
            return self.send_json(409, head or {})
        finally:
            if os.path.exists(tmp):
                os.remove(tmp)


class ThreadingServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, addr, handler, app):
        self.address_family = socket.AF_INET6 if ":" in addr[0] else socket.AF_INET
        self.app = app
        super().__init__(addr, handler)


class App:
    def __init__(self, store, identity, allowlist):
        self.store = store
        self.identity = identity
        self.allowlist = allowlist


def tailscale_ip(tailscale):
    try:
        out = subprocess.run([tailscale, "ip", "-4"], capture_output=True, text=True, timeout=10)
        ip = out.stdout.strip().splitlines()[0] if out.returncode == 0 and out.stdout.strip() else None
        return ip
    except (OSError, subprocess.TimeoutExpired):
        return None


def main(argv=None):
    p = argparse.ArgumentParser(description="SaveCloud save server")
    p.add_argument("--data", required=True, help="data directory, e.g. /srv/savecloud/data")
    p.add_argument("--allow-file", required=True, help="file with allowed Tailscale logins, one per line")
    p.add_argument("--mode", choices=["shared", "per-user"], default="shared",
                   help="shared: all allowed logins use the same saves (default); per-user: separate saves per login")
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--bind", help="address to listen on (default: this node's Tailscale IPv4, waiting until it exists)")
    p.add_argument("--tailscale", default="tailscale", help="path to the tailscale CLI")
    p.add_argument("--test-login", help=argparse.SUPPRESS)  # tests only: treat every caller as this login
    args = p.parse_args(argv)

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", stream=sys.stdout)

    if args.test_login and args.bind not in ("127.0.0.1", "::1"):
        p.error("--test-login is only allowed with --bind 127.0.0.1")

    bind = args.bind
    while not bind:
        bind = tailscale_ip(args.tailscale)
        if not bind:
            log.info("waiting for Tailscale to be up and logged in...")
            time.sleep(5)

    app = App(Store(args.data, per_user=args.mode == "per-user"),
              Identity(args.tailscale, args.test_login), Allowlist(args.allow_file))
    while True:
        try:
            server = ThreadingServer((bind, args.port), Handler, app)
            break
        except OSError as e:
            log.warning("cannot listen on %s:%d yet (%s); retrying", bind, args.port, e)
            time.sleep(5)
    log.info("SaveCloud server listening on %s:%d (%s saves)", bind, args.port, args.mode)
    print("READY", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
