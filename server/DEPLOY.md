# Deploying the SaveCloud server (for the Proxmox agent)

This replaces the SFTP-based client access from `docs/SERVER_SETUP.md`. The container, data disk, backups and
Tailscale setup from that document stay as they are; this adds one small service.

**What changes and why.** PCs no longer use SSH keys. A Python service (standard library only, ~450 lines,
`savecloud_server.py`) listens **only on the container's Tailscale IP**, port **8765**. It identifies every request
by the caller's Tailscale login (`tailscale whois` on the TCP source address; the client can't fake it), checks it
against an allowlist file, and stores save revisions. A new PC needs only Tailscale signed in. SaveCloud then finds
the server among its Tailscale peers by itself.

**Storage mode:** `shared`, as the owner decided. Every allowed login reads and writes the same saves. Switching
to `per-user` later is one flag; the two modes use separate folders, so switching doesn't mix or delete anything.

## 0. Prerequisites (from your report, still pending)

- Tailscale in CT 160 must be **logged in** (`tailscale status` shows the node, `tailscale ip -4` prints a 100.x IP).
- **Disable key expiry** for the `savecloud` node (admin console → Machines → savecloud → ⋯ → Disable key expiry).
  Otherwise every PC stops syncing when the node key expires.
- Re-test `tailscale whois` as a non-root user **in CT 160** (it worked in CT 140):
  `runuser -u savecloud -- tailscale whois --json <tailscale IP of the owner's PC>` must print a `UserProfile.LoginName`.

## 1. Get the code into the container

```bash
apt install -y python3   # already present (3.13.5); listed for completeness
mkdir -p /root/savecloud-src && cd /root/savecloud-src
curl -fsSL https://github.com/hanzulika/CloudGameSaves/archive/refs/heads/claude/vigilant-brown-9zas8o.tar.gz | tar xz --strip-components=1
ls server/   # savecloud_server.py  savecloud.service  install.sh  DEPLOY.md  tests/
```

(After the branch is merged, use `.../archive/refs/heads/master.tar.gz` instead.)

Optional but quick: `python3 -m unittest discover -s server/tests` should report `OK`.

## 2. Install

```bash
cd /root/savecloud-src/server
OWNER_LOGIN=<owner's Tailscale login> ./install.sh
```

The script:
- copies the program to `/opt/savecloud/` (root-owned)
- creates `/etc/savecloud/allowed_logins` (root:savecloud, 0640) containing the owner's login, **only if it
  doesn't exist yet**. Re-running never touches it or the saves.
- installs and starts `savecloud.service`, which runs as the existing `savecloud` user, can only write
  `/srv/savecloud/data`, and waits for Tailscale to come up before binding.

Add invited people's Tailscale logins (one per line) to `/etc/savecloud/allowed_logins`. Changes apply
immediately; no restart is needed.

## 3. Tailscale ACL (owner pastes this in the admin console)

The owner's `group:owner → *` rule already covers the owner's PCs. Invited users need:

```jsonc
{ "src": ["autogroup:shared"], "dst": ["savecloud"], "ip": ["tcp:8765"] }
```

The planned `tcp:22` grant for `autogroup:shared` is **no longer needed**. Clients don't use SFTP any more, so
don't add it. SSH/SFTP stays available to the owner for admin and manual restores.

Invited users still have to accept the device share of `savecloud`. They connect via its 100.x IP, which the
app discovers automatically. MagicDNS isn't required.

## 4. Verify

Inside the container:

```bash
systemctl status savecloud --no-pager      # active (running), "listening on 100.x.y.z:8765 (shared saves)"
ss -tlnp | grep 8765                         # bound to the Tailscale IP only, not 0.0.0.0
journalctl -u savecloud -n 20 --no-pager
```

From the owner's PC (PowerShell), with the container's Tailscale IP:

```powershell
curl.exe http://<100.x>:8765/health
# {"service": "savecloud", "api": 1, "login": "<owner login>", "authorized": true, "mode": "shared"}
```

From a device that is **not** on the allowlist, the same call returns `"authorized": false`, and
`curl.exe http://<100.x>:8765/api/v1/games` returns HTTP 403.

From the home LAN or the Proxmox host (`curl http://192.168.1.160:8765/health`): connection refused, because
it doesn't listen there.

## 5. Clean up

- Remove the throwaway key `savecloud@TEST` from `/etc/ssh/savecloud_keys/savecloud`.
- Nothing else from the SFTP setup needs to change.

## Operations

| Task | How |
|---|---|
| Allow a person | add their Tailscale login to `/etc/savecloud/allowed_logins` |
| Revoke a person | remove the line (takes effect immediately; Tailscale share can also be revoked) |
| Logs | `journalctl -u savecloud` (every upload is logged with login, game and PC name) |
| Update | download the new code as in step 1, re-run `./install.sh` (no `OWNER_LOGIN` needed) |
| Per-user saves | `SAVECLOUD_MODE=per-user ./install.sh` |
| Storage layout | `/srv/savecloud/data/shared/games/<game-id>/head.json` + `rev-NNNNNN.zip` (last 10 kept) |
| Manual restore | unzip any `rev-*.zip`; entries look like `<winAppData>/Game/slot1.sav` = `%AppData%\Game\slot1.sav` |

## Report back

Please write `SAVECLOUD_DEPLOY_RESULT.md` with: the output of the step 4 checks (with the login redacted if you
prefer), whether the whois re-test in step 0 passed in CT 160, whether key expiry is disabled, whether the
ACL grant was added, and anything that failed or had to be done differently. No secrets.
