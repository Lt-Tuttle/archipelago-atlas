<#
.SYNOPSIS
    Builds The Archipelago Atlas and runs its self-test, then its UI test, headless against new, empty scratch data
    folders.

.DESCRIPTION
    The self-test checks Atlas's protections one by one. The UI test then builds Atlas's window and drives it the way
    a user would (connecting a slot, a dropped connection, disconnecting) against a fake Archipelago server on this
    computer. Neither touches real data: each refuses a data folder that isn't empty, and this script always makes
    fresh ones under the system temp folder. Godot's output goes to files next to (never inside) those folders. Both
    runs also get empty stand-ins for the user's folders and the temp folder, and fail if anything is written to them
    (see footprint.ps1).

    Exit code: 0 everything passed; the self-test's own code if it failed (1 a check failed, 3 timed out, 4 didn't
    finish); 6 the UI test failed; 5 both passed but something was written outside Atlas's folder.

.PARAMETER Godot
    The Godot .NET console executable. Defaults to $env:ATLAS_GODOT, then the workspace's Godot_Engine folder.

.PARAMETER ArchipelagoDir
    Optional Archipelago install for the engine end-to-end check (sets ATLAS_SELFTEST_AP).

.PARAMETER NoBuild
    Skip "dotnet build" (CI builds in an earlier step).

.PARAMETER Keep
    Keep the scratch folder afterwards, to read selftest_results.txt and the full Godot output of both runs.

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
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$outside = $null

# Runs Atlas headless in one test mode against its own new, empty data folder, shows its result lines and returns
# its exit code.
function Invoke-AtlasTest([string]$Slug, [string]$Title, [string]$Mode, [string]$Prefix) {
    $data = Join-Path $scratch "$Slug-data"
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Godot
    $psi.Arguments = "--headless --path `"$project`" res://Scenes/Main_Window.tscn"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    # Only the child process sees these, so nothing leaks into the caller's session.
    foreach ($name in 'ATLAS_SELFTEST', 'ATLAS_UITEST', 'ATLAS_VISUALCHECK', 'ATLAS_SELFTEST_AP') {
        if ($psi.EnvironmentVariables.ContainsKey($name)) { $psi.EnvironmentVariables.Remove($name) }
    }
    $psi.EnvironmentVariables[$Mode] = '1'
    $psi.EnvironmentVariables['ATLAS_DATA_DIR'] = $data
    if ($ArchipelagoDir -and $Mode -eq 'ATLAS_SELFTEST') { $psi.EnvironmentVariables['ATLAS_SELFTEST_AP'] = $ArchipelagoDir }
    # Both runs share the stand-ins, so one footprint check covers them.
    $script:outside = Set-StandInUserFolders $psi $scratch

    Write-Host "Running the $Title (data folder: $data)..."
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEndAsync()
    $stderr = $proc.StandardError.ReadToEndAsync()
    if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        try { $proc.Kill() } catch { }
        Write-Host "The $Title took longer than $TimeoutMinutes minutes and was stopped." -ForegroundColor Red
        $code = 3
    }
    else {
        $proc.WaitForExit()
        $code = $proc.ExitCode
    }

    $text = $stdout.Result + [Environment]::NewLine + $stderr.Result
    Set-Content -LiteralPath (Join-Path $scratch "$Slug-output.txt") -Value $text -Encoding UTF8
    $lines = $text -split "`r?`n"
    $results = $lines | Where-Object { $_ -match "^$Prefix" }
    foreach ($line in $results) {
        if ($line -match "^$Prefix (FAIL|REFUSED|CRASHED)") { Write-Host $line -ForegroundColor Red }
        elseif ($line -match "^$Prefix PASS") { Write-Host $line -ForegroundColor Green }
        else { Write-Host $line }
    }
    if (-not ($results | Where-Object { $_ -match "^$Prefix DONE" })) {
        Write-Host "The $Title did not finish. Last lines of output:" -ForegroundColor Yellow
        $lines | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" }
        if ($code -eq 0) { $code = 4 }
    }
    return $code
}

$code = Invoke-AtlasTest 'selftest' 'self-test' 'ATLAS_SELFTEST' 'SELFTEST'
$uiCode = Invoke-AtlasTest 'uitest' 'UI test' 'ATLAS_UITEST' 'UITEST'
if ($code -eq 0 -and $uiCode -ne 0) { $code = 6 }
if ((Test-Footprint $outside) -gt 0 -and $code -eq 0) { $code = 5 }

if ($Keep -or $code -ne 0) {
    Write-Host "Full output: $scratch"
}
else {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
exit $code
