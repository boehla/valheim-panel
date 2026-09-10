# valheim-panel

A Valheim dedicated server in a Proxmox LXC, plus a small web panel to run it.

The panel shows whether the server is up, how many players are connected, and which
Steam build is installed. It can start, stop and restart the server, run a SteamCMD
update, edit the server settings, and update itself from this repository's releases.

Written in ASP.NET Core and shipped as one self-contained binary, so the container
needs no runtime, no Node, and no Docker.

## Install

Run on the **Proxmox host** shell:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/boehla/valheim-panel/main/proxmox/ct/valheim.sh)"
```

Defaults: Debian 13, 4 cores, 8 GB RAM, 16 GB disk, unprivileged. The initial
SteamCMD download is around 2.5 GB, so the first run takes a while.

When it finishes, open `http://<container-ip>:8099`. The access token is in
`/opt/valheim-panel/.env`.

## What lives where

| Path | Contents |
|---|---|
| `/opt/valheim/server` | Game server files (SteamCMD app 896660) |
| `/opt/valheim/data` | Worlds and the server's own backups |
| `/opt/valheim/backups` | The panel's world archives |
| `/opt/valheim/server/BepInEx` | Mod loader and one directory per installed mod |
| `/opt/valheim/mods` | Thunderstore catalogue, package cache, client pack |
| `/etc/valheim/server.env` | Server settings, written by the panel |
| `/opt/valheim-panel` | Panel binary, `.env`, `VERSION` |

Two systemd units: `valheim` and `valheim-panel`.

`valheim.service` stops with `SIGINT`, which is the only signal Valheim treats as a
clean shutdown. On `SIGTERM` the world is not written and the last session is lost.

## Backups and starting a fresh world

Valheim rotates its own backups inside `/opt/valheim/data` on the `-backupshort` and
`-backuplong` intervals. The panel keeps a separate set in `/opt/valheim/backups`: one
`tar.gz` per world.

**Valheim 1.0 changed what a world is.** It used to be four loose files — `.db`, `.fwl`
and the `.old` generation of each. Since 1.0 it is a directory holding
`_main.<n>.db2`, `.fwl2`, `.chunks` and `.ok` plus one `.chunk` per zone, where `<n>` is
the save number and every chunk carries its own counter. Nothing in there has a stable
name, so the archive is the whole directory. The panel still reads and writes the old
layout when it finds one, which is what a container that has not taken the 1.0 update has.

That difference matters most on the way back in. Chunks are only rewritten when they
change, so unpacking an older archive over a live world would leave newer chunks and a
higher `_main.<n>` in place and the server would go on loading those — a restore that
reports success and changes nothing. So a restore removes the world directory first.
Archives written before 1.0 hold loose files that would land *beside* the directory
instead of replacing it; restoring one onto a 1.0 world is refused rather than guessed at.
The format is read out of the archive, not out of its name.

The file name carries everything else the panel knows about an archive —
`20260909-181500_perma_Kanisfjall.tar.gz` is timestamp, kind, world — so there is no
index that can drift out of sync with the directory. `perma` archives are never pruned;
`temp` ones are kept ten deep. The name resolves to a whole second; a second backup of
the same world and kind within that second moves on to the next free second rather than
overwriting the first.

`AUTO_BACKUP_HOURS` (6 by default, 0 turns it off) adds a cyclic one on top. Whether a
backup is due is derived from the newest `temp` archive on disk rather than from a timer,
so restarting the panel — or letting it update itself — does not reset the cycle and
there is no schedule to persist. A cycle is skipped when the world has not been written
since the last archive, so an idle server does not push its own history out of the ten
kept slots.

Restoring stops the server, archives the world it is about to overwrite, unpacks, and
starts the server again. If the archive holds a different world than the one configured,
the job log says so — the files land correctly, but the server only loads them once the
world name in the settings matches.

**Welt neu generieren** deletes the current world so Valheim builds a new one on the next
start. A permanent backup is made first, and its success gates the deletion: if the
archive cannot be written, nothing is removed. Confirmation is the world name typed in
full, and the API checks it too — a browser dialog is not a safeguard an endpoint has.
The form also takes a new world name, which is the gentler route to a fresh start: the
old world keeps its files and can be switched back to at any time.

For Valheim 1.0 this is the button that matters. The Deep North only generates in terrain
that has never been explored, so a world carried over from 0.2x will not have it. Try a
backup and a restore once before the day you need them.

## Mods

