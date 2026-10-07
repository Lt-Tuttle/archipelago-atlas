<#
.SYNOPSIS
    Builds The Archipelago Atlas's release: the exported Windows build, tested, zipped, with its checksum.

.DESCRIPTION
    Exports the project with Godot, headless, through the "Windows Desktop" preset in export_presets.cfg (its release
    template pinned by hash; Tools/get_export_templates.ps1 puts it in place), checks what came out (the files, the exe's
    version and icon, not one export warning), runs the self-test and the UI test on the exported build itself
    (Tools/run_selftest.ps1 -Executable, with the footprint check), then packs TheArchipelagoAtlas-<version>-win-x64.zip
    (one folder, TheArchipelagoAtlas, holding the program, its data folder, README, LICENSE, THIRD_PARTY_NOTICES,
    GODOT_COPYRIGHT, CREDITS and CHANGELOG) and writes SHA256SUMS.txt next to it.

    Nothing is stamped here. The version comes from Directory.Build.props (the one place it is set; the build adds the
    commit), and the exe's file and product version from export_presets.cfg, which Tools/bump_version.ps1 keeps in step
    and the guard rails check. This script only refuses to build when they disagree, or when -Version (the tag, in CI)
    isn't that version. Godot is run from a clean staging folder (Builds/staging), never over an earlier build.

    Exit code: 0 built and verified; 2 a precondition failed (versions, Godot, templates); 3 the export failed or warned;
    4 the exported build isn't what a release needs; 5 the exported build failed its tests.

.PARAMETER Version
    The version expected (CI passes the tag's). Must equal <Version> in Directory.Build.props.

.PARAMETER OutDir
    Where the zip and SHA256SUMS.txt go (default: Builds/dist in the repository). A relative path is relative to the
    current folder.

.PARAMETER Godot
    The Godot .NET console executable. Defaults to $env:ATLAS_GODOT, then the workspace's Godot_Engine folder.

.PARAMETER SkipTests
    Don't run the self-test and UI test on the exported build (for work on the packaging itself; a release never skips
    them, and CI doesn't pass it).

.PARAMETER ScratchRoot
    Where the tests make their scratch folders (default: the system temp folder).

.EXAMPLE
    ./build_release.ps1
    ./build_release.ps1 -Version 0.1.0-beta.1 -OutDir dist
#>
param(
    [string]$Version,
    [string]$OutDir,
    [string]$Godot = $env:ATLAS_GODOT,
    [switch]$SkipTests,
    [string]$ScratchRoot = [System.IO.Path]::GetTempPath()
)

$ErrorActionPreference = 'Stop'
function Get-FullPath([string]$path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path) }
function Stop-Build([int]$Code, [string]$Message) { Write-Host $Message -ForegroundColor Red; exit $Code }

$project = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $project
if (-not $OutDir) { $OutDir = Join-Path $repo 'Builds\dist' }
$OutDir = Get-FullPath $OutDir
$ScratchRoot = Get-FullPath $ScratchRoot

# 1. The version: one source, and everything that carries it agrees.
$props = [System.IO.File]::ReadAllText((Join-Path $repo 'Directory.Build.props'))
$projectVersion = [regex]::Match($props, '<Version>([^<]*)</Version>').Groups[1].Value
if (-not $projectVersion) { Stop-Build 2 'No <Version> in Directory.Build.props.' }
if ($Version -and $Version -ne $projectVersion) { Stop-Build 2 "Asked to build $Version, but Directory.Build.props says $projectVersion." }
$Version = $projectVersion
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') { Stop-Build 2 "'$Version' isn't a version like 1.2.3 or 0.2.0-beta.1." }
$fileVersion = ($Version -split '-')[0] + '.0'
$presetPath = Join-Path $project 'export_presets.cfg'
if (-not (Test-Path -LiteralPath $presetPath)) { Stop-Build 2 'export_presets.cfg is missing.' }
$preset = [System.IO.File]::ReadAllText($presetPath)
foreach ($key in 'application/file_version', 'application/product_version') {
    $found = [regex]::Match($preset, [regex]::Escape($key) + '="([^"]*)"')
    if (-not $found.Success -or $found.Groups[1].Value -ne $fileVersion) {
        Stop-Build 2 "export_presets.cfg's $key must be $fileVersion for version $Version (Tools/bump_version.ps1 sets it)."
    }
}
# (No stderr redirection on native commands: Windows PowerShell 5.1 would turn a stderr line into a terminating error here.)
$dirty = @()
try { $dirty = @(& git -C $repo status --porcelain) } catch { $dirty = @() }
if ($dirty.Count -gt 0) { Write-Host "Note: building from a working tree with $($dirty.Count) uncommitted change(s); a release is built from a clean, tagged commit." -ForegroundColor Yellow }

