<#
.SYNOPSIS
    Stops a local documentation server started by Start-DocServer.ps1.

.DESCRIPTION
    Start-DocServer.ps1 runs the Jekyll container with the deterministic name
    `hbmig-jekyll-<port>`. This script removes that container (it was started
    with --rm, so stopping it also removes it). Stopping a non-existent
    container is a graceful no-op.

.PARAMETER Port
    Port of the server to stop (selects container `hbmig-jekyll-<Port>`).
    Default 4000. Ignored when -All is set.

.PARAMETER All
    Stop every running doc server started by Start-DocServer.ps1 (all
    `hbmig-jekyll-*` containers), regardless of port.

.EXAMPLE
    .\Stop-DocServer.ps1
    Stops the server on port 4000.

.EXAMPLE
    .\Stop-DocServer.ps1 -Port 4001

.EXAMPLE
    .\Stop-DocServer.ps1 -All
    Stops all doc servers.

.LINK
    docs/Start-DocServer.ps1
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int] $Port = 4000,

    [switch] $All
)

$ErrorActionPreference = "Stop"

if ( -not (Get-Command docker -ErrorAction SilentlyContinue) ) {
    throw "Docker is not installed or not on PATH."
}
try {
    docker info *> $null
    if ( $LASTEXITCODE -ne 0 ) { throw }
}
catch {
    throw "Docker is installed but the daemon is not reachable."
}

if ( $All ) {
    $names = docker ps -a --filter "name=hbmig-jekyll-" --format "{{.Names}}"
    if ( [string]::IsNullOrWhiteSpace( $names ) ) {
        Write-Host "No doc servers running (no hbmig-jekyll-* containers)." -ForegroundColor DarkGray
        return
    }
    foreach ( $n in ($names -split "`n" | Where-Object { $_.Trim() }) ) {
        docker rm -f $n.Trim() *> $null
        Write-Host "Stopped $($n.Trim())." -ForegroundColor Green
    }
    return
}

$containerName = "hbmig-jekyll-$Port"
$exists = docker ps -a --filter "name=^/$containerName$" --format "{{.Names}}"
if ( [string]::IsNullOrWhiteSpace( $exists ) ) {
    Write-Host "No doc server on port $Port (container '$containerName' not found)." -ForegroundColor DarkGray
    return
}

docker rm -f $containerName *> $null
Write-Host "Stopped $containerName (port $Port)." -ForegroundColor Green
