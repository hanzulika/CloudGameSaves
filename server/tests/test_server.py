"""Tests for savecloud_server. Run: python3 -m unittest discover -s server/tests"""

import hashlib
import http.client
import json
import os
import struct
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import savecloud_server as srv  # noqa: E402


class FakeIdentity:
    """Maps client IPs to logins without calling tailscale."""

    def __init__(self):
        self.login = "owner@example.com"

    def login_for(self, ip):
        return self.login


class ServerTest(unittest.TestCase):
    per_user = False

    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.data = os.path.join(self.dir.name, "data")
        self.allow = os.path.join(self.dir.name, "allowed")
        with open(self.allow, "w") as f:
            f.write("# owner\nOwner@Example.com\nfriend@example.com  # invited\n")
        self.identity = FakeIdentity()
        app = srv.App(srv.Store(self.data, per_user=self.per_user), self.identity, srv.Allowlist(self.allow))
        self.server = srv.ThreadingServer(("127.0.0.1", 0), srv.Handler, app)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.dir.cleanup()

    def request(self, method, path, body=None, headers=None):
        c = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        c.request(method, path, body=body, headers=headers or {})
        r = c.getresponse()
        data = r.read()
        c.close()
        ctype = r.getheader("Content-Type", "")
        return r.status, (json.loads(data) if "json" in ctype else data)

    def upload(self, slug, expected, game="Game", content=b"zipbytes", keep=10, fingerprint=None, sha=None):
        meta = json.dumps({
            "Game": game,
            "Fingerprint": fingerprint or hashlib.sha256(content).hexdigest(),
            "ArchiveSha256": sha if sha is not None else hashlib.sha256(content).hexdigest(),
            "Files": [{"Path": "<winAppData>/G/s.sav", "Size": 1, "Sha256": "x", "ModifiedUtc": "2026-01-01T00:00:00Z"}],
        }).encode()
        body = struct.pack(">I", len(meta)) + meta + content
        return self.request("PUT", "/api/v1/games/%s/revisions?expected=%d&keep=%d" % (slug, expected, keep),
                            body, {"X-SaveCloud-Machine": "PC-1", "Content-Length": str(len(body))})

    def test_health_reports_identity(self):
        status, body = self.request("GET", "/health")
        self.assertEqual(200, status)
        self.assertEqual("savecloud", body["service"])
        self.assertTrue(body["authorized"])
        self.identity.login = "stranger@example.com"
        self.assertFalse(self.request("GET", "/health")[1]["authorized"])

    def test_unlisted_login_is_rejected(self):
        self.identity.login = "stranger@example.com"
        self.assertEqual(403, self.request("GET", "/api/v1/games")[0])
        self.assertEqual(403, self.upload("G-1", 0)[0])
        self.identity.login = None
        self.assertEqual(403, self.request("GET", "/api/v1/games")[0])

    def test_upload_download_roundtrip(self):
        status, head = self.upload("G-1", 0, content=b"hello")
        self.assertEqual(200, status)
        self.assertEqual(1, head["Revision"])
        self.assertEqual("PC-1", head["UploadedBy"])
        self.assertEqual(hashlib.sha256(b"hello").hexdigest(), head["ArchiveSha256"])

        status, heads = self.request("GET", "/api/v1/games")
        self.assertEqual([1], [h["Revision"] for h in heads])
        self.assertEqual(200, self.request("GET", "/api/v1/games/G-1/head")[0])
        status, data = self.request("GET", "/api/v1/games/G-1/archive")
        self.assertEqual((200, b"hello"), (status, data))

    def test_stale_expected_revision_is_a_conflict(self):
        self.assertEqual(200, self.upload("G-1", 0)[0])
        self.assertEqual(200, self.upload("G-1", 1, content=b"two")[0])
        status, current = self.upload("G-1", 1, content=b"late")   # another PC already made revision 2
        self.assertEqual(409, status)
        self.assertEqual(2, current["Revision"])

    def test_concurrent_uploads_only_one_wins(self):
        results = []

        def go(i):
            results.append(self.upload("Race-1", 0, content=b"pc%d" % i)[0])

        threads = [threading.Thread(target=go, args=(i,)) for i in range(8)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual(1, results.count(200))
        self.assertEqual(7, results.count(409))

    def test_shared_mode_everyone_sees_the_same_saves(self):
        self.upload("G-1", 0, content=b"owner's save")
        self.identity.login = "friend@example.com"
        status, heads = self.request("GET", "/api/v1/games")
        self.assertEqual(["owner@example.com"], [h["UploadedLogin"] for h in heads])
        self.assertEqual(b"owner's save", self.request("GET", "/api/v1/games/G-1/archive")[1])
        status, head = self.upload("G-1", 1, content=b"friend played on")
        self.assertEqual((200, 2, "friend@example.com"), (status, head["Revision"], head["UploadedLogin"]))
        self.assertEqual("shared", self.request("GET", "/health")[1]["mode"])

    def test_history_is_pruned(self):
        for i in range(6):
            self.assertEqual(200, self.upload("G-1", i, content=b"v%d" % i, keep=3)[0])
        d = os.path.join(self.data, "shared", "games", "G-1")
        self.assertEqual(["rev-000004.zip", "rev-000005.zip", "rev-000006.zip"],
                         sorted(n for n in os.listdir(d) if n.endswith(".zip")))
        self.assertEqual(200, self.request("GET", "/api/v1/games/G-1/archive?rev=4")[0])
        self.assertEqual(404, self.request("GET", "/api/v1/games/G-1/archive?rev=1")[0])

    def test_corrupted_upload_is_rejected(self):
        status, _ = self.upload("G-1", 0, content=b"data", sha="0" * 64)
        self.assertEqual(400, status)
        self.assertEqual(404, self.request("GET", "/api/v1/games/G-1/head")[0])

    def test_bad_ids_are_rejected(self):
        for slug in ("..", ".hidden", "a%2Fb", "x" * 200):
            self.assertIn(self.upload(slug, 0)[0], (400, 404), slug)
        self.assertFalse(os.path.exists(os.path.join(self.data, "shared", "games", ".hidden")))

    def test_allowlist_reloads_when_edited(self):
        self.identity.login = "new@example.com"
        self.assertEqual(403, self.request("GET", "/api/v1/games")[0])
        with open(self.allow, "a") as f:
            f.write("new@example.com\n")
        os.utime(self.allow, (os.stat(self.allow).st_atime, os.stat(self.allow).st_mtime + 5))
        self.assertEqual(200, self.request("GET", "/api/v1/games")[0])


class PerUserModeTest(ServerTest):
    """Runs every ServerTest again with per-user storage, plus the isolation check."""
    per_user = True

    def test_shared_mode_everyone_sees_the_same_saves(self):
        pass  # not applicable

    def test_history_is_pruned(self):
        for i in range(3):
            self.upload("G-1", i, content=b"v%d" % i, keep=2)
        d = os.path.join(self.data, "users", "owner@example.com", "games", "G-1")
        self.assertEqual(2, len([n for n in os.listdir(d) if n.endswith(".zip")]))

    def test_users_are_isolated(self):
        self.upload("G-1", 0, content=b"owner's save")
        self.identity.login = "friend@example.com"
        self.assertEqual([], self.request("GET", "/api/v1/games")[1])
        self.assertEqual(404, self.request("GET", "/api/v1/games/G-1/archive")[0])
        status, head = self.upload("G-1", 0, content=b"friend's save")
        self.assertEqual((200, 1), (status, head["Revision"]))
        self.identity.login = "owner@example.com"
        self.assertEqual(b"owner's save", self.request("GET", "/api/v1/games/G-1/archive")[1])


class IdentityTest(unittest.TestCase):
    def test_non_tailscale_addresses_have_no_identity(self):
        ident = srv.Identity(tailscale="/nonexistent/tailscale")
        self.assertIsNone(ident.login_for("192.168.1.20"))
        self.assertIsNone(ident.login_for("127.0.0.1"))
        self.assertIsNone(ident.login_for("not-an-ip"))

    def test_login_dir_names_are_safe(self):
        self.assertEqual("a.b@x.com", srv.login_dir_name("A.B@x.com"))
        self.assertEqual("_", srv.login_dir_name(".."))
        self.assertEqual("_etc_passwd", srv.login_dir_name("/etc/passwd"))


if __name__ == "__main__":
    unittest.main()


class WhoisParsingTest(unittest.TestCase):
    """Runs Identity against a fake `tailscale` CLI that prints real-format whois JSON."""

    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.cli = os.path.join(self.dir.name, "tailscale")
        with open(self.cli, "w") as f:
            f.write("#!/bin/sh\n"
                    "[ \"$1\" = whois ] || exit 2\n"
                    "case \"$3\" in\n"
                    "  100.64.0.7) echo '{\"Node\":{\"Name\":\"\"},\"UserProfile\":{\"ID\":1,\"LoginName\":\"friend@example.com\",\"DisplayName\":\"Friend\"}}' ;;\n"
                    "  *) echo 'peer not found' >&2; exit 1 ;;\n"
                    "esac\n")
        os.chmod(self.cli, 0o755)

    def tearDown(self):
        self.dir.cleanup()

    @unittest.skipIf(os.name == "nt", "shell script fake")
    def test_whois_login_is_used_and_unknown_peers_get_none(self):
        ident = srv.Identity(tailscale=self.cli)
        self.assertEqual("friend@example.com", ident.login_for("100.64.0.7"))
        self.assertIsNone(ident.login_for("100.64.0.8"))