# 2. Godot, and the pinned release template the preset names.
if (-not $Godot) { $Godot = Join-Path $repo 'Godot_Engine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe' }
if (-not (Test-Path -LiteralPath $Godot)) { Stop-Build 2 "Godot not found at '$Godot'. Pass -Godot <path> or set ATLAS_GODOT." }
$godotVersion = ''
try { $godotVersion = "$(& $Godot --version | Select-Object -Last 1)".Trim() } catch { $godotVersion = '' }
if ($godotVersion -notlike '4.7.2.stable.mono*') { Stop-Build 2 "Godot reports '$godotVersion'; the export needs 4.7.2.stable.mono, which the templates match." }
& (Join-Path $PSScriptRoot 'get_export_templates.ps1')
if ($LASTEXITCODE -ne 0) { Stop-Build 2 "The export templates aren't in place (get_export_templates.ps1 exit code $LASTEXITCODE)." }
$templateRelative = [regex]::Match($preset, 'custom_template/release="([^"]*)"').Groups[1].Value
if (-not $templateRelative) { Stop-Build 2 'export_presets.cfg names no custom_template/release.' }
# Godot resolves the preset's relative path from the project folder (--path makes it the working folder).
$template = Get-FullPath (Join-Path $project $templateRelative)
if (-not (Test-Path -LiteralPath $template)) { Stop-Build 2 "The preset's release template is missing at $template." }

# 3. A clean export.
$stagingRoot = Join-Path $repo 'Builds\staging'
$staging = Join-Path $stagingRoot 'TheArchipelagoAtlas'
if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$exe = Join-Path $staging 'TheArchipelagoAtlas.exe'
$exportLog = Join-Path $stagingRoot 'export.log'
Write-Host "Exporting The Archipelago Atlas $Version with Godot $godotVersion..."
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Godot
$psi.Arguments = "--headless --path `"$project`" --export-release `"Windows Desktop`" `"$exe`""
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
# The export runs the .NET build, which would leave its compiler server and build nodes running for minutes with a handle
# to Godot's output; Godot's console wrapper (and anything waiting for the end of that output) would then wait for them.
# The build's helpers end with the build instead.
$psi.EnvironmentVariables['UseSharedCompilation'] = 'false'
$psi.EnvironmentVariables['MSBUILDDISABLENODEREUSE'] = '1'
$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
# Output is collected line by line as it arrives (not read to its end: a helper process holding the pipe open after Godot
# exits mustn't hold up the build).
$collected = New-Object System.Text.StringBuilder
$onLine = { if ($null -ne $EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) } }
$outEvent = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action $onLine -MessageData $collected
$errEvent = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action $onLine -MessageData $collected
[void]$proc.Start()
$proc.BeginOutputReadLine()
$proc.BeginErrorReadLine()
$exited = $proc.WaitForExit(20 * 60 * 1000)
if (-not $exited) { try { $proc.Kill() } catch { } }
Start-Sleep -Milliseconds 500 # the last lines arrive just after the exit
try { $proc.CancelOutputRead(); $proc.CancelErrorRead() } catch { }
Unregister-Event -SourceIdentifier $outEvent.Name
Unregister-Event -SourceIdentifier $errEvent.Name
$output = $collected.ToString()
[System.IO.File]::WriteAllText($exportLog, $output, (New-Object System.Text.UTF8Encoding($false)))
if (-not $exited) { Stop-Build 3 "The export took longer than 20 minutes and was stopped. Full output: $exportLog" }
# Godot's progress lines carry colour codes; read them plain. Any error or warning from Godot or the .NET build fails
# the release: a warning once meant an icon size left as Godot's own.
$plain = [regex]::Replace($output, [string][char]27 + '\[[0-9;]*m', '')
$problems = @($plain -split "`r?`n" | Where-Object { $_ -match '^\s*(ERROR|WARNING):|\berror (CS|MSB|NETSDK)\d|\bwarning (CS|MSB|NETSDK)\d' })
if ($proc.ExitCode -ne 0 -or $problems.Count -gt 0) {
    Write-Host "The export failed or warned (Godot exit code $($proc.ExitCode)). Full output: $exportLog" -ForegroundColor Red
    $problems | Select-Object -First 10 | ForEach-Object { Write-Host "  $($_.Trim())" }
    exit 3
}

