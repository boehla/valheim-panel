#!/usr/bin/env bash
# The engine lives in community-scripts/core; this repo only ships the scripts.
# A local core checkout wins (COMMUNITY_SCRIPTS_CORE_DIR, else a sibling ../../core),
# so a fork or branch of core can be tested without editing this file.
#
# COMMUNITY_SCRIPTS_URL points the engine back at THIS repo, otherwise it looks
# for install/valheim-install.sh and json/valheim.json in ProxmoxVED and 404s.
# Drop this line if the scripts are ever merged into ProxmoxVED itself.
COMMUNITY_SCRIPTS_URL="${COMMUNITY_SCRIPTS_URL:-https://raw.githubusercontent.com/boehla/valheim-panel/main/proxmox}"
_cs_boot="${COMMUNITY_SCRIPTS_CORE_DIR:-$(dirname "${BASH_SOURCE[0]}")/../../core}/core/build.func"
source "$_cs_boot" 2>/dev/null || source <(curl -fsSL "${COMMUNITY_SCRIPTS_CORE_URL:-https://raw.githubusercontent.com/community-scripts/core/main}/core/build.func")
# A failed download leaves an empty process substitution, which sources cleanly and
# leaves every function undefined -- the script then runs to the end printing
# "command not found" and a success banner. Fail loudly instead.
if ! declare -f build_container >/dev/null 2>&1; then
  echo "ERROR: could not load the community-scripts engine (core/build.func)." >&2
  echo "       Check network access to raw.githubusercontent.com." >&2
  exit 1
fi
# Copyright (c) 2021-2026 community-scripts ORG
# Author: Daniel (boehla)
# License: MIT | https://github.com/community-scripts/ProxmoxVE/raw/main/LICENSE
# Source: https://www.valheimgame.com/

APP="Valheim"
var_tags="${var_tags:-gaming;game-server}"
var_cpu="${var_cpu:-4}"
var_ram="${var_ram:-8192}"
var_disk="${var_disk:-16}"
var_os="${var_os:-debian}"
var_version="${var_version:-13}"
var_unprivileged="${var_unprivileged:-1}"

header_info "$APP"
variables
color
catch_errors

function update_script() {
  header_info
  check_container_storage
  check_container_resources

  if [[ ! -d /opt/valheim/server ]]; then
    msg_error "No ${APP} Installation Found!"
    exit
  fi

  msg_info "Stopping Valheim Server"
  systemctl stop valheim
  msg_ok "Stopped Valheim Server"

  msg_info "Updating Valheim Dedicated Server via SteamCMD"
  # Same as the install script: app_update fails with "Missing configuration" unless
  # the Steam config in $HOME already exists, so create it in its own run first.
  $STD /opt/valheim/steamcmd/steamcmd.sh +quit
  steamcmd_ok=""
  for attempt in 1 2 3; do
    if $STD /opt/valheim/steamcmd/steamcmd.sh +force_install_dir /opt/valheim/server +login anonymous +app_update 896660 validate +quit; then
      steamcmd_ok=1
      break
    fi
    sleep 10
  done
  if [ -z "$steamcmd_ok" ]; then
    msg_error "SteamCMD failed to update app 896660 after 3 attempts"
    systemctl start valheim
    exit 1
  fi
  msg_ok "Updated Valheim Dedicated Server"
  # The unit and its start wrapper belong to the installer, and until now a fix to
  # either only ever reached fresh installs -- the update path never rewrote them.
  # Both are regenerated here on every update so existing containers converge too.
  # Mods survive: the panel keeps those in valheim.service.d/bepinex.conf, which this
  # does not touch. The two blocks below are copies of the ones in
  # install/valheim-install.sh and have to be kept in sync with it.
  msg_info "Refreshing Service Definition"
# The argument list is built in a real shell script, not with $(...) inside ExecStart.
# The mod loader's drop-in preloads libdoorstop_x64.so into the whole unit -- the shell
# that runs ExecStart included -- and Doorstop hooks dup2, which is exactly what a shell
# uses to wire a command substitution to its pipe. With it loaded,
# $([ -n "$SERVER_PASSWORD" ] && echo -password "$SERVER_PASSWORD") returned nothing and
# the echo output leaked to stdout instead, so -password and -crossplay silently
# disappeared from the command line. A public server then started with an empty password
# and Valheim refused it: "Error bad password:The password is too short". Only modded
# servers were affected -- without the drop-in the substitution worked fine.
# set -- uses builtins only, no subshell and no pipe, so it is immune.
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
  $STD systemctl daemon-reload
  msg_ok "Refreshed Service Definition"


  if check_for_gh_release "valheim-panel" "boehla/valheim-panel"; then
    msg_info "Stopping Panel"
    systemctl stop valheim-panel
    msg_ok "Stopped Panel"

    create_backup /etc/valheim/server.env /opt/valheim-panel/.env

    CLEAN_INSTALL=1 fetch_and_deploy_gh_release "valheim-panel" "boehla/valheim-panel" "singlefile" "latest" "/opt/valheim-panel" "valheim-panel-linux-x64"

    restore_backup

    # Same reason as in the install script: the deploy helper records the tag in
    # $HOME/.<app>, but the panel reads its own VERSION file. CLEAN_INSTALL wipes
    # the directory, so this has to come after the deploy, not before.
    if [ -f "$HOME/.valheim-panel" ]; then
      cp "$HOME/.valheim-panel" /opt/valheim-panel/VERSION
    fi

    msg_info "Starting Panel"
    systemctl start valheim-panel
    msg_ok "Started Panel"
  fi

  msg_info "Starting Valheim Server"
  systemctl start valheim
  msg_ok "Started Valheim Server"
  msg_ok "Updated successfully!"
  exit
}

start
build_container
description

msg_ok "Completed Successfully!\n"
echo -e "${CREATING}${GN}${APP} setup has been successfully initialized!${CL}"
echo -e "${INFO}${YW}Access it using the following URL:${CL}"
echo -e "${GATEWAY}${BGN}http://${IP}:8099${CL}"
