# Local Docker image build

Builds a **full, runnable Jellyfin image** (server + web client) from your local
repositories, without the heavy official packaging pipeline.

The image is produced by adding the locally built server binaries and web client
on top of the official `jellyfin/jellyfin` runtime image, which already provides
ffmpeg, fonts, GPU/OpenCL runtimes and the `/jellyfin` entrypoint.

## What is needed

| Requirement | Notes |
|---|---|
| **Docker Desktop for Windows** | With the **WSL 2** backend and **Linux containers** enabled. Must be installed by an administrator. After install, verify `docker info` works. |
| **jellyfin-web source** | Branch matching the server branch. The script clones/normalises it automatically (`release-12.z` by default). |
| **This repository** | Already present. The build context must be the **parent** folder that contains both `jellyfin/` and `jellyfin-web/`. |
| Internet access | To pull base images (`node`, `dotnet/sdk`, `jellyfin/jellyfin`) and NuGet/npm packages. |

The helper script handles prerequisites and produces the image; it does **not**
install Docker for you (that needs admin rights).

## One-time setup

1. **Install Docker Desktop** (administrator required):
   - `winget install --id Docker.DockerDesktop -e`  *(from an elevated shell)*
   - or download from <https://www.docker.com/products/docker-desktop/>
   - During setup enable **Use WSL 2 based engine**.
   - Reboot if prompted, open Docker Desktop, wait until it reports *Engine running*.

2. Verify:
   ```powershell
   docker info
   docker run --rm hello-world
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

`Dockerfile` has three stages:

1. **web** – `node:24-alpine`, runs `npm ci` + `npm run build:production`, producing `dist/`.
2. **server** – `mcr.microsoft.com/dotnet/sdk:10.0`, runs a self-contained
   `dotnet publish` of `Jellyfin.Server` for `linux-x64`.
3. **final** – starts from `BASE_IMAGE` (official Jellyfin) and overlays the two
   artifacts into `/jellyfin` and `/jellyfin/jellyfin-web`.

This keeps local builds fast while still yielding a complete image.

## Troubleshooting

- **"Cannot talk to the Docker daemon"** – Docker Desktop is not running (or is
  using Windows containers). Start it and switch to Linux containers.
- **`permission denied` for `/jellyfin/jellyfin`** – only happens in overlay
  approaches where executable bits are lost; this Dockerfile preserves them via
  `COPY` from the build stage.
- **Web client shows an old UI** – ensure `jellyfin-web` is on the same branch as
  the server, then rebuild with `-NoCache`.
- **Wrong base runtime version** – set `-BaseImage` to a tag that matches your
  server branch (e.g. `jellyfin/jellyfin:12.1`).