# 4. What came out: the files a release needs, and an exe that says it's Atlas.
$pck = Join-Path $staging 'TheArchipelagoAtlas.pck'
$data = Join-Path $staging 'data_AP_Atlas_windows_x86_64'
foreach ($required in $exe, $pck, (Join-Path $data 'AP_Atlas.dll'), (Join-Path $data 'AP_Atlas.Core.dll'), (Join-Path $data 'coreclr.dll')) {
    if (-not (Test-Path -LiteralPath $required)) { Stop-Build 4 "The export is missing $required." }
}
if (Test-Path -LiteralPath (Join-Path $staging 'TheArchipelagoAtlas.console.exe')) { Stop-Build 4 'The release export has a console wrapper; the preset exports one for debug builds only.' }
$info = (Get-Item -LiteralPath $exe).VersionInfo
$expected = [ordered]@{ FileVersion = $fileVersion; ProductVersion = $fileVersion; ProductName = 'The Archipelago Atlas'; CompanyName = 'Lt-Tuttle'; FileDescription = 'The Archipelago Atlas' }
foreach ($name in $expected.Keys) {
    $value = "$($info.$name)".Trim()
    if ($value -ne $expected[$name]) { Stop-Build 4 "The exe's $name is '$value', not '$($expected[$name])': the export didn't write Atlas's properties." }
}
if ("$($info.LegalCopyright)" -notlike '*Lt-Tuttle*') { Stop-Build 4 "The exe's copyright reads '$($info.LegalCopyright)'." }
Add-Type -AssemblyName System.Drawing
function Get-IconHash([string]$Path) {
    $bitmap = [System.Drawing.Icon]::ExtractAssociatedIcon($Path).ToBitmap()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($stream.ToArray()))
}
if ((Get-IconHash $exe) -eq (Get-IconHash $template)) { Stop-Build 4 "The exe still shows Godot's icon: the export didn't write Atlas's." }
# The assemblies carry the version with the commit they were built from (the .NET build writes it into their properties).
$coreVersion = "$((Get-Item -LiteralPath (Join-Path $data 'AP_Atlas.Core.dll')).VersionInfo.ProductVersion)".Trim()
if ($coreVersion -ne $Version -and -not $coreVersion.StartsWith("$Version+")) { Stop-Build 4 "AP_Atlas.Core.dll says it's version '$coreVersion', not $Version." }
$commit = if ($coreVersion -match '\+([0-9a-f]{7,})') { $Matches[1].Substring(0, 7) } else { 'no commit recorded' }

# 5. The tests, on the build that ships.
if ($SkipTests) {
    Write-Host 'Skipping the tests on the exported build (-SkipTests); a release never does.' -ForegroundColor Yellow
}
else {
    Write-Host 'Testing the exported build: the self-test, the UI test and the footprint check...'
    & (Join-Path $PSScriptRoot 'run_selftest.ps1') -Executable $exe -ScratchRoot $ScratchRoot
    if ($LASTEXITCODE -ne 0) { Stop-Build 5 "The exported build failed its tests (run_selftest.ps1 exit code $LASTEXITCODE)." }
}

# 6. The documents, then the zip and its checksum.
foreach ($doc in 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'GODOT_COPYRIGHT.txt', 'CREDITS.md', 'CHANGELOG.md') {
    Copy-Item -LiteralPath (Join-Path $repo $doc) -Destination (Join-Path $staging $doc)
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$zipName = "TheArchipelagoAtlas-$Version-win-x64.zip"
$zipPath = Join-Path $OutDir $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# The zip unpacks to one folder, TheArchipelagoAtlas, so nothing lands loose in Downloads.
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
# sha256sum's own format (two spaces, LF line ends, no byte order mark), so "sha256sum -c SHA256SUMS.txt" checks it.
[System.IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), "$zipHash  $zipName`n", (New-Object System.Text.UTF8Encoding($false)))

$size = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
Write-Host "Built The Archipelago Atlas $Version (commit $commit): $zipPath ($size MB), SHA-256 $zipHash." -ForegroundColor Green
exit 0
