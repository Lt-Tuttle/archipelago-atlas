<#
.SYNOPSIS
    The pack lab's gallery: pictures of every pack in a corpus as a connected slot sees it.

.DESCRIPTION
    Atlas starts in a window placed off-screen, against a new, empty scratch data folder, and for each pack zip in
    <Corpus>\packs takes pictures of the Map Tracker, Key Items (each layout) and the Pack Doctor's overview, with the
    game's names from <Corpus>\names (the files build_corpus_names.py writes). Nothing goes online. See
    MainTrackerWindow.PackGallery.cs. The pictures land in <OutDir>\<pack>\*.png with a gallery.tsv summary.

.PARAMETER Corpus
    The corpus folder (packs\ and names\ under it).

.PARAMETER OutDir
    Where the pictures go (default: a new folder under -ScratchRoot).

.PARAMETER Only
    Only the packs whose file names contain this.
#>
param(
    [Parameter(Mandatory = $true)][string]$Corpus,
    [string]$OutDir,
    [string]$Only,
    [string]$Godot = $env:ATLAS_GODOT,
    [switch]$NoBuild,
    [int]$TimeoutMinutes = 120,
    [string]$ScratchRoot = [System.IO.Path]::GetTempPath()
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'footprint.ps1')
$project = Split-Path -Parent $PSScriptRoot
function Get-FullPath([string]$path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path) }
$ScratchRoot = Get-FullPath $ScratchRoot
$Corpus = Get-FullPath $Corpus
if (-not $Godot) {
    $Godot = Join-Path (Split-Path -Parent $project) 'Godot_Engine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
}
if (-not (Test-Path -LiteralPath $Godot)) { Write-Host "Godot not found at '$Godot'." -ForegroundColor Red; exit 2 }
if (-not (Test-Path -LiteralPath (Join-Path $Corpus 'packs') -PathType Container)) { Write-Host "No packs folder in '$Corpus'." -ForegroundColor Red; exit 2 }

if (-not $NoBuild) {
    & dotnet build (Join-Path $project 'AP_Atlas.sln') -nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.' -ForegroundColor Red; exit $LASTEXITCODE }
}

$scratch = Join-Path $ScratchRoot ('atlas-gallery-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$data = Join-Path $scratch 'data'
if (-not $OutDir) { $OutDir = Join-Path $scratch 'pictures' }
$OutDir = Get-FullPath $OutDir
New-Item -ItemType Directory -Path $data, $OutDir -Force | Out-Null
$outputFile = Join-Path $OutDir 'godot_output.txt'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Godot
$psi.Arguments = "--path `"$project`" --position -10000,-10000 res://Scenes/Main_Window.tscn"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables['ATLAS_DATA_DIR'] = $data
$psi.EnvironmentVariables['ATLAS_PACK_GALLERY'] = $Corpus
$psi.EnvironmentVariables['ATLAS_PACK_GALLERY_OUT'] = $OutDir
if ($Only) { $psi.EnvironmentVariables['ATLAS_PACK_GALLERY_ONLY'] = $Only } elseif ($psi.EnvironmentVariables.ContainsKey('ATLAS_PACK_GALLERY_ONLY')) { $psi.EnvironmentVariables.Remove('ATLAS_PACK_GALLERY_ONLY') }
foreach ($name in 'ATLAS_SELFTEST', 'ATLAS_SELFTEST_AP', 'ATLAS_UITEST', 'ATLAS_VISUALCHECK') {
    if ($psi.EnvironmentVariables.ContainsKey($name)) { $psi.EnvironmentVariables.Remove($name) }
}
$outside = Set-StandInUserFolders $psi $scratch

Write-Host "Taking the gallery's pictures (into $OutDir)..."
$proc = [System.Diagnostics.Process]::Start($psi)
$stdout = $proc.StandardOutput.ReadToEndAsync()
$stderr = $proc.StandardError.ReadToEndAsync()
if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    try { $proc.Kill() } catch { }
    Write-Host "The gallery took longer than $TimeoutMinutes minutes and was stopped." -ForegroundColor Red
    $code = 3
}
else { $proc.WaitForExit(); $code = $proc.ExitCode }
$text = $stdout.Result + [Environment]::NewLine + $stderr.Result
Set-Content -LiteralPath $outputFile -Value $text -Encoding UTF8
$text -split "`r?`n" | Where-Object { $_ -match '^GALLERY' } | ForEach-Object { Write-Host $_ }
if ((Test-Footprint $outside) -gt 0 -and $code -eq 0) { $code = 5 }
Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Pictures: $OutDir"
exit $code