Mods come from [Thunderstore](https://thunderstore.io/c/valheim/) and run under BepInEx.
The panel searches the catalogue, installs a package with its dependencies, and builds
the zip the players need — nothing has to be done over SSH.

Thunderstore has no working search endpoint: `/api/experimental/package/` takes a `q`
parameter and ignores it, and the frontend API is behind a bot check. So the panel takes
the full community listing once — 162 MB of JSON, 12 MB gzipped — keeps only the newest
version of each of the ~10 500 packages, and searches that locally. The catalogue is
streamed element by element rather than parsed as one document, which is what keeps it
off the heap in a container sized for the game server. It is cached in
`/opt/valheim/mods/catalog.json` and refetched when it is older than twelve hours.

**BepInEx is not installed as a mod.** It unpacks into the server root, and the panel
switches it on by writing `/etc/systemd/system/valheim.service.d/bepinex.conf` with the
Doorstop variables — the same ones the pack's own `start_server_bepinex.sh` exports, but
absolute, because systemd does not run the unit through that script. The drop-in is
deliberately not part of `valheim.service`: that file is rewritten on every reinstall and
would take the mod loader with it. Deleting the drop-in gives back a vanilla server with
every mod still on disk.

Each mod lives in `BepInEx/plugins/<Owner-Name>/` with a `valheim-panel.json` next to its
files holding the version and the flags. That directory *is* the state — there is no index
that can drift from what BepInEx actually loads, and deleting the directory really does
uninstall the mod. Disabling moves it to `BepInEx/plugins-disabled/`, which BepInEx does
not scan. Configs under `BepInEx/config/` are never overwritten by an install and never
removed by an uninstall.

Turn **`AUTO_UPDATE` off on a modded server.** A new Valheim build invalidates every
assembly the mods were compiled against, so an unattended SteamCMD update on the next
restart is an unattended way to break the server. The panel says so when both are on.

### The client pack

Valheim refuses a connection when the mod sets do not line up, so every player needs the
same files. **Client-Paket bauen** writes a zip holding the same BepInEx build the server
runs plus every enabled mod marked *für Clients*, laid out so it unpacks straight into the
Valheim game folder next to `valheim.exe`. A `LIESMICH.txt` in the archive says as much.

The *für Clients* box starts off ticked unless Thunderstore tags the package server-side
and not client-side. Untagged packages count as needed on both sides: a mod missing on the
client is a refused connection, a superfluous one is harmless. Building the pack warns when
a mod is in it but one of its libraries is not.

Rebuild the pack after every change, and hand out the new one — the point of failure here
is a player still running last week's zip.

## Crossplay and the join code

The six-digit join code comes from PlayFab, and the server only gets one when **both**
settings are on:

Both are on in a fresh install.

- `CROSSPLAY=1` — passes `-crossplay`.
- `PUBLIC=1` — Valheim registers the PlayFab session only for a public server, and it
  is that registration which hands out the code. With `PUBLIC=0` the log shows
  `New session server "…" that has join code ,` with nothing in it, and the server
  retries `create and join network` every 30 seconds forever. A public server needs a
  password, which the panel already enforces.

The container also needs `libpulse-dev`. PlayFab Party loads the unversioned
`libpulse.so`, and Debian ships that symlink only in the `-dev` package; without it the
server logs `DLL Not Found` once at start and crossplay never comes up. The install
script installs it — containers created before it did need `apt install -y libpulse-dev`
and a restart.

Once both are set, the log reads `Session "…" registered with join code 123456`, and the
panel shows the code under the status header. Without crossplay, players use
**Join IP** with the server's address and port 2456 (UDP 2456–2458 forwarded if they are
outside the LAN).

## Releasing

`fetch_and_deploy_gh_release` and the panel's self-update both read GitHub releases,
so a tag is what publishes an update:

```bash
git tag v0.1.0 && git push --tags
```

The workflow in `.github/workflows/release.yml` publishes
`valheim-panel-linux-x64` as a release asset. Both the asset name and the deployed
binary name (`/opt/valheim-panel/valheim-panel`) are referenced from the install
script and from `SelfUpdate.cs` — change one, change all three.

Self-update never overwrites the running process. The new binary is staged as
`valheim-panel.new` and an `ExecStartPre` in the unit swaps it in on the next start.

## Contributing the script upstream

The Proxmox scripts under `proxmox/` are kept here for direct installs. To submit
them to the community catalog they go into a fork of
[`community-scripts/ProxmoxVED`](https://github.com/community-scripts/ProxmoxVED)
(not ProxmoxVE — that repo is for fixes to already-published scripts):

```
ct/valheim.sh
install/valheim-install.sh
json/valheim.json
```

The boot block at the top of `ct/valheim.sh` is ProxmoxVED's current local-first
form: the engine comes from [`community-scripts/core`](https://github.com/community-scripts/core),
so the same file runs from a checkout, a fork, or a curl pipe. The one addition is
`COMMUNITY_SCRIPTS_URL`, which points the engine back at this repo so it finds
`install/valheim-install.sh` here instead of in ProxmoxVED. **Delete that line when
submitting upstream** — once the scripts live in ProxmoxVED, its default is correct.

## Security

The panel runs as root and shells out to `systemctl` and SteamCMD. Treat it as an
admin interface:

- Keep it on the LAN. Do not expose port 8099 to the internet.
- If you want remote access, put it behind a reverse proxy with its own auth.
- The token in `/opt/valheim-panel/.env` is the only thing between a visitor and a
  shell-equivalent surface. Rotate it if it leaks, and restart the unit.
- Installing a mod runs somebody else's assembly inside the game server. The panel
  rejects archive paths that would climb out of the target directory, but it cannot
  vouch for the code itself — install from authors you have reason to trust.

## Valheim 1.0

Version 1.0 lands on 9 September 2026 with the Deep North biome. Two things matter
for a server:

- The Deep North only generates in terrain that has never been explored. Iron Gate
  recommends a fresh world for the full experience. Decide before launch day —
  switching later costs the group its progress.
- Iron Gate does not guarantee mods will load on 1.0. The panel installs BepInEx on
  request, but whether a given mod survives the update is up to its author — keep
  `AUTO_UPDATE` off while modded so the jump happens when you choose it.

`AUTO_UPDATE=1` (the default) makes the server check SteamCMD on every start, so the
1.0 build arrives with the next restart on its own.

## License

MIT
