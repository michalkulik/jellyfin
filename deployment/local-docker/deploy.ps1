#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the Jellyfin image, pushes it to Docker Hub and redeploys it on the Ubuntu server.

.DESCRIPTION
    This is the ONLY supported way to ship a web or server change. The running container is never
    patched by hand: the image is the single source of truth, so a change exists on the server only
    after it was built, pushed and pulled.

    Steps:
      1. build.ps1   -> local image (server published on the host, web built in a node container)
      2. docker push -> mkulik91/jellyfin-but-better:latest, digest verified afterwards
      3. docker pull -> on the server, then `docker compose up -d` for the jellyfin stack
      4. verify      -> container health, image digest, startup log and the web bundle

.PARAMETER Tag
    Image to publish. Default: mkulik91/jellyfin-but-better:latest.

.PARAMETER Server
    SSH target of the Docker host. Default: root@ubuntu.

.PARAMETER ComposeDir
    Directory holding the compose file on the server.

.PARAMETER ComposeProject
    Compose project name. Default: jellyfin.

.PARAMETER SkipBuild
    Reuse the locally tagged image instead of rebuilding (for a retry after a failed push).

.PARAMETER SkipServerPublish
    Passed to build.ps1: reuse the existing ../jellyfin-server-build output.

.EXAMPLE
    .\deploy.ps1 -SkipServerPublish
#>
[CmdletBinding()]
param(
    [string]$Tag = 'mkulik91/jellyfin-but-better:latest',
    [string]$Server = 'root@ubuntu',
    [string]$ComposeDir = '/srv/share/docker/portainer/compose/58',
    [string]$ComposeProject = 'jellyfin',
    [switch]$SkipBuild,
    [switch]$SkipServerPublish
)

$ErrorActionPreference = 'Stop'

function Invoke-Docker([string[]]$DockerArgs) {
    # docker and wsl.exe report progress on stderr; keep that from looking like a failure.
    # Output is forwarded to the host so the caller only ever sees the exit code.
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $docker = Get-Command docker -ErrorAction SilentlyContinue
        if ($docker) {
            & docker @DockerArgs | Out-Host
            return $LASTEXITCODE
        }

        $joined = ($DockerArgs | ForEach-Object {
                if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
            }) -join ' '
        & wsl -e bash -lc "docker $joined" | Out-Host
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
}

function Get-DockerOutput([string[]]$DockerArgs) {
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $docker = Get-Command docker -ErrorAction SilentlyContinue
        if ($docker) {
            return (& docker @DockerArgs 2>$null)
        }

        $joined = ($DockerArgs | ForEach-Object {
                if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
            }) -join ' '
        return (& wsl -e bash -lc "docker $joined" 2>$null)
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
}

function Invoke-Remote([string]$Command) {
    # ssh writes progress to stderr; keep that from looking like a failure, and forward stdout to the
    # host so the caller only ever sees the exit code.
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & ssh -o ConnectTimeout=20 $Server $Command | Out-Host
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
}

function Get-RemoteOutput([string]$Command) {
    # Captures the remote stdout without letting stderr abort the script.
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        return (& ssh -o ConnectTimeout=20 $Server $Command 2>$null)
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
}

# --- 1. Build ----------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host "`n=== Building $Tag ===`n" -ForegroundColor Cyan
    $buildArgs = @{ Tag = $Tag }
    if ($SkipServerPublish) { $buildArgs.SkipServerPublish = $true }
    & (Join-Path $PSScriptRoot 'build.ps1') @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Image build failed." }
}
else {
    Write-Host "`n=== Reusing the local image $Tag ===`n" -ForegroundColor Cyan
    if ((Invoke-Docker @('image', 'inspect', $Tag)) -ne 0) { throw "Local image $Tag does not exist." }
}

# --- 2. Push -----------------------------------------------------------------
Write-Host "`n=== Pushing $Tag ===`n" -ForegroundColor Cyan
if ((Invoke-Docker @('push', $Tag)) -ne 0) { throw "docker push failed." }

Write-Host "`nPublished digest:"
Get-DockerOutput @('buildx', 'imagetools', 'inspect', $Tag) | Select-String '^Digest:'

# --- 3. Pull and redeploy on the server --------------------------------------
Write-Host "`n=== Pulling on $Server ===`n" -ForegroundColor Cyan
if ((Invoke-Remote "docker pull $Tag") -ne 0) { throw "docker pull on the server failed." }

Write-Host "`n=== Redeploying the $ComposeProject stack ===`n" -ForegroundColor Cyan
$compose = "cd $ComposeDir && docker compose -p $ComposeProject up -d"
if ((Invoke-Remote $compose) -ne 0) { throw "docker compose up failed." }

# --- 4. Verify ---------------------------------------------------------------
Write-Host "`n=== Verifying ===`n" -ForegroundColor Cyan
Start-Sleep -Seconds 20
$verify = @(
    "docker inspect $ComposeProject --format 'image={{.Image}} health={{.State.Health.Status}}'",
    "docker logs $ComposeProject 2>&1 | grep -E 'Jellyfin version|Startup complete' | tail -2",
    "docker exec $ComposeProject sh -c 'ls -la /jellyfin/jellyfin-web/main.jellyfin.bundle.js'"
) -join '; '
Invoke-Remote $verify

$state = (Get-RemoteOutput "docker inspect $ComposeProject --format '{{.State.Health.Status}}'").Trim()
if ($state -ne 'healthy') { throw "The container is not healthy ($state)." }

Write-Host "`nDeployed $Tag" -ForegroundColor Green
