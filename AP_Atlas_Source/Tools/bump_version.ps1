<#
.SYNOPSIS
    Sets The Archipelago Atlas's version and starts its changelog section.

.DESCRIPTION
    Writes the new version to <Version> in Directory.Build.props (the one place it is set, shared by every project). In CHANGELOG.md it turns the
    "## [Unreleased]" section into "## [<version>] - <today>" and opens a fresh, empty "## [Unreleased]" above it.
    It then prints the git commands that commit and tag the release. Pushing the tag starts the release build.

.EXAMPLE
    ./bump_version.ps1 0.1.0-beta.2
#>
param(
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
# SemVer 2.0: MAJOR.MINOR.PATCH with an optional pre-release such as -beta.1 (no build metadata; the build adds the commit).
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    Write-Host "'$Version' isn't a version like 1.2.3 or 0.2.0-beta.1." -ForegroundColor Red
    exit 2
}

$project = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $project
$props = Join-Path $repo 'Directory.Build.props'
$changelog = Join-Path $repo 'CHANGELOG.md'

$text = [System.IO.File]::ReadAllText($props)
$pattern = '<Version>[^<]*</Version>'
if (-not [regex]::IsMatch($text, $pattern)) { Write-Host 'No <Version> found in Directory.Build.props.' -ForegroundColor Red; exit 2 }
$old = [regex]::Match($text, '<Version>([^<]*)</Version>').Groups[1].Value
if ($old -eq $Version) { Write-Host "The version is already $Version." -ForegroundColor Yellow; exit 0 }
$text = [regex]::Replace($text, $pattern, "<Version>$Version</Version>")
[System.IO.File]::WriteAllText($props, $text, (New-Object System.Text.UTF8Encoding($false)))

$log = [System.IO.File]::ReadAllText($changelog)
if ($log -notmatch '(?m)^## \[Unreleased\]') { Write-Host 'CHANGELOG.md has no "## [Unreleased]" section.' -ForegroundColor Red; exit 2 }
$today = (Get-Date).ToString('yyyy-MM-dd')
$log = [regex]::Replace($log, '(?m)^## \[Unreleased\]', "## [Unreleased]`r`n`r`n## [$Version] - $today", 1)
[System.IO.File]::WriteAllText($changelog, $log, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Version $old -> $Version (Directory.Build.props, CHANGELOG.md)." -ForegroundColor Green
Write-Host 'Review the changelog section, then:'
Write-Host "  git commit -am `"Release $Version`""
Write-Host "  git tag -a v$Version -m `"The Archipelago Atlas $Version`""
Write-Host "  git push origin main v$Version"
exit 0
