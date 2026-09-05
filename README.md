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
| `/etc/valheim/server.env` | Server settings, written by the panel |
| `/opt/valheim-panel` | Panel binary, `.env`, `VERSION` |

Two systemd units: `valheim` and `valheim-panel`.

`valheim.service` stops with `SIGINT`, which is the only signal Valheim treats as a
clean shutdown. On `SIGTERM` the world is not written and the last session is lost.

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

## Valheim 1.0

Version 1.0 lands on 9 September 2026 with the Deep North biome. Two things matter
for a server:

- The Deep North only generates in terrain that has never been explored. Iron Gate
  recommends a fresh world for the full experience. Decide before launch day —
  switching later costs the group its progress.
- Iron Gate does not guarantee mods will load on 1.0. This setup installs no mod
  loader, which is deliberate.

`AUTO_UPDATE=1` (the default) makes the server check SteamCMD on every start, so the
1.0 build arrives with the next restart on its own.

## License

MIT
