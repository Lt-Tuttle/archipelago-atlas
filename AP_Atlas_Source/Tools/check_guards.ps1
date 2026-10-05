<#
.SYNOPSIS
    Guard rails: fails if code that must stay in one audited place shows up anywhere else.

.DESCRIPTION
    Each rule names something that could hurt the user or a site if used carelessly, and the only files allowed to use it:
      - Opening links or files through Windows: only ExternalLinks (https pages and existing folders, nothing else).
      - Making web requests: only PoliteHttp (one request at a time per site, backoff, size and time limits).
      - Reading the Windows registry: only AtlasEngine's install search, which runs only after the user agrees.
      - Searching the PC for Archipelago: only from the Atlas Engine window's Find button, after the user agrees.
      - Letting Windows run a program by file type (UseShellExecute = true): nowhere.
      - "async void" methods, whose failures vanish: nowhere (start the work with Async.Fire instead).
      - Handing work to the main thread with Callable.From(...).CallDeferred(): only in Ui.Defer, which skips work whose
        owner was freed and logs a failure.
      - Throwing away a call's result ("_ = SomethingAsync()"), which hides a failed task: only in the self-test's fake servers (and Async.cs, which describes the rule).
      - Creating an Archipelago session: only AtlasSessions, which keeps the games' names in Atlas's folder (the
        connection library's own cache is in %LocalAppData%).
      - Connecting a session: only SessionManager, which sets the time limit, closes everything when Atlas closes and
        reconnects politely (the self-test may create one it never connects).
      - Starting a program: only the engine's launch points (EngineInstall.StartInfo, AtlasEngine.SetupStartInfo), which
        keep its temporary files and caches in Atlas's folder, and the self-test.
      - Running the engine on a Python other than the portable engine's own (AtlasEngine.TestPython): only the UI test,
        for its fake engine.
      - Sending chat or commands, or changing a connection's tags: only SessionManager, which keeps one connection per
        multiworld team receiving the room's text, and switches a slot's text on before its command (so the answer arrives).
    It also checks that every script in the Godot project has its .uid file (Godot makes one per script; it's committed
    with the script, or every fresh copy of the project gets new ones). CI runs this before Godot's import, so it checks
    what was committed.
    Run it from anywhere; CI runs it on every push. Exit code 0 means every rule holds.
#>
$ErrorActionPreference = 'Stop'
# The app's scripts and the Godot-free library; paths below are relative to the repository root.
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$roots = @('AP_Atlas_Source\Scripts', 'AP_Atlas.Core') | ForEach-Object { Join-Path $repo $_ }

$rules = @(
    @{ Name = 'Opening links or files through Windows'; Pattern = 'OS\.ShellOpen\s*\('; Allowed = @('AP_Atlas_Source\Scripts\Core\ExternalLinks.cs') },
    @{ Name = 'Making web requests outside PoliteHttp'; Pattern = 'new\s+(System\.Net\.Http\.)?HttpClient\s*\(|WebClient\s*\(|HttpWebRequest'; Allowed = @('AP_Atlas.Core\PoliteHttp.cs') },
    @{ Name = 'Reading the Windows registry'; Pattern = 'Microsoft\.Win32\.Registry|RegistryKey'; Allowed = @('AP_Atlas_Source\Scripts\Core\Engine\AtlasEngine.cs') },
    @{ Name = 'Searching the PC for Archipelago'; Pattern = 'FindArchipelagoInstalls\s*\('; Allowed = @('AP_Atlas_Source\Scripts\Core\Engine\AtlasEngine.cs', 'AP_Atlas_Source\Scripts\UI\AtlasEngineWindow.cs') },
    @{ Name = 'Letting Windows run a program by file type'; Pattern = 'UseShellExecute\s*=\s*true'; Allowed = @() },
    @{ Name = 'An async void method (use Async.Fire)'; Pattern = '\basync\s+void\b'; Allowed = @('AP_Atlas.Core\Async.cs') },
    @{ Name = 'Handing work to the main thread without Ui.Defer'; Pattern = '\)\.CallDeferred\(\)'; Allowed = @('AP_Atlas_Source\Scripts\UI\Ui.cs') },
    @{ Name = 'Throwing away a call''s result (use Async.Fire for tasks)'; Pattern = '(?<!var\s)(?<![\w.])_\s*=\s*[^;=>]*\('; Allowed = @('AP_Atlas.Core\Async.cs', 'AP_Atlas_Source\Scripts\Core\SelfTest.Cheese.cs', 'AP_Atlas_Source\Scripts\Core\SelfTest.Spheres.cs') },
    @{ Name = 'Creating an Archipelago session outside AtlasSessions'; Pattern = 'ArchipelagoSessionFactory'; Allowed = @('AP_Atlas.Core\Connections\AtlasSessions.cs') },
    @{ Name = 'Connecting a session outside SessionManager'; Pattern = 'AtlasSessions\.Create\s*\(|TryConnectAndLogin|\.LoginAsync\s*\(|Session\w*\.ConnectAsync\s*\(\s*\)'
       Allowed = @('AP_Atlas.Core\Connections\SessionManager.cs', 'AP_Atlas_Source\Scripts\Core\SelfTest.Footprint.cs') },
    # Case-sensitive, so starting an engine process made at a launch point (process.Start()) isn't mistaken for one.
    @{ Name = 'Starting a program outside the engine''s launch points'; CaseSensitive = $true
       Pattern = 'new\s+(System\.Diagnostics\.)?ProcessStartInfo\b|(?<![\w.])(System\.Diagnostics\.)?Process\.Start\s*\(|\bOS\.(Execute|ExecuteWithPipe|CreateProcess|CreateInstance)\s*\('
       Allowed = @('AP_Atlas_Source\Scripts\Core\Engine\AtlasEngine.cs', 'AP_Atlas_Source\Scripts\Core\Engine\EngineInstall.cs', 'AP_Atlas_Source\Scripts\Core\SelfTest.cs') },
    @{ Name = 'Running the engine on another Python'; Pattern = '\bTestPython\s*=(?!=)'; Allowed = @('AP_Atlas_Source\Scripts\MainTrackerWindow.UiTest.cs') },
    @{ Name = 'Sending chat or changing a connection''s tags outside SessionManager'; Pattern = 'new\s+(SayPacket|ConnectUpdatePacket)\b|\.UpdateConnectionOptions\s*\('
       Allowed = @('AP_Atlas.Core\Connections\SessionManager.Text.cs') }
)

$files = $roots | ForEach-Object { Get-ChildItem -Path $_ -Recurse -Filter '*.cs' -File } |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$broken = 0
foreach ($rule in $rules) {
    $hits = $files | Select-String -Pattern $rule.Pattern -CaseSensitive:([bool]$rule.CaseSensitive) | Where-Object {
        $relative = $_.Path.Substring($repo.Length).TrimStart('\', '/')
        -not ($rule.Allowed -contains $relative)
    }
    foreach ($hit in $hits) {
        $relative = $hit.Path.Substring($repo.Length).TrimStart('\', '/')
        Write-Host "GUARD: $($rule.Name) is only allowed in $(if ($rule.Allowed.Count) { $rule.Allowed -join ', ' } else { 'no file' }), but $relative line $($hit.LineNumber) does it:" -ForegroundColor Red
        Write-Host "    $($hit.Line.Trim())"
        $broken++
    }
}
# Nullable checks: classes whose files still start with "#nullable disable" predate them and are annotated as they're
# reworked. Counted by class (the file name up to its first dot), so splitting a class into partial files doesn't
# change the count. This number only goes down: lower it when a class is migrated.
$nullableOptOutLimit = 56
$optedOut = @($files | Where-Object { (Get-Content -LiteralPath $_.FullName -TotalCount 1) -eq '#nullable disable' } |
    ForEach-Object { $_.Name.Split('.')[0] } | Sort-Object -Unique).Count
if ($optedOut -gt $nullableOptOutLimit) {
    Write-Host "GUARD: $optedOut classes turn nullable checks off, more than the $nullableOptOutLimit allowed. New code keeps them on." -ForegroundColor Red
    $broken++
}
elseif ($optedOut -lt $nullableOptOutLimit) {
    Write-Host "Nullable checks: $optedOut classes still opted out; lower the limit in check_guards.ps1 to $optedOut." -ForegroundColor Yellow
}

# Godot's .uid files: one per script, committed with it.
$godotScripts = Get-ChildItem -Path (Join-Path $repo 'AP_Atlas_Source\Scripts') -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
foreach ($script in $godotScripts) {
    if (Test-Path -LiteralPath ($script.FullName + '.uid')) { continue }
    $relative = $script.FullName.Substring($repo.Length).TrimStart('\', '/')
    Write-Host "GUARD: $relative has no .uid file. Run the project once in Godot (or with --import) and commit the .uid with the script." -ForegroundColor Red
    $broken++
}

if ($broken -gt 0) {
    Write-Host "$broken guard rail problem(s)." -ForegroundColor Red
    exit 1
}
Write-Host "Guard rails hold ($($rules.Count) rules, $($files.Count) files)." -ForegroundColor Green
exit 0
