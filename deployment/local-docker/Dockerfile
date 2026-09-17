# syntax=docker/dockerfile:1
#
# Local, self-contained Jellyfin image build.
#
# Unlike the official jellyfin-packaging Dockerfile (which installs Debian packages,
# ffmpeg, Intel/Rockchip OpenCL runtimes, east-asian fonts, jemalloc, ...), this build
# reuses the official runtime image as its final base and only replaces the server
# binaries and the web client with locally built artifacts. This keeps the build fast
# and reproducible on a developer machine while still producing a full, runnable image.
#
# Required build context: the parent directory that contains BOTH repositories:
#   <context>/jellyfin/       (this repository, the server)
#   <context>/jellyfin-web/   (jellyfin web client, branch must match the server)
#
# Build example (from the repository root):
#   docker buildx build --platform linux/amd64 \
#     -f deployment/local-docker/Dockerfile \
#     -t jellyfin-local:12z \
#     --build-arg BASE_IMAGE=jellyfin/jellyfin:unstable \
#     ../..
#
# See README.md in this folder for the helper script.

ARG NODE_VERSION=24
ARG DOTNET_VERSION=10.0
# Official Jellyfin image used for the runtime layer (ffmpeg, fonts, GPU runtimes, ...).
# Keep this aligned with the server branch you are building (e.g. :unstable for 12.x).
ARG BASE_IMAGE=jellyfin/jellyfin:unstable

#
# Build the web client.
#
FROM node:${NODE_VERSION}-alpine AS web

WORKDIR /src

# Build dependencies mirroring the official packaging Dockerfile.
RUN apk add --no-cache \
    autoconf \
    automake \
    g++ \
    gcc \
    gifsicle \
    git \
    libpng-dev \
    libtool \
    make \
    musl-dev \
    nasm \
    python3

COPY jellyfin-web/ ./

RUN npm ci --no-audit --unsafe-perm \
    && npm run build:production

#
# Build the server (self-contained, linux-x64).
#
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS server

ARG DOTNET_ARCH=x64
ARG CONFIG=Release

WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY jellyfin/ ./

RUN dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
    --arch ${DOTNET_ARCH} \
    --configuration ${CONFIG} \
    --output=/out \
    --self-contained \
    -p:DebugSymbols=false \
    -p:DebugType=none

#
# Final combined image: official runtime base + our server + our web client.
#
FROM ${BASE_IMAGE} AS final

ARG JELLYFIN_VERSION=local

# Overlay the locally built server binaries (the base image already carries the
# matching runtime dependencies, ffmpeg and the /jellyfin entrypoint).
COPY --from=server /out /jellyfin

# Overlay the locally built web client.
COPY --from=web /src/dist /jellyfin/jellyfin-web

LABEL org.opencontainers.image.title="Jellyfin (local build)"
LABEL org.opencontainers.image.description="The Free Software Media System - locally built server and web client"
LABEL org.opencontainers.image.version="${JELLYFIN_VERSION}"
