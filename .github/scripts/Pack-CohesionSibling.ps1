#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds this repository against a sibling checkout of assimalign/cohesion instead of the staging feed.

.DESCRIPTION
    Cohesion package releases are immutable and only published from commits reachable from cohesion's main
    (release.yml), so a platforms branch that tracks unreleased cohesion work cannot restore from the GitHub
    Packages staging feed. This script recreates the inner-loop layout on a CI runner: it clones cohesion to
    ../cohesion (the sibling path build/Targets/Build.References.Packages.targets probes), checks out the
    requested ref, builds cohesion's build tasks, then packs the transitive closure of every cohesion package
    this repository consumes into ../cohesion/_out/packages. Restore then resolves the release floor from
    that feed automatically; nothing here writes credentials or touches nuget.config.

    The roots are read from build/Targets/Build.References.Packages.targets (_CohesionPackage items whose id
    starts with Assimalign.Cohesion.), and the closure follows CohesionProjectReference and
    CohesionPrivateProjectReference items across cohesion's src projects, so the list never has to be kept by hand.

.PARAMETER Ref
    A cohesion branch, tag or commit SHA.

.PARAMETER SiblingDirectory
    Where the cohesion checkout lives. Defaults to ../cohesion next to this repository.

.EXAMPLE
    ./.github/scripts/Pack-CohesionSibling.ps1 -Ref feature/dx-design-buildout
