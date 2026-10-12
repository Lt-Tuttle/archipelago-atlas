<#
.SYNOPSIS
    Builds the Atlas Engine's core that ships with Atlas, and checks it.

.DESCRIPTION
    Atlas sets its own engine up, headless (ATLAS_BUILD_ENGINE=1), in a build folder of its own (-Cache, kept between
    builds so nothing is downloaded twice): Python, pip, Archipelago's packages and the Universal Tracker, each checked
    against Atlas's pinned hashes, and Archipelago without its game worlds (its release zip kept for them), then a health
    check. The core is copied from there into -Out: python\, archipelago\ (the Universal Tracker as worlds\tracker.apworld,
    no compiled files, nothing the user would add) and core.json. Then the Atlas given runs once more on a fresh data folder
    (ATLAS_CHECK_SHIPPED_ENGINE=1) with that core beside it: the core passes its health check, a game's world unpacks from
    the kept zip and loads, and nothing is written into the core or outside Atlas's folders.

    Exit code: 0 built and checked; 6 the setup failed; 7 the core isn't complete; 8 the shipped core failed its check.

.PARAMETER Atlas
    The Atlas to run: the exported TheArchipelagoAtlas.exe (the release build), or Godot's console executable with
    -Project to run from source.

.PARAMETER Project
    With Godot as -Atlas: the project folder (AP_Atlas_Source).

.PARAMETER Out
    The engine folder to make (the release's TheArchipelagoAtlas\engine). It's replaced.

.PARAMETER Cache
    The build's own data folder (default: Builds\engine-data in the repository).

.PARAMETER ScratchRoot
    Where the check makes its scratch folders (default: the system temp folder).
#>
param(
    [Parameter(Mandatory = $true)][string]$Atlas,
    [string]$Project,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Cache,
    [string]$ScratchRoot = [System.IO.Path]::GetTempPath()
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'footprint.ps1')
function Get-FullPath([string]$path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path) }
function Stop-Engine([int]$Code, [string]$Message) { Write-Host $Message -ForegroundColor Red; exit $Code }

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Cache) { $Cache = Join-Path $repo 'Builds\engine-data' }
$Cache = Get-FullPath $Cache
$Out = Get-FullPath $Out
$Atlas = Get-FullPath $Atlas
$ScratchRoot = Get-FullPath $ScratchRoot

