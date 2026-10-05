<#
.SYNOPSIS
    The footprint check shared by run_selftest.ps1 and run_visualcheck.ps1 (dot-source it): Atlas must write nothing
    outside its own folder.

.DESCRIPTION
    Godot, .NET and Python find the user's folders and the temp folder through APPDATA, LOCALAPPDATA, TEMP and TMP. A
    test run gets empty stand-ins for them, and afterwards any file in them is something Atlas would have written
    outside its folder on a user's PC. Empty folders are allowed: Godot always makes its user folder
    (%APPDATA%\Godot\app_userdata\The Archipelago Atlas) when it starts, and there's no setting to stop it.
#>

# Points a test run's user folders and temp folder at empty stand-ins under $Scratch; returns where they are.
function Set-StandInUserFolders([System.Diagnostics.ProcessStartInfo]$Info, [string]$Scratch) {
    $outside = Join-Path $Scratch 'outside'
    $folders = [ordered]@{ APPDATA = 'AppData\Roaming'; LOCALAPPDATA = 'AppData\Local'; TEMP = 'Temp'; TMP = 'Temp' }
    foreach ($name in $folders.Keys) {
        $dir = Join-Path $outside $folders[$name]
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $Info.EnvironmentVariables[$name] = $dir
    }
    return $outside
}

# Lists any file the run wrote to the stand-ins; returns how many there were (0 = nothing written outside Atlas's folder).
function Test-Footprint([string]$Outside) {
    $written = @(Get-ChildItem -LiteralPath $Outside -Recurse -File -Force -ErrorAction SilentlyContinue)
    if ($written.Count -eq 0) {
        Write-Host "Footprint: nothing was written outside Atlas's folder." -ForegroundColor Green
        return 0
    }
    Write-Host "FOOTPRINT: the run wrote $($written.Count) file(s) outside Atlas's folder:" -ForegroundColor Red
    $written | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.FullName.Substring($Outside.Length).TrimStart('\'))" }
    return $written.Count
}
