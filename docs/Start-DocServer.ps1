<#
.SYNOPSIS
    Serves the Hyperbee Migrations Jekyll documentation site locally via Docker.

.DESCRIPTION
    A PowerShell wrapper around the canonical recipe in docs/local-jekyll.md:
    a throwaway `ruby:3.3` container that runs `bundle install` then
    `jekyll serve` against docs/site/. No local Ruby/Bundler install required;
    Docker is the only dependency.

    Faithful to docs/local-jekyll.md:
      - ruby:3.3 (Debian/glibc -- avoids the Alpine/musl sass-embedded bug;
        the jekyll/jekyll image is unmaintained at 4.2.2 and violates the
        Gemfile's `jekyll "~> 4.3"`).
      - -w /srv/jekyll working dir; bind-mounts docs/site there.
      - --force_polling (required for file-change detection on Windows
        bind mounts).
      - A named bundle-cache volume so repeat starts skip the ~20s gem
        install (docs/local-jekyll.md "Speeding up repeat starts").
      - Production baseurl is kept; browse at the baseurl path. Use
        -RootBaseUrl to override baseurl to "" and browse at the host root.

    Press Ctrl+C to stop (the --rm container is removed automatically); or
    from another shell: docker stop <container-name> (printed on start).

.PARAMETER Port
    Host port to publish (container port is always 4000). Default 4000.

.PARAMETER RootBaseUrl
    Override the production baseurl ("/hyperbee.migrations/") to "" so the
    site is reachable at http://localhost:<Port>/ instead of
    http://localhost:<Port>/hyperbee.migrations/. Convenience for quick
    browsing; the default keeps the production link layout.

.PARAMETER NoBundleCache
    Do not mount the shared `jekyll-bundle-cache` Docker volume. Every start
    re-installs gems from scratch (~20s). Use for a clean-room dependency
    resolution check.

.PARAMETER Image
    Docker image to run. Default "ruby:3.3". Must be a Ruby image with a
    compiler toolchain (the default `ruby:3.x`, not `-slim`).

.PARAMETER GitHubToken
    Optional GitHub token exported as JEKYLL_GITHUB_TOKEN, to avoid anonymous
    rate-limiting when jekyll-remote-theme fetches the just-the-docs theme.

.EXAMPLE
    .\Run-DocSite.ps1
    Serve at http://localhost:4000/hyperbee.migrations/

.EXAMPLE
    .\Run-DocSite.ps1 -RootBaseUrl
    Serve at http://localhost:4000/

.EXAMPLE
    .\Run-DocSite.ps1 -Port 4001 -NoBundleCache
    Serve at http://localhost:4001/hyperbee.migrations/ with a clean gem install.

.LINK
    docs/local-jekyll.md
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int] $Port = 4000,

    [switch] $RootBaseUrl,

    [switch] $NoBundleCache,

    [string] $Image = "ruby:3.3",

    [string] $GitHubToken
)

$ErrorActionPreference = "Stop"

# This script lives in docs/; the Jekyll site is docs/site/. Anchored to the
# script's own directory so it works regardless of the caller's CWD.
$siteDir = Join-Path $PSScriptRoot "site"

if ( -not (Test-Path (Join-Path $siteDir "_config.yml")) ) {
    throw "No _config.yml found in '$siteDir'. Expected this script in docs/ with the Jekyll site in docs/site/."
}
if ( -not (Test-Path (Join-Path $siteDir "Gemfile")) ) {
    throw "No Gemfile found in '$siteDir'."
}

if ( -not (Get-Command docker -ErrorAction SilentlyContinue) ) {
    throw "Docker is not installed or not on PATH. See docs/local-jekyll.md (First-time setup)."
}
try {
    docker info *> $null
    if ( $LASTEXITCODE -ne 0 ) { throw }
}
catch {
    throw "Docker is installed but the daemon is not reachable. Start Docker Desktop and retry."
}

$containerName = "hbmig-jekyll-$Port"

# Stop any prior instance bound to this name so re-runs are idempotent.
docker rm -f $containerName *> $null

# In-container command: install gems (cached via the bundle volume unless
# disabled), then serve with the Windows-bind-mount-safe polling watcher.
$serveParts = @(
    "bundle install --quiet",
    "bundle exec jekyll serve --host 0.0.0.0 --port 4000 --force_polling"
)
if ( $RootBaseUrl ) {
    $serveParts[1] += " --baseurl ''"
}
$serveCmd = $serveParts -join " && "

# Allocate a TTY (-t) only for an interactive console; under automation
# (input redirected) `-t` fails with "the input device is not a TTY".
$runFlags = if ( -not [Console]::IsInputRedirected ) { "-it" } else { "-i" }

$dockerArgs = @(
    "run", "--rm", $runFlags,
    "--name", $containerName,
    "-v", "${siteDir}:/srv/jekyll",
    "-w", "/srv/jekyll",
    "-p", "${Port}:4000"
)
if ( -not $NoBundleCache ) {
    $dockerArgs += @("-v", "jekyll-bundle-cache:/usr/local/bundle")
}
if ( $GitHubToken ) {
    $dockerArgs += @("-e", "JEKYLL_GITHUB_TOKEN=$GitHubToken")
}
$dockerArgs += @($Image, "sh", "-c", $serveCmd)

$baseUrlPath = if ( $RootBaseUrl ) { "" } else { "/hyperbee.migrations/" }
$shownUrl = "http://localhost:$Port$baseUrlPath"

# OSC 8 terminal hyperlink: ESC ]8;;URI ESC \ text ESC ]8;; ESC \
# Rendered clickable by VS Code's terminal, Windows Terminal, iTerm2, etc.;
# terminals without OSC 8 still show the URL text (kept identical to the URI).
$esc = [char]27
$clickableUrl = "$esc]8;;$shownUrl$esc\$shownUrl$esc]8;;$esc\"

Write-Host "Serving docs/site via $Image  (per docs/local-jekyll.md)" -ForegroundColor Cyan
Write-Host "  Container:  $containerName   (stop: docker stop $containerName)" -ForegroundColor Cyan
Write-Host "  BundleCache:$([bool](-not $NoBundleCache))   first run also fetches the just-the-docs remote theme (network required)" -ForegroundColor DarkGray
Write-Host "  Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Launch -> " -ForegroundColor Green -NoNewline
Write-Host $clickableUrl -ForegroundColor Green
Write-Host "  (the link is live once Jekyll prints 'Server running' below)" -ForegroundColor DarkGray
Write-Host ""

& docker @dockerArgs
exit $LASTEXITCODE
