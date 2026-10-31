# Builds the Kubernetes end-to-end test's resource program as a container image and pushes it to the
# local registry that New-CohesionKindCluster.ps1 runs, where the kind cluster pulls it as
# localhost:5001/cohesion-kubernetes-web:e2e. The program builds against the sibling cohesion
# checkout's packages: run .github/scripts/Pack-CohesionSibling.ps1 -Local first.
[CmdletBinding()]
param(
    [string] $Registry = 'localhost:5001',
    [string] $CohesionVersion = '10.0.0-preview.1.local',
    # The kind nodes' architecture is the podman machine's, which follows the host's: an arm64 Windows
    # machine runs arm64 nodes, which cannot run an x64 image.
    [ValidateSet('', 'linux-x64', 'linux-arm64')][string] $RuntimeIdentifier = ''
)
$ErrorActionPreference = 'Stop'

if (-not $RuntimeIdentifier) {
    $architecture = podman info --format '{{.Host.Arch}}'
    if ($LASTEXITCODE -ne 0) { throw "Read the podman machine's architecture failed (exit $LASTEXITCODE)." }
    $RuntimeIdentifier = switch ($architecture.Trim()) {
        'amd64' { 'linux-x64' }
        'arm64' { 'linux-arm64' }
        default { throw "The podman machine's architecture '$architecture' has no .NET runtime identifier here." }
    }
}

$project = Join-Path $PSScriptRoot '../fixtures/WebProgram/WebProgram.csproj'

# The registry serves plain HTTP; the SDK pushes over HTTP only to registries named here.
$env:DOTNET_CONTAINER_INSECURE_REGISTRIES = $Registry
dotnet publish $project `
    -c Release `
    -r $RuntimeIdentifier `
    -t:PublishContainer `
    -p:ContainerRegistry=$Registry `
    -p:CohesionSiblingPackageVersion=$CohesionVersion `
    -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish the fixture image failed (exit $LASTEXITCODE)." }
