<#
.SYNOPSIS
    Serves the Hyperbee Migrations Jekyll documentation site locally via Docker.

.DESCRIPTION
    Runs the site in docs/site/ using the jekyll/jekyll Docker image -- no local
    Ruby/Bundler install required. The container runs `bundle install` (first run
    fetches the just-the-docs remote theme from GitHub, so network is required)
    then `jekyll serve`.

    By default the production baseurl ("/hyperbee.migrations/") is overridden to
    "" so the site is reachable at the host root. Press Ctrl+C to stop.

.PARAMETER Port
    Host port to bind. Default 4000.

.PARAMETER LiveReload
    Enable Jekyll live reload (rebuild + browser refresh on file change).

.PARAMETER ProductionBaseUrl
    Keep the production baseurl ("/hyperbee.migrations/") instead of "". The
    site is then at http://localhost:<Port>/hyperbee.migrations/. Use this to
    reproduce production link behavior locally.

.PARAMETER Image
    jekyll/jekyll image tag. Default "jekyll/jekyll:4.3.2" (matches the
    Gemfile's `jekyll "~> 4.3"`).

.PARAMETER GitHubToken
    Optional GitHub token exported as JEKYLL_GITHUB_TOKEN, to avoid anonymous
    rate-limiting when jekyll-remote-theme fetches the theme.

.EXAMPLE
    .\Run-DocSite.ps1
    Serve at http://localhost:4000/

.EXAMPLE
    .\Run-DocSite.ps1 -Port 8080 -LiveReload
    Serve at http://localhost:8080/ with live reload.

.EXAMPLE
    .\Run-DocSite.ps1 -ProductionBaseUrl
    Serve at http://localhost:4000/hyperbee.migrations/ (production link layout).
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int] $Port = 4000,

    [switch] $LiveReload,

    [switch] $ProductionBaseUrl,

    [string] $Image = "jekyll/jekyll:4.3.2",

    [string] $GitHubToken
)

$ErrorActionPreference = "Stop"

# Site root is this script's directory (docs/site), independent of caller CWD.
$siteDir = $PSScriptRoot

if ( -not (Test-Path (Join-Path $siteDir "_config.yml")) ) {
    throw "No _config.yml found in '$siteDir'. This script must live in docs/site/."
}

if ( -not (Get-Command docker -ErrorAction SilentlyContinue) ) {
    throw "Docker is not installed or not on PATH. Install Docker Desktop, or use the native-Ruby path (see docs/site Gemfile)."
}

try {
    docker info *> $null
    if ( $LASTEXITCODE -ne 0 ) { throw }
}
catch {
    throw "Docker is installed but the daemon is not reachable. Start Docker Desktop and retry."
}

# Build the in-container jekyll serve command.
$baseUrlArg = if ( $ProductionBaseUrl ) { "" } else { "--baseurl ''" }
$liveArg    = if ( $LiveReload ) { "--livereload" } else { "" }
$serveCmd   = "bundle install && bundle exec jekyll serve --host 0.0.0.0 --port 4000 $baseUrlArg $liveArg".Trim()

# docker run arguments.
$dockerArgs = @(
    "run", "--rm", "-it",
    "-v", "${siteDir}:/srv/jekyll",
    "-p", "${Port}:4000"
)
# Live reload uses port 35729; map it through when enabled.
if ( $LiveReload ) {
    $dockerArgs += @("-p", "35729:35729")
}
if ( $GitHubToken ) {
    $dockerArgs += @("-e", "JEKYLL_GITHUB_TOKEN=$GitHubToken")
}
$dockerArgs += @($Image, "sh", "-c", $serveCmd)

$shownUrl = if ( $ProductionBaseUrl ) {
    "http://localhost:$Port/hyperbee.migrations/"
} else {
    "http://localhost:$Port/"
}

Write-Host "Serving docs/site via $Image" -ForegroundColor Cyan
Write-Host "  URL:        $shownUrl" -ForegroundColor Cyan
Write-Host "  LiveReload: $([bool]$LiveReload)" -ForegroundColor Cyan
Write-Host "  (first run fetches the just-the-docs remote theme -- network required)" -ForegroundColor DarkGray
Write-Host "  Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

& docker @dockerArgs
exit $LASTEXITCODE
