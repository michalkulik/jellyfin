# syntax=docker/dockerfile:1
#
# Local, self-contained Jellyfin image build (packaging stage).
#
# The server is published on the host with the .NET SDK (fast and reliable: it avoids
# running the .NET build servers inside the buildkit sandbox) and the web client is
# built inside a Node container. This image overlays both artifacts on top of the
# official Jellyfin runtime image, which already provides ffmpeg, fonts, GPU/OpenCL
# runtimes and the /jellyfin entrypoint.
#
# Build context: c:\sources (the parent folder of both repositories):
#   <context>/jellyfin/                (server repository)
#   <context>/jellyfin-web/            (web client repository)
#   <context>/jellyfin-server-build/   (dotnet publish output; created by build.ps1)
#
# Prefer the helper script (deployment/local-docker/build.ps1), which performs the
# server publish first and then calls this Dockerfile.

ARG NODE_VERSION=24
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
# Final combined image: official runtime base + host-published server + built web client.
#
FROM ${BASE_IMAGE} AS final

ARG JELLYFIN_VERSION=local

# Locally published server binaries (produced by `dotnet publish` in build.ps1).
# The base image already carries the matching runtime dependencies and ffmpeg.
COPY jellyfin-server-build/ /jellyfin

# Overlay the locally built web client.
COPY --from=web /src/dist /jellyfin/jellyfin-web

LABEL org.opencontainers.image.title="Jellyfin (local build)"
LABEL org.opencontainers.image.description="The Free Software Media System - locally built server and web client"
LABEL org.opencontainers.image.version="${JELLYFIN_VERSION}"
