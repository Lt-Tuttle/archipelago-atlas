<#
.SYNOPSIS
    Builds The Archipelago Atlas and runs its self-test headless against a new, empty scratch data folder.

.DESCRIPTION
    The self-test never touches real data: it refuses any data folder that isn't empty, and this script always
    makes a fresh one under the system temp folder. Godot's output goes to a file next to (never inside) that
    data folder. The run also gets empty stand-ins for the user's folders and the temp folder, and fails if anything
    is written to them (see footprint.ps1). The script exits with the self-test's exit code (0 = all passed), or 5 if
    the self-test passed but something was written outside Atlas's folder.

.PARAMETER Godot
    The Godot .NET console executable. Defaults to $env:ATLAS_GODOT, then the workspace's Godot_Engine folder.

.PARAMETER ArchipelagoDir
    Optional Archipelago install for the engine end-to-end check (sets ATLAS_SELFTEST_AP).

.PARAMETER NoBuild
    Skip "dotnet build" (CI builds in an earlier step).

.PARAMETER Keep
    Keep the scratch folder afterwards, to read selftest_results.txt and the full Godot output.

.PARAMETER TimeoutMinutes
    Stop the run if it takes longer than this (default 20).

.PARAMETER ScratchRoot
    Where to make the scratch folder (default: the system temp folder). CI points this at its own temp folder so the
    output can be kept as an artifact.
#>
param(
    [string]$Godot = $env:ATLAS_GODOT,
    [string]$ArchipelagoDir,
    [switch]$NoBuild,
    [switch]$Keep,
    [int]$TimeoutMinutes = 20,
    [string]$ScratchRoot = [System.IO.Path]::GetTempPath()
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'footprint.ps1')
$project = Split-Path -Parent $PSScriptRoot
# Relative paths mean relative to where you are in PowerShell (the Godot process would resolve them from its own folder).
function Get-FullPath([string]$path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path) }
$ScratchRoot = Get-FullPath $ScratchRoot
if ($ArchipelagoDir) { $ArchipelagoDir = Get-FullPath $ArchipelagoDir }

if (-not $Godot) {
    $Godot = Join-Path (Split-Path -Parent $project) 'Godot_Engine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
}
if (-not (Test-Path -LiteralPath $Godot)) {
    Write-Host "Godot not found at '$Godot'. Pass -Godot <path> or set ATLAS_GODOT." -ForegroundColor Red
    exit 2
}

if (-not $NoBuild) {
    Write-Host 'Building...'
    & dotnet build (Join-Path $project 'AP_Atlas.sln') -nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Build failed.' -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

$scratch = Join-Path $ScratchRoot ('atlas-selftest-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$data = Join-Path $scratch 'data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$outputFile = Join-Path $scratch 'godot_output.txt'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Godot
$psi.Arguments = "--headless --path `"$project`" res://Scenes/Main_Window.tscn"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
# Only the child process sees these, so nothing leaks into the caller's session.
$psi.EnvironmentVariables['ATLAS_SELFTEST'] = '1'
$psi.EnvironmentVariables['ATLAS_DATA_DIR'] = $data
if ($ArchipelagoDir) { $psi.EnvironmentVariables['ATLAS_SELFTEST_AP'] = $ArchipelagoDir }
elseif ($psi.EnvironmentVariables.ContainsKey('ATLAS_SELFTEST_AP')) { $psi.EnvironmentVariables.Remove('ATLAS_SELFTEST_AP') }
$outside = Set-StandInUserFolders $psi $scratch

Write-Host "Running the self-test (data folder: $data)..."
$proc = [System.Diagnostics.Process]::Start($psi)
$stdout = $proc.StandardOutput.ReadToEndAsync()
$stderr = $proc.StandardError.ReadToEndAsync()
if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    try { $proc.Kill() } catch { }
    Write-Host "The self-test took longer than $TimeoutMinutes minutes and was stopped." -ForegroundColor Red
    $code = 3
}
else {
    $proc.WaitForExit()
    $code = $proc.ExitCode
}

$text = $stdout.Result + [Environment]::NewLine + $stderr.Result
Set-Content -LiteralPath $outputFile -Value $text -Encoding UTF8

$lines = $text -split "`r?`n"
$results = $lines | Where-Object { $_ -match '^SELFTEST' }
foreach ($line in $results) {
    if ($line -match '^SELFTEST (FAIL|REFUSED|CRASHED)') { Write-Host $line -ForegroundColor Red }
    elseif ($line -match '^SELFTEST PASS') { Write-Host $line -ForegroundColor Green }
    else { Write-Host $line }
}
if (-not ($results | Where-Object { $_ -match '^SELFTEST DONE' })) {
    Write-Host 'The self-test did not finish. Last lines of output:' -ForegroundColor Yellow
    $lines | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" }
    if ($code -eq 0) { $code = 4 }
}
if ((Test-Footprint $outside) -gt 0 -and $code -eq 0) { $code = 5 }

if ($Keep -or $code -ne 0) {
    Write-Host "Full output: $outputFile"
}
else {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
exit $code
