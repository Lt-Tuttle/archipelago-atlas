<#
.SYNOPSIS
    Gets Godot 4.7.2's .NET export templates for Windows, hash-checked, where the export preset expects them.

.DESCRIPTION
    A release build (Tools/build_release.ps1) exports Atlas with Godot's export templates, which must match the editor
    exactly (4.7.2.stable.mono). This script downloads the templates archive from Godot's official builds (about 1.2 GB;
    it's kept in Godot_Engine/, which git ignores, so it isn't downloaded twice), checks it against the SHA-512 Godot
    publishes next to it, and unpacks only the Windows x86_64 templates into Godot_Engine/export_templates/4.7.2.stable.mono/,
    the folder export_presets.cfg names (custom_template/release and custom_template/debug). Each unpacked template is
    checked against its own pinned SHA-256 as well, so a damaged or swapped file is refused. Nothing is written anywhere
    else: not Godot's own folder in AppData, nothing outside the repository folder.

    When the templates are already there and match, it does nothing.

.PARAMETER Force
    Download the archive again even if it's present.

.PARAMETER DeleteArchive
    Remove the archive after unpacking (saves 1.2 GB; the next run downloads it again).

.EXAMPLE
    ./get_export_templates.ps1
#>
param(
    [switch]$Force,
    [switch]$DeleteArchive
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Godot 4.7.2 stable, .NET build. The archive hash is from Godot's published SHA512-SUMS.txt for the release; the template
# hashes were taken from that archive. Change all of them together when Godot is updated (with AP_Atlas.csproj's Sdk, the
# editor in Godot_Engine, CI's GODOT_VERSION and GODOT_SHA512, and the preset's custom_template paths).
$godotVersion = '4.7.2-stable'
$templatesFolder = '4.7.2.stable.mono'
$archiveName = "Godot_v${godotVersion}_mono_export_templates.tpz"
$archiveUrl = "https://github.com/godotengine/godot-builds/releases/download/$godotVersion/$archiveName"
$archiveSha512 = 'bb5c41d72370ed743660361f6228006f808ab04ca33abdc545d740b044f3fe057f32ae8cb7873a1bc86ddcd82ae683b9f6dfdfe4179852f2c0f1acde2ff6bd5a'
$templates = [ordered]@{
    'windows_release_x86_64.exe'         = '16e0ec3cfd398b938f514de95ff2474b532b5061ada78f92226dba3f7584d55d'
    'windows_release_x86_64_console.exe' = '7895a7873947deaff6c8c859c1f77be051023413035048e2db74ceba07a65017'
    'windows_debug_x86_64.exe'           = '4526aac3ac47c65de66f99f715f56b35a2e2e4446fcb23e6d280a830cd97fdea'
    'windows_debug_x86_64_console.exe'   = 'f78703ba3a05dd51a3464f8c90d4c72f54f95d5a8669993e7a5c3ce522b30c52'
}

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$engineDir = Join-Path $repo 'Godot_Engine'
$archive = Join-Path $engineDir $archiveName
$target = Join-Path $engineDir "export_templates\$templatesFolder"

function Test-Template([string]$Name) {
    $path = Join-Path $target $Name
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $templates[$Name]
}

$missing = @($templates.Keys | Where-Object { -not (Test-Template $_) })
if ($missing.Count -eq 0 -and -not $Force) {
    Write-Host "The Windows export templates for Godot $templatesFolder are in place and match ($target)." -ForegroundColor Green
    exit 0
}

New-Item -ItemType Directory -Path $engineDir -Force | Out-Null
if ($Force -or -not (Test-Path -LiteralPath $archive)) {
    Write-Host "Downloading $archiveName (about 1.2 GB) from Godot's official builds..."
    $curl = Get-Command curl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($curl) {
        & $curl.Source --location --fail --silent --show-error --retry 3 --output $archive $archiveUrl
        if ($LASTEXITCODE -ne 0) { throw "The download failed (curl exit code $LASTEXITCODE)." }
    }
    else {
        Invoke-WebRequest -Uri $archiveUrl -OutFile $archive
    }
}

Write-Host 'Checking the archive against the hash Godot publishes...'
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant()
if ($hash -ne $archiveSha512) {
    Remove-Item -LiteralPath $archive -Force
    throw "The templates archive doesn't match its published hash (got $hash). It was deleted; run this again."
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($name in @($templates.Keys) + 'version.txt') {
        $entry = $zip.GetEntry("templates/$name")
        if (-not $entry) { throw "The archive has no templates/$name." }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $target $name), $true)
    }
}
finally {
    $zip.Dispose()
}

foreach ($name in $templates.Keys) {
    if (Test-Template $name) { continue }
    Remove-Item -LiteralPath (Join-Path $target $name) -Force
    throw "$name doesn't match its pinned hash after unpacking; it was removed."
}
$versionLine = (Get-Content -LiteralPath (Join-Path $target 'version.txt') -TotalCount 1).Trim()
if ($versionLine -ne $templatesFolder) { throw "The archive's version.txt says '$versionLine', not $templatesFolder." }

if ($DeleteArchive) { Remove-Item -LiteralPath $archive -Force }
Write-Host "Godot $templatesFolder Windows export templates ready in $target." -ForegroundColor Green
exit 0
