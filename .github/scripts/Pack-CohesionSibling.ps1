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
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Ref,
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

# 1. Sibling checkout at the requested ref (branch, tag or SHA; the repository is public).
if (-not (Test-Path (Join-Path $SiblingDirectory '.git'))) {
    Invoke-Checked 'git clone' { git clone --no-checkout --filter=blob:none https://github.com/assimalign/cohesion.git $SiblingDirectory }
}
Push-Location $SiblingDirectory
try {
    Invoke-Checked 'git fetch' { git fetch --depth 1 origin $Ref }
    Invoke-Checked 'git checkout' { git checkout --quiet --detach FETCH_HEAD }
    $commit = (git rev-parse --short HEAD).Trim()
    Write-Host "cohesion sibling: $SiblingDirectory @ $commit ($Ref)"

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

    # 5. Pack the closure into the sibling feed.
    New-Item -ItemType Directory -Force $feed | Out-Null
    foreach ($id in $closure) {
        Write-Host ("[{0:mm\:ss}] pack {1}" -f $sw.Elapsed, $id)
        Invoke-Checked "dotnet pack $id" { dotnet pack $projects[$id] --configuration Release --nologo "-p:PackageOutputPath=$feed" }
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
