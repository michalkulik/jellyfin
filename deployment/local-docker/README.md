# Local Docker image build

Builds a **full, runnable Jellyfin image** (server + web client) from your local
repositories, without the heavy official packaging pipeline.

The image is produced by adding the locally built server binaries and web client
on top of the official `jellyfin/jellyfin` runtime image, which already provides
ffmpeg, fonts, GPU/OpenCL runtimes and the `/jellyfin` entrypoint.

The build is intentionally **two-phase**:

1. The server is published **on the host** with the .NET SDK (`dotnet publish`,
   self-contained `linux-x64`). This is fast and avoids running the .NET build
   servers inside the container, which can deadlock under buildkit.
2. The **web client is built inside a Node container**, then both artifacts are
   layered onto the official runtime image.

## What is needed

| Requirement | Notes |
|---|---|
| **Docker** | Either Docker Desktop (WSL 2 backend, Linux containers) on Windows PATH, or Docker installed **inside WSL2** (as in this setup). The script auto-detects and invokes WSL when Docker is not on the Windows PATH. |
| **.NET SDK** | On the Windows host, matching `global.json` (10.0), to publish the server. |
| **jellyfin-web source** | Branch matching the server branch. The script clones/normalises it automatically (`release-12.z` by default). Node is only needed *inside* the build container. |
| **This repository** | Already present. The build context must be the **parent** folder that contains both `jellyfin/` and `jellyfin-web/`. |
| Internet access | To pull base images (`node`, `jellyfin/jellyfin`) and NuGet/npm packages. |

The helper script handles prerequisites and produces the image; it does **not**
install Docker for you (that needs admin rights).

## One-time setup

Docker is only needed in **one** of these places:

- **WSL2 (recommended when you cannot install Docker Desktop)** – install Docker
  Engine inside your WSL distro and make sure `docker info` works from `wsl`.
- **Docker Desktop for Windows** – install as administrator, enable **Use WSL 2
  based engine**, then verify `docker info` on the Windows PATH.

Verify:

```powershell
docker info            # Docker Desktop
# or
wsl -e docker info     # Docker inside WSL2
```

## Build

From the repository root:

```powershell
# Full build (server branch is used in the tag automatically)
.\deployment\local-docker\build.ps1

# Explicit tag, pinned base image matching 12.x, and export a tar for the server
.\deployment\local-docker\build.ps1 `
    -Tag jellyfin-local:12z `
    -BaseImage jellyfin/jellyfin:unstable `
    -SaveTar .\jellyfin-local-12z.tar
```

Useful parameters:

| Parameter | Default | Purpose |
|---|---|---|
| `-Tag` | `jellyfin-local:<branch>` | Output image tag |
| `-BaseImage` | `jellyfin/jellyfin:unstable` | Official runtime base; align with your branch |
| `-WebRef` | `release-12.z` | jellyfin-web git ref to use |
| `-SaveTar` | – | Also export the image to a `.tar` for transfer |
| `-NoCache` | off | Rebuild without Docker layer cache |
| `-SkipServerPublish` | off | Reuse an existing `../jellyfin-server-build` output |
| `-WebRef` | `release-12.z` | jellyfin-web git ref to use |

## Run locally

```powershell
docker run -d --name jellyfin-local -p 8096:8096 `
  -v ${PWD}\config:/config -v ${PWD}\cache:/cache `
  jellyfin-local:12z
```

Then open <http://localhost:8096>.

## Ship to the Ubuntu server

```powershell
# Export on Windows
docker save -o jellyfin-local-12z.tar jellyfin-local:12z
scp .\jellyfin-local-12z.tar user@server:/tmp/

# On the server
docker load -i /tmp/jellyfin-local-12z.tar
docker run -d --name jellyfin -p 8096:8096 `
  -v /srv/jellyfin/config:/config -v /srv/jellyfin/cache:/cache `
  jellyfin-local:12z
```

## How it works

1. **Host publish** – `build.ps1` runs `dotnet publish` of `Jellyfin.Server` for
   `linux-x64` (self-contained) into `<context>/jellyfin-server-build`.
2. **web** stage – `node:24-alpine`, runs `npm ci` + `npm run build:production`,
   producing `dist/`.
3. **final** stage – starts from `BASE_IMAGE` (official Jellyfin) and overlays
   `jellyfin-server-build/` into `/jellyfin` and the web `dist/` into
   `/jellyfin/jellyfin-web`.

This keeps local builds fast while still yielding a complete image.

> Note: an earlier all-in-container variant ran `dotnet publish` inside a
> `dotnet/sdk` stage. That can hang in `futex`/`wait_for_partner` under buildkit;
> publishing on the host avoids it entirely.

## Troubleshooting

- **"Cannot talk to the Docker daemon"** – Docker is not running, or is using
  Windows containers. Start Docker (Desktop or inside WSL2) and use Linux
  containers.
- **Build hangs at `dotnet publish`** – this is why the server is published on
  the host instead of in a container. If you reintroduce a container publish,
  pass `-p:UseSharedCompilation=false -nodeReuse:false`.
- **`permission denied` for `/jellyfin/jellyfin`** – happens when executable bits
  are lost (e.g. `scp` from Windows). This Dockerfile preserves them via `COPY`
  from the host publish output; if you copy manually, run `chmod +x`.
- **Web client shows an old UI** – ensure `jellyfin-web` is on the same branch as
  the server, then rebuild with `-NoCache`.
- **Wrong base runtime version** – set `-BaseImage` to a tag that matches your
  server branch (e.g. `jellyfin/jellyfin:12.1`).
