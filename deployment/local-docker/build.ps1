#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a full, locally-built Jellyfin Docker image (server + web client).

.DESCRIPTION
    Two-phase build:
      1. The server is published locally with the .NET SDK (`dotnet publish`,
         self-contained linux-x64). This is fast and avoids running the .NET build
         servers inside a container, which can deadlock under buildkit.
      2. The web client is built inside a Node container and both artifacts are
         layered onto the official Jellyfin runtime image.

    Docker may live on the Windows PATH (Docker Desktop) or inside WSL2; the script
    detects the first available option. When Docker is only in WSL2 the build is
    invoked through `wsl.exe` with the context and Dockerfile translated to
    /mnt/<drive>/... paths.

.PARAMETER Tag
    Image tag to produce. Default: jellyfin-local:<server branch>.

.PARAMETER BaseImage
    Official Jellyfin image used as the runtime base. Keep it aligned with your
    server branch (e.g. jellyfin/jellyfin:unstable for 12.x, or a pinned tag).

.PARAMETER WebRef
    Git ref of jellyfin-web to check out when the repository is missing or out of
    date. Default: release-12.z.

.PARAMETER SaveTar
    If set, also export the image to this .tar file (for shipping to a server).

.PARAMETER NoCache
    Pass --no-cache to docker build.

.PARAMETER SkipServerPublish
    Reuse an existing server publish output instead of running `dotnet publish`.

.EXAMPLE
    .\build.ps1 -Tag jellyfin-local:12z -SaveTar .\jellyfin-local-12z.tar
#>
[CmdletBinding()]
param(
    [string]$Tag,
    [string]$BaseImage = 'jellyfin/jellyfin:unstable',
    [string]$WebRef = 'release-12.z',
    [string]$SaveTar,
    [switch]$NoCache,
    [switch]$SkipServerPublish
)

$ErrorActionPreference = 'Stop'

function Get-GitBranch([string]$Repo) {
    $branch = & git -C $Repo rev-parse --abbrev-ref HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch) -or $branch -eq 'HEAD') {
        return 'local'
    }

    return ($branch -replace '[^A-Za-z0-9._-]', '-')
}

function ConvertTo-WslPath([string]$WindowsPath) {
    $full = [System.IO.Path]::GetFullPath($WindowsPath)
    if ($full -match '^([A-Za-z]):\\(.*)$') {
        $drive = $Matches[1].ToLowerInvariant()
        $rest = $Matches[2] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }

    throw "Cannot convert path to WSL form: $WindowsPath"
}

function Quote-Arg([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

function Invoke-Docker([string[]]$DockerArgs) {
    # docker and wsl.exe write their progress to stderr. With $ErrorActionPreference = 'Stop' that
    # would surface as a terminating NativeCommandError even for a successful build, so the native
    # calls run with 'Continue' and the exit code is checked instead.
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        $docker = Get-Command docker -ErrorAction SilentlyContinue
        if ($docker) {
            Write-Host "Running: docker $($DockerArgs -join ' ')`n"
            & docker @DockerArgs
            return $LASTEXITCODE
        }

        if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) {
            throw "Neither 'docker' (Windows) nor 'wsl' is available. Install Docker Desktop or Docker inside WSL2."
        }

        $joined = ($DockerArgs | ForEach-Object { Quote-Arg $_ }) -join ' '
        Write-Host "Running in WSL: docker $joined`n"
        & wsl -e bash -lc "docker $joined"
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
}

# --- Resolve locations -------------------------------------------------------
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$context = Split-Path -Parent $repoRoot
$webRepo = Join-Path $context 'jellyfin-web'
$serverOut = Join-Path $context 'jellyfin-server-build'
$dockerfile = Join-Path $PSScriptRoot 'Dockerfile'
$branch = Get-GitBranch $repoRoot

if (-not $Tag) { $Tag = 'jellyfin-local:' + $branch }

Write-Host "Repository : $repoRoot ($branch)"
Write-Host "Context    : $context"
Write-Host "Web repo   : $webRepo"
Write-Host "Server out : $serverOut"
Write-Host "Tag        : $Tag"
Write-Host "Base image : $BaseImage"

# --- Phase 1: publish the server locally -------------------------------------
if (-not $SkipServerPublish) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet SDK is required on the host to publish the server. Install the .NET SDK (see global.json)."
    }

    if (Test-Path $serverOut) { Remove-Item -Recurse -Force $serverOut }

    Write-Host "`nPublishing server (self-contained linux-x64) ..."
    & dotnet publish (Join-Path $repoRoot 'Jellyfin.Server\Jellyfin.Server.csproj') `
        -c Release -r linux-x64 --self-contained true `
        -o $serverOut --nologo `
        -p:DebugSymbols=false -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}
else {
    if (-not (Test-Path $serverOut)) { throw "-SkipServerPublish was set but $serverOut does not exist." }
    Write-Host "`nReusing existing server publish at $serverOut"
}

