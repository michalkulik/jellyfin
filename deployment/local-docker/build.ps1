#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a full, locally-built Jellyfin Docker image (server + web client).

.DESCRIPTION
    Requires Docker Desktop (Linux containers). The build context is the parent
    directory containing both the "jellyfin" server repository and the
    "jellyfin-web" repository.

.PARAMETER Tag
    Image tag to produce. Default: jellyfin-local:<server branch>.

.PARAMETER BaseImage
    Official Jellyfin image used as the runtime base. Keep it aligned with your
    server branch (e.g. jellyfin/jellyfin:unstable for 12.x, or a pinned tag).

.PARAMETER WebRef
    Git ref of jellyfin-web to check out when the repository is missing or
    out of date. Default: release-12.z.

.PARAMETER SaveTar
    If set, also export the image to this .tar file (for shipping to a server).

.PARAMETER NoCache
    Pass --no-cache to docker buildx.

.EXAMPLE
    .\build.ps1 -Tag jellyfin-local:12z -SaveTar .\jellyfin-local-12z.tar
#>
[CmdletBinding()]
param(
    [string]$Tag,
    [string]$BaseImage = 'jellyfin/jellyfin:unstable',
    [string]$WebRef = 'release-12.z',
    [string]$SaveTar,
    [switch]$NoCache
)

$ErrorActionPreference = 'Stop'

function Resolve-RepoRoot {
    # This script lives in <repo>/deployment/local-docker/
    return (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

function Get-GitBranch([string]$Repo) {
    $branch = & git -C $Repo rev-parse --abbrev-ref HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch) -or $branch -eq 'HEAD') {
        return 'local'
    }

    return ($branch -replace '[^A-Za-z0-9._-]', '-')
}

$repoRoot = Resolve-RepoRoot
$context = Split-Path -Parent $repoRoot
$webRepo = Join-Path $context 'jellyfin-web'

if (-not $Tag) {
    $Tag = 'jellyfin-local:' + (Get-GitBranch $repoRoot)
}

Write-Host "Repository : $repoRoot"
Write-Host "Context    : $context"
Write-Host "Web repo   : $webRepo"
Write-Host "Tag        : $Tag"
Write-Host "Base image : $BaseImage"

# --- Prerequisites -----------------------------------------------------------
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker is not available on PATH. Install Docker Desktop (Linux containers) first."
}

& docker info *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Cannot talk to the Docker daemon. Start Docker Desktop and try again."
}

# --- Ensure jellyfin-web is present and on the expected ref ------------------
if (-not (Test-Path (Join-Path $webRepo '.git'))) {
    Write-Host "jellyfin-web not found, cloning ($WebRef) ..."
    & git clone --depth 1 --branch $WebRef https://github.com/jellyfin/jellyfin-web.git $webRepo
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone jellyfin-web." }
}
else {
    Write-Host "Updating jellyfin-web ($WebRef) ..."
    & git -C $webRepo fetch --depth 1 origin $WebRef
    if ($LASTEXITCODE -eq 0) {
        & git -C $webRepo checkout FETCH_HEAD
    }
    else {
        Write-Warning "Could not fetch '$WebRef'; using the current jellyfin-web checkout."
    }
}

# --- .dockerignore at the context root (docker only reads it from there) -----
$dockerIgnore = Join-Path $context '.dockerignore'
$dockerIgnoreContent = @'
# Build outputs and VCS metadata are not needed inside the image build.
**/bin/
**/obj/
**/.git/
**/node_modules/
**/dist/
**/.vs/
**/.vscode/
jellyfin/m3u_tv/
'@

if (-not (Test-Path $dockerIgnore)) {
    Set-Content -Path $dockerIgnore -Value $dockerIgnoreContent -Encoding utf8
    Write-Host "Created $dockerIgnore"
}

# --- Build -------------------------------------------------------------------
$dockerfile = Join-Path $PSScriptRoot 'Dockerfile'
$buildArgs = @(
    'buildx', 'build',
    '--platform', 'linux/amd64',
    '-f', $dockerfile,
    '-t', $Tag,
    '--build-arg', "BASE_IMAGE=$BaseImage",
    '--build-arg', ("JELLYFIN_VERSION=" + (Get-GitBranch $repoRoot)),
    '--load',
    $context
)

if ($NoCache) { $buildArgs += '--no-cache' }

Write-Host "`nRunning: docker $($buildArgs -join ' ')`n"
& docker @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Docker build failed." }

Write-Host "`nBuilt image: $Tag"

if ($SaveTar) {
    Write-Host "Exporting image to $SaveTar ..."
    & docker save -o $SaveTar $Tag
    if ($LASTEXITCODE -ne 0) { throw "docker save failed." }
    Write-Host "Saved: $SaveTar"
}
