# Builds the Docker end-to-end test's resource program as a container image and loads it into the
# local engine's image store, where the Docker platform runs it as cohesion-docker-web:e2e. The
# program builds against the sibling cohesion checkout's packages: run
# .github/scripts/Pack-CohesionSibling.ps1 -Local first.
[CmdletBinding()]
param(
    # The engine whose store receives the image: Podman or Docker; the first one on PATH, Podman first.
    [ValidateSet('', 'Podman', 'Docker')][string] $Engine = '',
    [string] $CohesionVersion = '10.0.0-preview.1.local',
    # The image runs on the engine's architecture: a Podman machine on an arm64 Windows host is
    # linux-arm64 and cannot run an x64 image. Empty asks the engine.
    [ValidateSet('', 'linux-x64', 'linux-arm64')][string] $RuntimeIdentifier = ''
)
$ErrorActionPreference = 'Stop'

if (-not $Engine) {
    $Engine = if (Get-Command podman -ErrorAction SilentlyContinue) { 'Podman' }
              elseif (Get-Command docker -ErrorAction SilentlyContinue) { 'Docker' }
              else { throw 'Neither podman nor docker is on PATH.' }
}

if (-not $RuntimeIdentifier) {
    $architecture = if ($Engine -eq 'Podman') { podman info --format '{{.Host.Arch}}' } else { docker version --format '{{.Server.Arch}}' }
    if ($LASTEXITCODE -ne 0) { throw "Read the engine's architecture failed (exit $LASTEXITCODE)." }
    $RuntimeIdentifier = switch ($architecture.Trim()) {
        'amd64' { 'linux-x64' }
        'arm64' { 'linux-arm64' }
        default { throw "The engine's architecture '$architecture' has no .NET runtime identifier here." }
    }
}

$project = Join-Path $PSScriptRoot '../fixtures/WebProgram/WebProgram.csproj'

# LocalRegistry hands the image to the engine's CLI (podman load or docker load) instead of a registry.
dotnet publish $project `
    -c Release `
    -r $RuntimeIdentifier `
    -t:PublishContainer `
    -p:LocalRegistry=$Engine `
    -p:CohesionSiblingPackageVersion=$CohesionVersion `
    -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish the fixture image failed (exit $LASTEXITCODE)." }
Write-Host "Loaded cohesion-docker-web:e2e ($RuntimeIdentifier) into $Engine."