# --- Ensure jellyfin-web is present and on the expected ref ------------------
if (-not (Test-Path (Join-Path $webRepo '.git'))) {
    Write-Host "jellyfin-web not found, cloning ($WebRef) ..."
    & git clone --depth 1 --branch $WebRef https://github.com/jellyfin/jellyfin-web.git $webRepo
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone jellyfin-web." }
}
else {
    # Never discard local jellyfin-web work (e.g. local patches/commits). Only update the
    # checkout when the working tree is clean, otherwise build exactly what is checked out.
    $webDirty = (& git -C $webRepo status --porcelain) | Where-Object { $_ -notmatch '^\?\?' }
    $webUnpushed = (& git -C $webRepo log --oneline "origin/$WebRef..HEAD" 2>$null)

    if ($webDirty -or $webUnpushed) {
        Write-Host "jellyfin-web has local changes/commits; building the current checkout and NOT updating it."
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
}

# --- .dockerignore at the context root (docker only reads it from there) -----
$dockerIgnore = Join-Path $context '.dockerignore'
# Note: jellyfin-web/.git is intentionally NOT excluded because its webpack build
# runs `git describe` to stamp the commit SHA. The server repository does not need
# its history for a self-contained publish, so it is excluded to shrink the context.
$dockerIgnoreContent = @'
# Server build outputs / VCS metadata.
jellyfin/.git/
jellyfin/m3u_tv/
jellyfin/**/bin/
jellyfin/**/obj/
# Web client build outputs (recreated inside the image).
jellyfin-web/node_modules/
jellyfin-web/dist/
# Editor metadata.
**/.vs/
**/.vscode/
'@

if (-not (Test-Path $dockerIgnore)) {
    Set-Content -Path $dockerIgnore -Value $dockerIgnoreContent -Encoding utf8
    Write-Host "Created $dockerIgnore"
}

# --- Phase 2: build the image ------------------------------------------------
$useWsl = -not (Get-Command docker -ErrorAction SilentlyContinue)
$dockerfileArg = if ($useWsl) { ConvertTo-WslPath $dockerfile } else { $dockerfile }
$contextArg = if ($useWsl) { ConvertTo-WslPath $context } else { $context }

$buildArgs = @(
    'build',
    '-f', $dockerfileArg,
    '-t', $Tag,
    '--build-arg', "BASE_IMAGE=$BaseImage",
    '--build-arg', "JELLYFIN_VERSION=$branch"
)
if ($NoCache) { $buildArgs += '--no-cache' }
$buildArgs += $contextArg

Write-Host ''
$exit = Invoke-Docker -DockerArgs $buildArgs
if ($exit -ne 0) { throw "Docker build failed (exit $exit)." }

Write-Host "`nBuilt image: $Tag"

# --- Optional export ---------------------------------------------------------
if ($SaveTar) {
    $saveFull = [System.IO.Path]::GetFullPath($SaveTar)
    $saveArg = if ($useWsl) { ConvertTo-WslPath $saveFull } else { $saveFull }
    Write-Host "Exporting image to $saveFull ..."
    $saveExit = Invoke-Docker -DockerArgs @('save', '-o', $saveArg, $Tag)
    if ($saveExit -ne 0) { throw "docker save failed." }
    Write-Host "Saved: $saveFull"
}
