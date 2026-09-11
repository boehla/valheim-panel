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

  # See pin_crossplay_cpus below. Only the host can pin, and this runs inside the
  # container, so a container created before the pin existed only gets told.
  local cpus expected
  cpus="$(cat /sys/devices/system/cpu/online 2>/dev/null)"
  expected="0-$(($(nproc) - 1))"
  [[ "$(nproc)" == "1" ]] && expected="0"
  if [[ -n "$cpus" && "$cpus" != "$expected" ]]; then
    msg_warn "This container runs on host CPUs ${cpus}, not ${expected} -- crossplay can lose its join code (see README)"
    msg_warn "On the Proxmox host add 'lxc.cgroup2.cpuset.cpus: ${expected}' to /etc/pve/lxc/<CTID>.conf above any [snapshot] section, then restart the container"
  fi
  msg_ok "Updated successfully!"
  exit
}

# PlayFab Party, which crossplay runs on, pins its worker threads to CPUs 0..N-1 where N
# is how many CPUs it sees -- a count, not the IDs. An LXC sees the host's CPU IDs, and
# pvestatd moves the cores of an unpinned container around while it runs, so a 4-core
# container can sit on 6,10,13,22. The affinity call then fails with EINVAL,
# PartyInitialize returns "unmapped platform error", PlayFab's Unity layer drops that
# without a log line, and the server loops on "begin PlayFab create and join network"
# without ever getting a join code. Pinning to 0..N-1 makes the IDs match the count and
# keeps pvestatd from moving them.
function pin_crossplay_cpus() {
  local conf="/etc/pve/lxc/${CTID}.conf"
  [[ -f "$conf" ]] || return 0
  local cores
  cores="$(pct config "$CTID" | awk '/^cores:/ {print $2}')"
  # Without a core limit the container sees every host CPU from 0 up, which already works.
  [[ -n "$cores" ]] || return 0
  # Only the main section counts: everything after the first [snapshot] header belongs to
  # that snapshot and is ignored. A pin somebody set by hand is left alone.
  if awk '/^\[/ {exit} /^lxc\.cgroup2?\.cpuset\.cpus:/ {found = 1} END {exit !found}' "$conf"; then
    return 0
  fi
  local key="lxc.cgroup2.cpuset.cpus"
  [[ "$(stat -fc %T /sys/fs/cgroup)" == "cgroup2fs" ]] || key="lxc.cgroup.cpuset.cpus"
  local range="0-$((cores - 1))"
  [[ "$cores" == "1" ]] && range="0"

  msg_info "Pinning the container to CPUs ${range} for crossplay"
  # Goes in above the blank line that separates the main section from the first snapshot.
  local content
  content="$(awk -v line="${key}: ${range}" '
    !done && /^$/ {blank = blank $0 "\n"; next}
    !done && /^\[/ {print line; done = 1}
    {printf "%s", blank; blank = ""; print}
    END {if (!done) print line}' "$conf")"
  printf '%s\n' "$content" >"$conf"
  msg_ok "Pinned the container to CPUs ${range}"

  # The install already started Valheim once, and if the cores were elsewhere its PlayFab
  # setup failed for that whole run. A cpuset only applies when the container starts.
  msg_info "Restarting LXC Container"
  if ! pct reboot "$CTID"; then
    msg_warn "Could not restart CT ${CTID} -- restart it by hand so the CPU pin takes effect"
    return 0
  fi
  msg_ok "Restarted LXC Container"
}

start
build_container
description
pin_crossplay_cpus

msg_ok "Completed Successfully!\n"
echo -e "${CREATING}${GN}${APP} setup has been successfully initialized!${CL}"
echo -e "${INFO}${YW}Access it using the following URL:${CL}"
echo -e "${GATEWAY}${BGN}http://${IP}:8099${CL}"
# The panel refuses every request without this token, and it only exists inside the
# container. Plain echo rather than msg_*, which also write to the build log.
PANEL_TOKEN="$(pct exec "$CTID" -- awk -F= '$1 == "PANEL_TOKEN" {print $2}' /opt/valheim-panel/.env 2>/dev/null || true)"
if [[ -n "$PANEL_TOKEN" ]]; then
  echo -e "${INFO}${YW}Log in with this access token:${CL}"
  echo -e "${TAB}🔑${TAB}${BGN}${PANEL_TOKEN}${CL}"
else
  echo -e "${INFO}${YW}The access token is in /opt/valheim-panel/.env inside the container${CL}"
fi
