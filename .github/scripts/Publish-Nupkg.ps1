#Requires -Version 5.1
<#
.SYNOPSIS
    Pushes a .nupkg to GitHub Packages, replacing any existing version.

.DESCRIPTION
    GitHub Packages NuGet does not allow re-pushing an existing (id, version)
    pair via `dotnet nuget push`. For QA/UAT pipelines where the version stays
    constant (e.g., 10.0.0 while iterating on main) and we WANT the previous
    push to be replaced, the workflow is:

      1. GET  /orgs/<owner>/packages/nuget/<id-lowercase>/versions
         Returns versions with .id (numeric) and .name (semver).
      2. Find the entry matching the .nupkg's semver. If found, DELETE its id.
         404 from the GET means no such package yet -- nothing to delete.
      3. `dotnet nuget push` the .nupkg.

    The workflow's GITHUB_TOKEN needs packages: write permission. That scope
    covers both upload and delete; no separate delete:packages needed.

    No --skip-duplicate: if the delete somehow failed but reported success,
    we want the workflow to turn RED rather than silently leave the old
    version in place. Silent skip would defeat the purpose of this script.

.PARAMETER NupkgPath
    Path to the .nupkg file to publish.

.PARAMETER Owner
    GitHub organization (or user) that owns the package. Defaults to the
    GITHUB_REPOSITORY_OWNER env var that workflows always set.

.EXAMPLE
    ./Publish-Nupkg.ps1 -NupkgPath ./_out/packages/Assimalign.Cohesion.Core.10.0.0.nupkg
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$NupkgPath,

    [string]$Owner = $env:GITHUB_REPOSITORY_OWNER
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $NupkgPath)) {
    throw "Package not found: $NupkgPath"
}
if (-not $Owner) {
    throw "Owner not specified and GITHUB_REPOSITORY_OWNER env var is unset."
}
if (-not $env:GITHUB_TOKEN) {
    throw "GITHUB_TOKEN env var must be set so gh api + dotnet nuget push can authenticate."
}

# Parse the package id and version out of the filename.
#   Assimalign.Cohesion.Core.10.0.0.nupkg
#     -> id  = "Assimalign.Cohesion.Core"
#     -> ver = "10.0.0"
# Handles SemVer 2.0 pre-release (-foo) and build-metadata (+bar) suffixes.
$fileName = [System.IO.Path]::GetFileNameWithoutExtension($NupkgPath)
if ($fileName -notmatch '^(?<id>.+?)\.(?<ver>\d+\.\d+\.\d+(?:-[\w.-]+)?(?:\+[\w.-]+)?)$') {
    throw "Could not parse package id and version from filename '$fileName'."
}
$packageId      = $matches['id']
$version        = $matches['ver']
$packageIdLower = $packageId.ToLowerInvariant()

$versionsApi = "/orgs/$Owner/packages/nuget/$packageIdLower/versions"
$source      = "https://nuget.pkg.github.com/$Owner/index.json"

Write-Host "$packageId @ $version"

# --- Delete any existing copy of this exact version ---------------------------
# `gh api` exits non-zero on HTTP error responses. A 404 here means the package
# itself has never been published (so there's nothing to delete) -- not fatal.
# Other failure modes (auth, network) should fail the script.
$versionsJson = $null
$ghStdErr = New-TemporaryFile
try {
    $versionsJson = gh api $versionsApi 2>$ghStdErr
    $ghExit = $LASTEXITCODE
}
finally {
    $ghErrText = if (Test-Path $ghStdErr) { Get-Content -Raw $ghStdErr } else { '' }
    Remove-Item $ghStdErr -ErrorAction SilentlyContinue
}

if ($ghExit -ne 0) {
    if ($ghErrText -match '404|Not Found') {
        Write-Host "  no existing package on the feed; first push"
    } else {
        throw "Failed to query existing versions of $packageId via $versionsApi`n$ghErrText"
    }
}
else {
    $existing = ($versionsJson | ConvertFrom-Json) | Where-Object { $_.name -eq $version } | Select-Object -First 1
    if ($existing) {
        Write-Host "  deleting existing version (id $($existing.id))"
        gh api --method DELETE "$versionsApi/$($existing.id)" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to delete existing $packageId@$version (version id $($existing.id))."
        }
    } else {
        Write-Host "  package exists but version $version isn't on the feed yet"
    }
}

# --- Push the fresh copy ------------------------------------------------------
Write-Host "  pushing $NupkgPath"
dotnet nuget push $NupkgPath `
    --source $source `
    --api-key $env:GITHUB_TOKEN
if ($LASTEXITCODE -ne 0) {
    throw "dotnet nuget push failed for $NupkgPath"
}