# Runs Atlas headless with these variables, its output to a log; returns the exit code.
function Invoke-Atlas([hashtable]$Variables, [string]$Log, [string]$Scratch, [int]$Minutes) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Atlas
    $info.Arguments = if ($Project) { "--headless --path `"$(Get-FullPath $Project)`"" } else { '--headless' }
    $info.WorkingDirectory = Split-Path -Parent $Atlas
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($name in $Variables.Keys) { $info.EnvironmentVariables[$name] = $Variables[$name] }
    $outside = Set-StandInUserFolders $info $Scratch
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $info
    $collected = New-Object System.Text.StringBuilder
    $onLine = { if ($null -ne $EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) } }
    $outEvent = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action $onLine -MessageData $collected
    $errEvent = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action $onLine -MessageData $collected
    [void]$proc.Start()
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()
    $exited = $proc.WaitForExit($Minutes * 60 * 1000)
    if (-not $exited) { try { $proc.Kill() } catch { } }
    Start-Sleep -Milliseconds 500
    try { $proc.CancelOutputRead(); $proc.CancelErrorRead() } catch { }
    Unregister-Event -SourceIdentifier $outEvent.Name
    Unregister-Event -SourceIdentifier $errEvent.Name
    [System.IO.File]::WriteAllText($Log, $collected.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    if ((Test-Footprint $outside) -gt 0) { return 98 }
    if (-not $exited) { return 99 }
    return $proc.ExitCode
}

# 1. Atlas's own setup, in the build's folder.
$scratch = Join-Path $ScratchRoot ('atlas-engine-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $scratch, $Cache -Force | Out-Null
Write-Host "Setting the engine up in $Cache (downloads only what isn't there yet)..."
$buildLog = Join-Path $Cache 'engine-build.log'
$code = Invoke-Atlas @{ ATLAS_DATA_DIR = $Cache; ATLAS_BUILD_ENGINE = '1' } $buildLog (Join-Path $scratch 'build') 30
$text = Get-Content -Raw -LiteralPath $buildLog
if ($code -ne 0 -or -not $text.Contains('ENGINEBUILD DONE')) {
    ($text -split "`r?`n" | Where-Object { $_ -match 'ENGINEBUILD|ENGINE (Setup stopped|Health check)' }) | Select-Object -Last 8 | ForEach-Object { Write-Host "  $_" }
    Stop-Engine 6 "The engine's setup failed (exit code $code). Log: $buildLog"
}
Write-Host ("  " + ($text -split "`r?`n" | Where-Object { $_ -match 'ENGINEBUILD DONE' } | Select-Object -First 1))

# 2. The core, from there: no compiled files, nothing a user adds.
$engine = Join-Path $Cache 'engine'
$tracker = Join-Path $engine 'ap\worlds\tracker.apworld'
$zip = Get-ChildItem -LiteralPath (Join-Path $engine 'downloads') -Filter 'Archipelago-*.zip' | Select-Object -First 1
foreach ($required in (Join-Path $engine 'python\python.exe'), (Join-Path $engine 'archipelago\Utils.py'), $tracker, (Join-Path $engine 'core.json')) {
    if (-not (Test-Path -LiteralPath $required)) { Stop-Engine 7 "The engine's setup left no $required." }
}
if (-not $zip) { Stop-Engine 7 "The engine's setup kept no Archipelago zip." }
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out -Force | Out-Null
$skip = @('__pycache__', 'custom_worlds', 'Players', 'logs')
function Copy-Core([string]$From, [string]$To) {
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $From -Force) {
        if ($skip -contains $item.Name -or $item.Name -eq 'host.yaml' -or $item.Name.EndsWith('.pyc')) { continue }
        if ($item.PSIsContainer) { Copy-Core $item.FullName (Join-Path $To $item.Name) }
        else { Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $To $item.Name) }
    }
}
Copy-Core (Join-Path $engine 'python') (Join-Path $Out 'python')
Copy-Core (Join-Path $engine 'archipelago') (Join-Path $Out 'archipelago')
Copy-Item -LiteralPath $tracker -Destination (Join-Path $Out 'archipelago\worlds\tracker.apworld')
Copy-Item -LiteralPath (Join-Path $engine 'core.json') -Destination (Join-Path $Out 'core.json')
$files = @(Get-ChildItem -LiteralPath $Out -Recurse -File)
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ("  The core: {0} files, {1:N1} MB in {2}" -f $files.Count, ($bytes / 1MB), $Out)

# 3. The shipped core, checked on a fresh data folder with the zip a user would have downloaded.
$checkData = Join-Path $scratch 'check-data'
New-Item -ItemType Directory -Path (Join-Path $checkData 'engine\downloads') -Force | Out-Null
Copy-Item -LiteralPath $zip.FullName -Destination (Join-Path $checkData "engine\downloads\$($zip.Name)")
$checkLog = Join-Path $Cache 'engine-check.log'
$variables = @{ ATLAS_DATA_DIR = $checkData; ATLAS_CHECK_SHIPPED_ENGINE = '1' }
# Run from source, the core isn't beside Godot's executable: name it.
if ($Project) { $variables['ATLAS_ENGINE_CORE'] = $Out }
$code = Invoke-Atlas $variables $checkLog (Join-Path $scratch 'check') 10
$text = Get-Content -Raw -LiteralPath $checkLog
if ($code -ne 0 -or -not $text.Contains('SHIPPEDENGINE DONE')) {
    ($text -split "`r?`n" | Where-Object { $_ -match 'SHIPPEDENGINE|Health check' }) | Select-Object -Last 8 | ForEach-Object { Write-Host "  $_" }
    Stop-Engine 8 "The shipped core failed its check (exit code $code). Log: $checkLog"
}
Write-Host ("  " + ($text -split "`r?`n" | Where-Object { $_ -match 'SHIPPEDENGINE DONE' } | Select-Object -First 1)) -ForegroundColor Green
Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
exit 0
