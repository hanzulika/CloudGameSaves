# SaveCloud server setup (Proxmox)

This is the spec for the machine that stores the game saves. It's written so it can be handed to whoever (or
whatever agent) administers the Proxmox host. **The server runs no SaveCloud code.** It is a locked-down
SFTP-only account in a small container; all sync logic (versions, conflicts, locking) lives in the Windows app.

## Decisions the owner must make first

| # | Question | Recommended answer | Why it matters |
|---|----------|--------------------|----------------|
| 1 | How do PCs reach the server when they're **not** on the home network? | **Tailscale** inside the container | Without it, saves only sync at home. Port-forwarding SSH to the internet works but invites constant brute-force attempts. |
| 2 | Which Proxmox storage holds the data? | A ZFS-backed storage (e.g. `local-zfs`) | Snapshots and `vzdump` backups of the save data. |
| 3 | Is there a backup target (Proxmox Backup Server or another disk)? | Yes, daily | The app keeps 10 old versions per game, but that doesn't protect against the container or disk dying. |

If Tailscale is not wanted, skip step 5 below. The PCs then need a LAN address (DHCP reservation or static IP).

## 1. Container

- **Type:** unprivileged LXC, Debian 12 template
- **Hostname:** `savecloud`
- **Resources:** 1 vCPU, 512 MB RAM, 4 GB root disk
- **Data disk:** a separate mount point `mp0` of 20 GB (saves are small; 20 GB is years of history), mounted at
  `/srv/savecloud/data`, **with backup enabled** (`backup=1`)
- **Network:** bridge `vmbr0`, DHCP reservation or static IP on the LAN
- **Features:** `nesting=1` is not needed. Start at boot: yes.

Example (adjust storage names and ID):

```bash
pct create 120 local:vztmpl/debian-12-standard_12.7-1_amd64.tar.zst \
  --hostname savecloud --unprivileged 1 --cores 1 --memory 512 --swap 256 \
  --rootfs local-zfs:4 --mp0 local-zfs:20,mp=/srv/savecloud/data,backup=1 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp --onboot 1
pct start 120
```

## 2. Packages

Inside the container (`pct enter 120`):

```bash
apt update && apt full-upgrade -y
apt install -y openssh-server unattended-upgrades
dpkg-reconfigure -f noninteractive unattended-upgrades
```

## 3. The `savecloud` account and folders

```bash
useradd --system --no-create-home --home-dir /data --shell /usr/sbin/nologin savecloud
# IMPORTANT: useradd leaves the account "locked" (password field "!"), and OpenSSH then rejects
# even key logins ("User savecloud not allowed because account is locked"). Unlock it without a password:
usermod -p '*' savecloud

# Chroot jail: the jail root must be owned by root and not writable by anyone else (OpenSSH requirement).
mkdir -p /srv/savecloud/data
chown root:root /srv/savecloud
chmod 755 /srv/savecloud
# The data folder is the only place the account can write.
chown savecloud:savecloud /srv/savecloud/data
chmod 700 /srv/savecloud/data

# Authorized keys live outside the jail and are owned by root, so the account can't change its own keys.
mkdir -p /etc/ssh/savecloud_keys
touch /etc/ssh/savecloud_keys/savecloud
chmod 755 /etc/ssh/savecloud_keys
chmod 644 /etc/ssh/savecloud_keys/savecloud
```

> On an unprivileged container the data mount point's ownership must be set from **inside** the container as
> above. If `chown` on `/srv/savecloud/data` fails, the mount point was created with host-side ownership; fix it
> with `pct set`/host-side `chown 100000:100000` (UID mapping) and re-run the `chown` inside.

## 4. SSH configuration

Create `/etc/ssh/sshd_config.d/10-savecloud.conf`:

```
# Key-only logins for everyone.
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password

Match User savecloud
    AuthorizedKeysFile /etc/ssh/savecloud_keys/%u
    ChrootDirectory /srv/savecloud
    ForceCommand internal-sftp -u 0077
    AllowTcpForwarding no
    AllowAgentForwarding no
    X11Forwarding no
    PermitTTY no
    PermitTunnel no
```

Validate and apply. The two `sshd -T` checks confirm the `Match` block applies **only** to `savecloud`:

```bash
sshd -t && systemctl restart ssh
sshd -T -C user=savecloud,host=x,addr=127.0.0.1 | grep -E '^(chrootdirectory|forcecommand)'
#   expect: chrootdirectory /srv/savecloud   and   forcecommand internal-sftp -u 0077
sshd -T -C user=root,host=x,addr=127.0.0.1 | grep -E '^(chrootdirectory|forcecommand)'
#   expect: chrootdirectory none   and   forcecommand none
```

## 5. Remote access with Tailscale (recommended)

An unprivileged LXC needs the TUN device. On the **Proxmox host**, add to `/etc/pve/lxc/120.conf` and restart the
container:

```
lxc.cgroup2.devices.allow: c 10:200 rwm
lxc.mount.entry: /dev/net/tun dev/net/tun none bind,create=file
```

Inside the container:

```bash
curl -fsSL https://tailscale.com/install.sh | sh
tailscale up --hostname=savecloud
```

Each gaming PC runs the Tailscale client on the same tailnet. The app then uses the MagicDNS name
(`savecloud.<tailnet>.ts.net`) or the `100.x.y.z` address. Port 22 is **not** forwarded on the router.

## 6. Backups

- In the Proxmox UI: Datacenter → Backup → add a daily job for container 120 (snapshot mode), keep ~14 dailies.
- The `mp0` data disk must have `backup=1` (set in step 1), otherwise the saves are not in the backup.

## 7. Adding a PC

Each PC gets its own key (SaveCloud → Settings → **Generate key…** copies the public key line). Append that line,
unchanged, to `/etc/ssh/savecloud_keys/savecloud`. No restart needed. To revoke a PC, delete its line. The key's
comment (`savecloud@PCNAME`) shows which PC it belongs to.

## 8. Verification (run from any PC with OpenSSH, using a key that's been added)

```bash
sftp -i <private key> savecloud@<server>
```

```
sftp> ls -la /           # shows only "data"
sftp> mkdir /data/t      # succeeds
sftp> mkdir /data/t      # must FAIL ("Failure"): SaveCloud uses this as its lock
sftp> rmdir /data/t
sftp> mkdir /evil        # must FAIL ("Permission denied"): the jail is read-only outside /data
```

Also confirm `ssh -i <key> savecloud@<server>` does **not** give a shell.

## 9. What to report back to the owner

| Item | Example |
|------|---------|
| Server address (Tailscale name and/or LAN IP) | `savecloud.tail1234.ts.net`, `192.168.1.50` |
| Port | `22` |
| Username | `savecloud` |
| Server folder | `/data` |
| Host key fingerprint, from `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` | `SHA256:DVQ4Bb7Lrm2M…` |

The owner compares the fingerprint with the one SaveCloud shows on its first **Test connection**. A match means the
app is talking to the right server. After that the app refuses any server presenting a different key.

## What's stored

```
/srv/savecloud/data/
  games/<Game_Name>-<hash>/
    head.json          current revision: game name, revision number, file list with SHA-256 hashes, uploading PC
    rev-000042.zip     saves for that revision (the last 10 are kept)
    .lock/             only exists for a moment while a PC uploads
```

Restoring by hand is possible: unzip any `rev-*.zip`. Entry names look like
`<winAppData>/Game/slot1.sav`, meaning `%AppData%\Game\slot1.sav`.
