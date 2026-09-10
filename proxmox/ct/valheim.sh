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