#>
[CmdletBinding(DefaultParameterSetName = 'Ref')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Ref')] [string] $Ref,
    # The inner loop: pack the sibling checkout as it is, uncommitted changes included, at cohesion's
    # local version (the canonical prerelease plus .local, as Install-Local packs it). Nothing is cloned
    # or checked out, and the cached extracts of the packed versions are removed so restore reads the
    # new packages. Build against them with -p:CohesionSiblingPackageVersion=<the version printed>.
    [Parameter(Mandatory, ParameterSetName = 'Local')] [switch] $Local,
    [string] $SiblingDirectory,
    # Resolve the checkout and print the closure without building or packing (a dry run).
    [switch] $NoPack
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
if (-not $SiblingDirectory) { $SiblingDirectory = Join-Path (Split-Path -Parent $repository) 'cohesion' }
$feed = Join-Path $SiblingDirectory '_out' 'packages'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-Checked([string] $Description, [scriptblock] $Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

# 1. Sibling checkout at the requested ref (branch, tag or SHA; the repository is public), or as it
#    is for -Local.
if ($Local) {
    if (-not (Test-Path (Join-Path $SiblingDirectory '.git'))) { throw "No cohesion checkout at '$SiblingDirectory'." }
}
elseif (-not (Test-Path (Join-Path $SiblingDirectory '.git'))) {
    Invoke-Checked 'git clone' { git clone --no-checkout --filter=blob:none https://github.com/assimalign/cohesion.git $SiblingDirectory }
}
Push-Location $SiblingDirectory
try {
    if (-not $Local) {
        Invoke-Checked 'git fetch' { git fetch --depth 1 origin $Ref }
        Invoke-Checked 'git checkout' { git checkout --quiet --detach FETCH_HEAD }
    }
    $commit = (git rev-parse --short HEAD).Trim()
    Write-Host "cohesion sibling: $SiblingDirectory @ $commit ($(if ($Local) { 'working tree' } else { $Ref }))"

    # The local version vector, as Install-Local stamps it, so every generated dependency version is
    # the same .local identity.
    $packProperties = @()
    if ($Local) {
        Import-Module (Join-Path $SiblingDirectory 'scripts' 'modules' 'CohesionLocalPackaging.psm1') -Force
        $canonical = & (Join-Path $SiblingDirectory 'scripts' 'Get-CohesionVersion.ps1') -RepoRoot $SiblingDirectory
        $version = Get-CohesionLocalPackageVersion -Version $canonical
        $packProperties = @(
            "-p:CohesionMajorVersion=$($version.MajorVersion)"
            "-p:CohesionMinorVersion=$($version.MinorVersion)"
            "-p:CohesionPatchVersion=$($version.PatchVersion)"
            "-p:CohesionVersionPrefix=$($version.VersionPrefix)"
            "-p:CohesionVersionSuffix=$($version.VersionSuffix)"
            "-p:CohesionVersion=$($version.Version)"
            "-p:VersionPrefix=$($version.VersionPrefix)"
            "-p:VersionSuffix=$($version.VersionSuffix)"
            "-p:PackageVersion=$($version.Version)"
        )
        Write-Host "local version: $($version.Version)"
    }

    # 2. Build tasks first: per-project builds fail with MSB4062 in a fresh checkout otherwise.
    if (-not $NoPack) {
        Invoke-Checked 'build/Tasks' { dotnet build (Join-Path $SiblingDirectory 'build' 'Tasks' 'Assimalign.Cohesion.Build.Tasks.csproj') --configuration Release --nologo }
    }

    # 3. Roots: every cohesion package this repository consumes.
    $targets = Get-Content (Join-Path $repository 'build' 'Targets' 'Build.References.Packages.targets') -Raw
    $roots = [regex]::Matches($targets, '_CohesionPackage\s+Include\s*=\s*"(Assimalign\.Cohesion\.[A-Za-z0-9.]+)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    if ($roots.Count -eq 0) { throw 'No Assimalign.Cohesion.* _CohesionPackage items found in Build.References.Packages.targets.' }

    # 4. Transitive closure over cohesion's src projects.
    $projects = @{}
    foreach ($tree in 'libraries', 'resources', 'sdks', 'tooling', 'extensions') {
        $root = Join-Path $SiblingDirectory $tree
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem $root -Recurse -Filter *.csproj |
            Where-Object { $_.Directory.Name -eq 'src' -and $_.FullName -notmatch '[\\/](bin|obj|_old)[\\/]' } |
            ForEach-Object { $projects[$_.BaseName] = $_.FullName }
    }
    $closure = [System.Collections.Generic.List[string]]::new()
    $seen = @{}
    $queue = [System.Collections.Generic.Queue[string]]::new([string[]] $roots)
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        if ($seen.ContainsKey($id)) { continue }
        $seen[$id] = $true
        if (-not $projects.ContainsKey($id)) { throw "No src project found for package '$id' in the cohesion checkout." }
        $closure.Add($id)
        $content = Get-Content $projects[$id] -Raw
        foreach ($match in [regex]::Matches($content, 'Cohesion(?:Private)?ProjectReference\s+Include\s*=\s*"([^"]+)"')) {
            $dependency = $match.Groups[1].Value
            if (-not $seen.ContainsKey($dependency)) { $queue.Enqueue($dependency) }
        }
    }
    Write-Host ("closure: {0} packages from {1} roots ({2})" -f $closure.Count, $roots.Count, ($roots -join ', '))
    if ($NoPack) {
        $closure | ForEach-Object { Write-Host "  $_" }
        return
    }

    # 5. Pack the closure into the sibling feed. A local pack replaces packages whose id and version
    #    restore may already have extracted, so their cached extracts go first.
    New-Item -ItemType Directory -Force $feed | Out-Null
    if ($Local) {
        $globalPackages = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
        foreach ($id in $closure) {
            $cached = Join-Path $globalPackages.Trim() $id.ToLowerInvariant() $version.Version
            if (Test-Path $cached) { Remove-Item -LiteralPath $cached -Recurse -Force }
        }
    }
    foreach ($id in $closure) {
        Write-Host ("[{0:mm\:ss}] pack {1}" -f $sw.Elapsed, $id)
        Invoke-Checked "dotnet pack $id" { dotnet pack $projects[$id] --configuration Release --nologo "-p:PackageOutputPath=$feed" @packProperties }
    }
    $packed = @(Get-ChildItem $feed -Filter 'Assimalign.Cohesion.*.nupkg')
    Write-Host ("packed {0} nupkgs into {1} in {2:mm\:ss}" -f $packed.Count, $feed, $sw.Elapsed)
    if ($env:GITHUB_STEP_SUMMARY) {
        "Cohesion sibling feed: $($packed.Count) packages from ``$Ref`` ($commit) in $($sw.Elapsed.ToString('mm\:ss'))." >> $env:GITHUB_STEP_SUMMARY
    }
}
finally {
    Pop-Location
}
