# Request to the Proxmox agent: SaveCloud server info

You set up the SaveCloud SFTP container from `docs/SERVER_SETUP.md`. The app is being reworked so a new PC
needs **little or no setup**. The goal: install the app, and it finds the server and authorizes itself
automatically over Tailscale. To design that, I need the facts below about what's actually running.

**Write your answers to a file named `SAVECLOUD_SERVER_INFO.md`**, using the template at the bottom. Paste
command output verbatim (trim only obvious noise).

**Do NOT put any private key, password, Tailscale auth key, or API token in the file.** Public keys,
fingerprints, hostnames and IPs are fine.

If something from the spec wasn't done, or was done differently, say so plainly. That's more useful than a
cleaned-up answer.

---

## Commands to run inside the container

```bash
cat /etc/os-release | head -3
ssh -V
hostname; hostname -I
ip -br addr
ss -tlnp
sshd -T -C user=savecloud,host=x,addr=127.0.0.1 | grep -E '^(chrootdirectory|forcecommand|authorizedkeysfile|passwordauthentication)'
sshd -T -C user=root,host=x,addr=127.0.0.1 | grep -E '^(chrootdirectory|forcecommand|passwordauthentication)'
for k in /etc/ssh/ssh_host_*_key.pub; do ssh-keygen -lf "$k"; done
ls -la /srv/savecloud /srv/savecloud/data
df -h /srv/savecloud/data
wc -l /etc/ssh/savecloud_keys/savecloud; awk '{print $1, $NF}' /etc/ssh/savecloud_keys/savecloud
python3 --version
tailscale version
tailscale status
tailscale status --json | python3 -c "import sys,json; d=json.load(sys.stdin); s=d['Self']; print('MagicDNSSuffix:', d.get('MagicDNSSuffix')); print('Self DNSName:', s.get('DNSName')); print('Self IPs:', s.get('TailscaleIPs')); print('Self Tags:', s.get('Tags')); u=d.get('User',{}); print('Users:', [(v.get('LoginName'), v.get('DisplayName')) for v in u.values()])"
tailscale whois $(tailscale ip -4) 2>&1 | head -20
systemctl is-enabled ssh tailscaled unattended-upgrades 2>&1
```

## Commands to run on the Proxmox host

```bash
pveversion
pct config <CTID>
cat /etc/pve/lxc/<CTID>.conf | grep -E 'lxc\.(cgroup2|mount)'
pvesm status
grep -A8 -E 'vzdump|backup' /etc/pve/jobs.cfg 2>/dev/null
pve-firewall status 2>/dev/null; cat /etc/pve/firewall/<CTID>.fw 2>/dev/null
```

## Questions (answer each; "unknown" is a valid answer)

1. **Network**
   - Is the container's LAN IP static or DHCP-reserved? Which IP?
   - Does the home router/LAN DNS resolve the name `savecloud` (or `savecloud.lan`, `savecloud.home`)? From where did you test?
   - Is port 22 forwarded on the router to the container? (Expected: **no**.)
2. **Tailscale**
   - Is Tailscale running in the container? Its MagicDNS name and 100.x IP?
   - Is MagicDNS enabled for the tailnet?
   - Which **Tailscale login (email)** owns the gaming PCs? Is it the same login that owns the container node?
   - Are ACLs/tags customised? If yes, paste the relevant ACL policy section (no keys).
   - Which gaming PCs are already on the tailnet (names from `tailscale status`)?
3. **Willingness to run a tiny pairing service.** To remove manual key copying, I'd like to add a small
   Python 3 service (stdlib only, ~200 lines, systemd unit, runs as its own unprivileged user). It would:
   - listen **only on the container's Tailscale IP**, on one TCP port (proposed **8765**)
   - on `POST /pair` from a PC, run `tailscale whois <caller IP>` and accept only devices owned by the
     owner's Tailscale login
   - append that PC's **public** key to `/etc/ssh/savecloud_keys/savecloud` (via a narrowly scoped helper,
     since that file is root-owned), then return the SFTP host, port, username, folder and host-key fingerprint
   - expose `GET /health` for discovery

   Questions: Can you deploy that from this repo (`server/` folder, once written)? Any constraint against it
   (no Python, policy, SELinux/AppArmor, port 8765 taken)? Is `tailscale whois` usable by a non-root user in the
   container, or does it need root / the operator setting (`tailscale set --operator=<user>`)?
4. **Storage & backups**
   - Which storage backs `mp0`? Is it ZFS? Is `backup=1` set on `mp0`?
   - Is a backup job configured for the container? Schedule, retention, target?
5. **Deviations.** Anything from `SERVER_SETUP.md` skipped, changed, or that failed (and how you fixed it)?
   Results of the verification in step 8 (the two `mkdir /data/t` attempts, `mkdir /evil`, and `ssh` giving no shell)?
6. **Anything else** that would make a zero-setup client harder or easier (e.g. a reverse proxy already present,
   a local DNS server like Pi-hole/AdGuard that could serve a `savecloud` record, a second site).

---

## Template for `SAVECLOUD_SERVER_INFO.md`

```markdown
# SaveCloud server info

## Summary
- Container ID / hostname:
- OS / OpenSSH version:
- LAN IP (static/reserved?):
- LAN DNS name that resolves (if any):
- Tailscale MagicDNS name / 100.x IP:
- Tailscale owner login of container node:
- Tailscale owner login of gaming PCs:
- SFTP port / username / folder:
- Host key fingerprints (all types):
- Python 3 version:
- Pairing service OK to deploy? (yes/no + constraints):
- Port 8765 free on the Tailscale IP? (yes/no):
- `tailscale whois` works as non-root? (yes/no/needs operator):
- Backup job (schedule/retention/target):

## Deviations from SERVER_SETUP.md

## Verification results (step 8)

## Answers to questions 1–6

## Command output
(paste each command and its output)
```
