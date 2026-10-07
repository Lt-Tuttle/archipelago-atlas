<#
.SYNOPSIS
    Takes pictures of The Archipelago Atlas's main screens and, if given a baseline, compares them with an earlier run.

.DESCRIPTION
    Atlas starts in a window placed off-screen, against a new, empty scratch data folder (it never touches real data),
    opens a fixed set of screens as a first-time user sees them, saves a picture of each and exits. Run it before and
    after a change that could move pixels (a Godot upgrade, a theme, a layout change), passing the first run's pictures
    as -Baseline: each changed picture gets a ".diff.png" with the changed pixels in magenta.
    Pictures compare pixel for pixel only on the same PC (fonts, display scaling and graphics drivers all differ).

    The run also gets empty stand-ins for the user's folders and the temp folder, and fails if anything is written to
    them (see footprint.ps1).

    Exit code: 0 all pictures taken (and, with -Baseline, all the same), 1 some differ from the baseline (look at the
    .diff.png pictures), 2 refused or set up wrongly, 3 failed or timed out, 5 something was written outside Atlas's
    folder.

.PARAMETER Godot
    The Godot .NET console executable. Defaults to $env:ATLAS_GODOT, then the workspace's Godot_Engine folder.

.PARAMETER Baseline
    A folder of pictures from an earlier run to compare with.

.PARAMETER OutDir
    Where to save the pictures (default: a new folder under -ScratchRoot). It must be empty or not exist yet.

.PARAMETER NoBuild
    Skip "dotnet build".

.PARAMETER TimeoutMinutes
    Stop the run if it takes longer than this (default 5).

.PARAMETER ScratchRoot
    Where to make the scratch folder (default: the system temp folder).
#>
param(
    [string]$Godot = $env:ATLAS_GODOT,
    [string]$Baseline,
    [string]$Theme,
    [string]$OutDir,
    [switch]$NoBuild,
    [int]$TimeoutMinutes = 5,
    [string]$ScratchRoot = [System.IO.Path]::GetTempPath()
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'footprint.ps1')
$project = Split-Path -Parent $PSScriptRoot
# Relative paths mean relative to where you are in PowerShell (.NET's GetFullPath would use the process's folder).
function Get-FullPath([string]$path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path) }
$ScratchRoot = Get-FullPath $ScratchRoot

if (-not $Godot) {
    $Godot = Join-Path (Split-Path -Parent $project) 'Godot_Engine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
}
if (-not (Test-Path -LiteralPath $Godot)) {
    Write-Host "Godot not found at '$Godot'. Pass -Godot <path> or set ATLAS_GODOT." -ForegroundColor Red
    exit 2
}
if ($Baseline) {
    $Baseline = Get-FullPath $Baseline
    if (-not (Test-Path -LiteralPath $Baseline -PathType Container)) {
        Write-Host "The baseline folder '$Baseline' doesn't exist." -ForegroundColor Red
        exit 2
    }
}

if (-not $NoBuild) {
    Write-Host 'Building...'
    & dotnet build (Join-Path $project 'AP_Atlas.sln') -nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Build failed.' -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

$scratch = Join-Path $ScratchRoot ('atlas-visualcheck-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$data = Join-Path $scratch 'data'
if (-not $OutDir) { $OutDir = Join-Path $scratch 'pictures' }
$OutDir = Get-FullPath $OutDir
if ((Test-Path -LiteralPath $OutDir) -and (Get-ChildItem -LiteralPath $OutDir -Force | Select-Object -First 1)) {
    Write-Host "The picture folder '$OutDir' isn't empty." -ForegroundColor Red
    exit 2
}
New-Item -ItemType Directory -Path $data, $OutDir -Force | Out-Null
$outputFile = Join-Path $scratch 'godot_output.txt'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Godot
# A real window (pictures need drawing), placed off-screen so it stays out of the way.
$psi.Arguments = "--path `"$project`" --position -10000,-10000 res://Scenes/Main_Window.tscn"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
# Only the child process sees these, so nothing leaks into the caller's session.
$psi.EnvironmentVariables['ATLAS_DATA_DIR'] = $data
$psi.EnvironmentVariables['ATLAS_VISUALCHECK'] = $OutDir
if ($Baseline) { $psi.EnvironmentVariables['ATLAS_VISUALCHECK_BASELINE'] = $Baseline }
# The theme pictured: dark unless asked (never the PC's Windows mode, so two machines take the same pictures).
if ($Theme) { $psi.EnvironmentVariables['ATLAS_VISUALCHECK_THEME'] = $Theme } elseif ($psi.EnvironmentVariables.ContainsKey('ATLAS_VISUALCHECK_THEME')) { $psi.EnvironmentVariables.Remove('ATLAS_VISUALCHECK_THEME') }
foreach ($name in 'ATLAS_SELFTEST', 'ATLAS_SELFTEST_AP') {
    if ($psi.EnvironmentVariables.ContainsKey($name)) { $psi.EnvironmentVariables.Remove($name) }
}
if (-not $Baseline -and $psi.EnvironmentVariables.ContainsKey('ATLAS_VISUALCHECK_BASELINE')) { $psi.EnvironmentVariables.Remove('ATLAS_VISUALCHECK_BASELINE') }
$outside = Set-StandInUserFolders $psi $scratch

Write-Host "Taking pictures (into $OutDir)..."
$proc = [System.Diagnostics.Process]::Start($psi)
$stdout = $proc.StandardOutput.ReadToEndAsync()
$stderr = $proc.StandardError.ReadToEndAsync()
if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    try { $proc.Kill() } catch { }
    Write-Host "The visual check took longer than $TimeoutMinutes minutes and was stopped." -ForegroundColor Red
    $code = 3
}
else {
    $proc.WaitForExit()
    $code = $proc.ExitCode
}

$text = $stdout.Result + [Environment]::NewLine + $stderr.Result
Set-Content -LiteralPath $outputFile -Value $text -Encoding UTF8

$lines = $text -split "`r?`n"
$results = $lines | Where-Object { $_ -match '^VISUALCHECK' }
foreach ($line in $results) {
    if ($line -match '^VISUALCHECK (FAILED|REFUSED|MISSING)') { Write-Host $line -ForegroundColor Red }
    elseif ($line -match '^VISUALCHECK CHANGED') { Write-Host $line -ForegroundColor Yellow }
    elseif ($line -match '^VISUALCHECK (SAME|DONE: every)') { Write-Host $line -ForegroundColor Green }
    elseif ($line -notmatch '^VISUALCHECK PICTURE') { Write-Host $line }
}
if (-not ($results | Where-Object { $_ -match '^VISUALCHECK TAKEN' })) {
    Write-Host 'The visual check did not finish. Last lines of output:' -ForegroundColor Yellow
    $lines | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" }
    if ($code -eq 0) { $code = 3 }
}
if ((Test-Footprint $outside) -gt 0 -and $code -eq 0) { $code = 5 }

# The pictures are the point: keep them (and Godot's output); the scratch data folder isn't needed.
Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Pictures: $OutDir"
Write-Host "Godot's output: $outputFile"
exit $code
