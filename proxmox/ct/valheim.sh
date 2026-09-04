#!/usr/bin/env bash
source "$(dirname "${BASH_SOURCE[0]}")/../misc/build.func" 2>/dev/null || source <(curl -fsSL "${COMMUNITY_SCRIPTS_URL:-https://raw.githubusercontent.com/community-scripts/ProxmoxVED/main}/misc/build.func")
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
  $STD /opt/valheim/steamcmd/steamcmd.sh +force_install_dir /opt/valheim/server +login anonymous +app_update 896660 validate +quit
  msg_ok "Updated Valheim Dedicated Server"

  if check_for_gh_release "valheim-panel" "boehla/valheim-panel"; then
    msg_info "Stopping Panel"
    systemctl stop valheim-panel
    msg_ok "Stopped Panel"

    create_backup /etc/valheim/server.env /opt/valheim-panel/.env

    CLEAN_INSTALL=1 fetch_and_deploy_gh_release "valheim-panel" "boehla/valheim-panel" "singlefile" "latest" "/opt/valheim-panel" "valheim-panel-linux-x64"

    restore_backup

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
