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
$STD apt install -y \
  ca-certificates \
  lib32gcc-s1 \
  lib32stdc++6
msg_ok "Installed Dependencies"

msg_info "Installing SteamCMD"
mkdir -p /opt/valheim/steamcmd /opt/valheim/server /opt/valheim/data
curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar xz -C /opt/valheim/steamcmd
msg_ok "Installed SteamCMD"

msg_info "Installing Valheim Dedicated Server (this takes a while)"
$STD /opt/valheim/steamcmd/steamcmd.sh +force_install_dir /opt/valheim/server +login anonymous +app_update 896660 validate +quit
msg_ok "Installed Valheim Dedicated Server"

fetch_and_deploy_gh_release "valheim-panel" "boehla/valheim-panel" "singlefile" "latest" "/opt/valheim-panel" "valheim-panel-linux-x64"

msg_info "Configuring Valheim"
mkdir -p /etc/valheim
cat <<EOF >/etc/valheim/server.env
# Managed by valheim-panel — edits here are picked up on the next restart.
SERVER_NAME=Valheim
SERVER_PORT=2456
WORLD_NAME=Dedicated
SERVER_PASSWORD=$(openssl rand -base64 12 | tr -dc 'a-zA-Z0-9' | cut -c1-10)
PUBLIC=0
CROSSPLAY=1
SAVE_DIR=/opt/valheim/data
BACKUPS=4
BACKUP_SHORT=7200
BACKUP_LONG=43200
AUTO_UPDATE=1
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
Environment=LD_LIBRARY_PATH=/opt/valheim/server/linux64
ExecStartPre=/bin/sh -c '[ "\$AUTO_UPDATE" = "1" ] && /opt/valheim/steamcmd/steamcmd.sh +force_install_dir /opt/valheim/server +login anonymous +app_update 896660 +quit || true'
ExecStart=/bin/sh -c 'exec /opt/valheim/server/valheim_server.x86_64 -nographics -batchmode -name "\$SERVER_NAME" -port "\$SERVER_PORT" -world "\$WORLD_NAME" -savedir "\$SAVE_DIR" -public "\$PUBLIC" -backups "\$BACKUPS" -backupshort "\$BACKUP_SHORT" -backuplong "\$BACKUP_LONG" \$([ -n "\$SERVER_PASSWORD" ] && echo -password "\$SERVER_PASSWORD") \$([ "\$CROSSPLAY" = "1" ] && echo -crossplay)'
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
