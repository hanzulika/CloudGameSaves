#!/bin/sh
# Installs or updates the SaveCloud server inside the savecloud container. Run as root from this folder:
#   OWNER_LOGIN=you@example.com ./install.sh
# Re-running is safe: it updates the program and service but never touches the allowlist or saves.
set -eu
cd "$(dirname "$0")"

MODE="${SAVECLOUD_MODE:-shared}"
case "$MODE" in shared|per-user) ;; *) echo "SAVECLOUD_MODE must be shared or per-user" >&2; exit 1 ;; esac

command -v python3 >/dev/null || { echo "python3 is required" >&2; exit 1; }
command -v tailscale >/dev/null || { echo "tailscale is required" >&2; exit 1; }

if ! id savecloud >/dev/null 2>&1; then
  useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin savecloud
fi
install -d -o savecloud -g savecloud -m 700 /srv/savecloud/data

install -d -m 755 /opt/savecloud
install -m 755 savecloud_server.py /opt/savecloud/savecloud_server.py

install -d -o root -g savecloud -m 750 /etc/savecloud
if [ ! -f /etc/savecloud/allowed_logins ]; then
  : "${OWNER_LOGIN:?first install: set OWNER_LOGIN to your Tailscale login, e.g. OWNER_LOGIN=you@example.com}"
  printf '# Tailscale logins allowed to use SaveCloud, one per line. Changes apply immediately.\n%s\n' "$OWNER_LOGIN" > /etc/savecloud/allowed_logins
fi
chown root:savecloud /etc/savecloud/allowed_logins
chmod 640 /etc/savecloud/allowed_logins

sed "s/--mode shared/--mode $MODE/" savecloud.service > /etc/systemd/system/savecloud.service
systemctl daemon-reload
systemctl enable savecloud >/dev/null
systemctl restart savecloud
sleep 3
systemctl --no-pager --lines=5 status savecloud || true
echo
echo "Allowed logins: /etc/savecloud/allowed_logins"
echo "Health check from a tailnet device: curl http://$(tailscale ip -4 2>/dev/null || echo '<tailscale-ip>'):8765/health"
