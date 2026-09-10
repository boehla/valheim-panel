#!/usr/bin/env bash

# Copyright (c) 2021-2026 community-scripts ORG
# Author: Daniel (boehla)
# License: MIT | https://github.com/community-scripts/ProxmoxVE/raw/main/LICENSE
# Source: https://www.valheimgame.com/

source /dev/stdin <<<"$FUNCTIONS_FILE_PATH"
color
verb_ip6
catch_errors
setting_up_container
network_check
update_os

msg_info "Installing Dependencies"
dpkg --add-architecture i386
$STD apt update
# libpulse-dev, not libpulse0: PlayFab Party dlopens the unversioned libpulse.so,
# and Debian ships that symlink only in the -dev package. Without it Party fails to
# initialise with "DLL Not Found" and a crossplay server never gets a join code.
$STD apt install -y \
  ca-certificates \
  lib32gcc-s1 \
  lib32stdc++6 \
  libpulse-dev
msg_ok "Installed Dependencies"

msg_info "Installing SteamCMD"
mkdir -p /opt/valheim/steamcmd /opt/valheim/server /opt/valheim/data /opt/valheim/mods
curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar xz -C /opt/valheim/steamcmd
msg_ok "Installed SteamCMD"

msg_info "Installing Valheim Dedicated Server (this takes a while)"
# app_update needs an initialised Steam config in $HOME. Where $HOME/Steam does not
# exist yet the client creates it during this very session, and the app_update queued
# into the same invocation runs before it is usable: "Missing configuration". A bare
# +quit run creates the config first. This is not a client self-update -- it happens
# with an already current steamcmd.
$STD /opt/valheim/steamcmd/steamcmd.sh +quit
# 2 GB over Steam's CDN fails often enough to be worth retrying, and app_update
# resumes a partial download, so a retry costs little. The call has to sit in an if:
# $STD is core's silent(), which returns the exit code, but a bare failing call
# trips the ERR trap and takes the whole install down before we can retry.
steamcmd_ok=""
for attempt in 1 2 3; do
  if $STD /opt/valheim/steamcmd/steamcmd.sh +force_install_dir /opt/valheim/server +login anonymous +app_update 896660 validate +quit; then
    steamcmd_ok=1
    break
  fi
  sleep 10
done
if [ -z "$steamcmd_ok" ]; then
  msg_error "SteamCMD failed to install app 896660 after 3 attempts"
  exit 1
fi
msg_ok "Installed Valheim Dedicated Server"

fetch_and_deploy_gh_release "valheim-panel" "boehla/valheim-panel" "singlefile" "latest" "/opt/valheim-panel" "valheim-panel-linux-x64"

# fetch_and_deploy_gh_release records the deployed tag in $HOME/.<app>, but the
# panel reads its own VERSION file -- that is what SelfUpdate writes as
# VERSION.new and what the unit's ExecStartPre swaps in. Without seeding it here
# a fresh install reports version "dev" and offers an update to the very release
# it just installed.
if [ -f "$HOME/.valheim-panel" ]; then
  cp "$HOME/.valheim-panel" /opt/valheim-panel/VERSION
fi

msg_info "Configuring Valheim"
mkdir -p /etc/valheim
cat <<EOF >/etc/valheim/server.env
# Managed by valheim-panel — edits here are picked up on the next restart.
SERVER_NAME=Valheim
SERVER_PORT=2456
WORLD_NAME=Dedicated
SERVER_PASSWORD=$(openssl rand -base64 12 | tr -dc 'a-zA-Z0-9' | cut -c1-10)
# PUBLIC=1 is what makes CROSSPLAY=1 useful: Valheim registers the PlayFab session
# only for a public server, and that registration is what issues the join code.
PUBLIC=1
CROSSPLAY=1
SAVE_DIR=/opt/valheim/data
BACKUPS=4
BACKUP_SHORT=7200
BACKUP_LONG=43200
AUTO_UPDATE=1
AUTO_BACKUP_HOURS=6
EOF
chmod 600 /etc/valheim/server.env

cat <<EOF >/opt/valheim-panel/.env
PANEL_PORT=8099
PANEL_TOKEN=$(openssl rand -hex 24)
PANEL_REPO=boehla/valheim-panel
EOF
chmod 600 /opt/valheim-panel/.env
msg_ok "Configured Valheim"

msg_info "Creating Service"
# The argument list is built in a real shell script, not in ExecStart. ExecStart is
# not a shell command line: systemd resolves $... in it by its own rules before
# /bin/sh ever runs, which silently swallowed the $(...) that appended -password and
# -crossplay. A public server then started with an empty password and refused to boot
# ("Error bad password:The password is too short"), and crossplay never turned on.
cat <<'WRAP' >/opt/valheim/start-server.sh
#!/bin/sh
# Written by the valheim-panel installer. Every value comes from
# /etc/valheim/server.env via the unit's EnvironmentFile.
set -- -nographics -batchmode \
  -name "$SERVER_NAME" \
  -port "$SERVER_PORT" \
  -world "$WORLD_NAME" \
  -savedir "$SAVE_DIR" \
  -public "$PUBLIC" \
  -backups "$BACKUPS" \
  -backupshort "$BACKUP_SHORT" \
  -backuplong "$BACKUP_LONG"
# No "set -e" and no "[ x ] && set -- ...": a false test would end the script.
if [ -n "${SERVER_PASSWORD:-}" ]; then set -- "$@" -password "$SERVER_PASSWORD"; fi
if [ "${CROSSPLAY:-}" = "1" ]; then set -- "$@" -crossplay; fi
exec /opt/valheim/server/valheim_server.x86_64 "$@"
WRAP
chmod 755 /opt/valheim/start-server.sh
cat <<EOF >/etc/systemd/system/valheim.service
[Unit]
Description=Valheim Dedicated Server
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/valheim/server
EnvironmentFile=/etc/valheim/server.env
Environment=SteamAppId=892970
# systemd does not set HOME for a system unit without User=, so the AUTO_UPDATE
# steamcmd below would look for its Steam config somewhere other than /root and hit
# the same "Missing configuration" as a fresh install -- silently, because of the
# "|| true" that keeps a failed update from blocking the server start.
Environment=HOME=/root
Environment=LD_LIBRARY_PATH=/opt/valheim/server/linux64
# The panel switches mods on by dropping bepinex.conf into valheim.service.d, which
# overrides LD_LIBRARY_PATH and adds the Doorstop variables. Keep that out of this
# file: it is rewritten on every reinstall and would take the mod loader with it.
ExecStartPre=/bin/sh -c '[ "\$AUTO_UPDATE" = "1" ] && /opt/valheim/steamcmd/steamcmd.sh +force_install_dir /opt/valheim/server +login anonymous +app_update 896660 +quit || true'
ExecStart=/opt/valheim/start-server.sh
KillSignal=SIGINT
TimeoutStopSec=120
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

cat <<EOF >/etc/systemd/system/valheim-panel.service
[Unit]
Description=Valheim Panel
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/valheim-panel
EnvironmentFile=/opt/valheim-panel/.env
ExecStartPre=/bin/sh -c '[ -f /opt/valheim-panel/valheim-panel.new ] && mv -f /opt/valheim-panel/valheim-panel.new /opt/valheim-panel/valheim-panel && mv -f /opt/valheim-panel/VERSION.new /opt/valheim-panel/VERSION || true'
ExecStart=/opt/valheim-panel/valheim-panel
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl enable -q --now valheim
systemctl enable -q --now valheim-panel
msg_ok "Created Service"

motd_ssh
customize
cleanup_lxc
