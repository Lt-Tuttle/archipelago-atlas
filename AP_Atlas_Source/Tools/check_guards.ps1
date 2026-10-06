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
      - Changing how many engines a multiworld's slots share (EnginePools.TestMaxEngines): only the UI test.
      - Sending chat or commands, or changing a connection's tags: only SessionManager, which keeps one connection per
        multiworld team receiving the room's text, and switches a slot's text on before its command (so the answer arrives).
      - Running Lua (MoonSharp): only PackScriptHost, where each piece of a pack's scripts' work runs under limits (work
        that runs away is stopped, not left to freeze or crash Atlas). Inside it, one place calls Lua (CallLua), one
        compiles it, and three helpers make the functions scripts call (Callback, Checked, AsLuaErrors): anything else
        would bypass the limits or the way failures are handled.
      - Closing a server connection: only SessionManager's DisconnectAsync (a connection still opening is closed as it
        opens), and giving back a connection's thread (AtlasSessions.Finished) only in SessionManager.
      - Forcing a garbage collection, which pauses all of Atlas: only in the self-test.
      - Starting a thread of its own: only PackScriptHost's compiler thread, which needs a big stack, and the self-test's
        check that it does (Task.Run and async code share the pool Atlas sizes for its connections).
      - Looking up the user's own folders (Documents, AppData, Program Files, the temp folder): only AtlasEngine's install
        search, which runs only after the user agrees. Atlas keeps everything in its own folder.
      - Saving a whole file without SafeFile: only where it's checked to be safe (the log, a crash report, an export the
        user chose, files the engine setup regenerates, a store that writes a temporary file and moves it, the tests).
        Atlas's own data goes through SafeFile, which keeps a backup and survives a crash mid-save.
      - Reading a zip without SafeZip: nowhere else. Map packs and apworlds come from outside Atlas, and a zip's headers
        can lie: SafeZip refuses zip64 zips before they're listed, and counts every file's bytes as they're unpacked, with
        limits per file and per zip. Making a zip (ZipArchiveMode.Create, CreateEntry(...).Open()) is fine anywhere.
      - Unpacking a zip into a folder: only the engine's setup, for its downloads checked against pinned SHA-256 hashes
        (Python, pip, Archipelago) before they're unpacked.
      - Decoding an image: only PackImages.DecodeImage, which takes the image's size from its header and checks it against
        an ImageBudget first (a decoder sets aside width x height x 4 bytes from the header alone), and the visual check,
        for its own screenshots.
      - Rich text that reads BBCode: only SafeRichText, which lets only Atlas's own tags through (Bbcode.Safe). Godot opens
        the files named in [img] and [font] tags, and for a network path that means connecting to another computer with
        the user's Windows sign-in. Text from outside Atlas is escaped too (Bbcode.Escape); the self-test and the UI test
        show it never makes Atlas open a file. The self-test's check that its detection works is the one other use.
      - Markup in a log message: nowhere. Messages are plain text, escaped for the window (they often quote outside text),
        so a tag in one would show as text; a line's colour is an argument (Logger.LogInfo(message, color)).
      - An empty catch that doesn't say why on the same line: nowhere. A failure is logged, handled, or explained.
    Where a rule names a number, the file may do it only that many times: one helper does it, everything else uses it.
    Libraries whose internals Atlas relies on are pinned (MoonSharp, Archipelago.MultiClient.Net): update one only with
    the checks CONTRIBUTING lists for it.
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
    @{ Name = 'Changing how many engines a multiworld runs'; Pattern = '\bTestMaxEngines\s*=(?!=)'; Allowed = @('AP_Atlas_Source\Scripts\MainTrackerWindow.UiTest.cs') },
    @{ Name = 'Sending chat or changing a connection''s tags outside SessionManager'; Pattern = 'new\s+(SayPacket|ConnectUpdatePacket)\b|\.UpdateConnectionOptions\s*\('
       Allowed = @('AP_Atlas.Core\Connections\SessionManager.Text.cs') },
    @{ Name = 'Running Lua outside PackScriptHost'; Pattern = 'using\s+MoonSharp|MoonSharp\.Interpreter\.'; Allowed = @('AP_Atlas_Source\Scripts\Core\PopTracker\PackScriptHost.cs') },
    @{ Name = 'Calling Lua outside PackScriptHost.CallLua'; Pattern = '_script\.Call\s*\('; Allowed = @('AP_Atlas_Source\Scripts\Core\PopTracker\PackScriptHost.cs'); Max = 1 },
    @{ Name = 'Compiling Lua outside PackScriptHost.Compile'; Pattern = '_script\.(DoString|DoFile|LoadString|LoadFile|LoadStream|LoadFunction)\s*\('
       Allowed = @('AP_Atlas_Source\Scripts\Core\PopTracker\PackScriptHost.cs'); Max = 1 },
    @{ Name = 'Making a function for pack scripts outside Callback, Checked and AsLuaErrors'; Pattern = 'DynValue\.NewCallback\s*\('
       Allowed = @('AP_Atlas_Source\Scripts\Core\PopTracker\PackScriptHost.cs'); Max = 3 },
    @{ Name = 'Closing a server connection outside SessionManager.DisconnectAsync'; Pattern = '\.Socket\.DisconnectAsync\s*\('; Allowed = @('AP_Atlas.Core\Connections\SessionManager.cs'); Max = 1 },
    @{ Name = 'Giving back a connection''s thread outside SessionManager'; Pattern = 'AtlasSessions\.Finished\s*\('; Allowed = @('AP_Atlas.Core\Connections\SessionManager.cs') },
    @{ Name = 'Forcing a garbage collection (it pauses all of Atlas)'; Pattern = '\bGC\.Collect\s*\('
       Allowed = @('AP_Atlas_Source\Scripts\Core\SelfTest.cs', 'AP_Atlas_Source\Scripts\Core\SelfTest.Reliability.cs') },
    @{ Name = 'Starting a thread of its own (use Task.Run or async code)'; Pattern = 'new\s+(System\.Threading\.)?Thread\s*\('
       Allowed = @('AP_Atlas_Source\Scripts\Core\PopTracker\PackScriptHost.cs', 'AP_Atlas_Source\Scripts\Core\SelfTest.Reliability.cs'); Max = 1 },
    @{ Name = 'Looking up the user''s own folders'; Pattern = 'GetFolderPath\s*\(|\bSpecialFolder\.|GetTempPath\s*\(|GetTempFileName\s*\('
       Allowed = @('AP_Atlas_Source\Scripts\Core\Engine\AtlasEngine.cs') },
    @{ Name = 'Saving a whole file without SafeFile'; Pattern = '(?<!\w)File\.(WriteAll|AppendAll)\w*\s*\('
       Allowed = @('AP_Atlas.Core\Logger.cs', 'AP_Atlas.Core\Connections\DataPackageStore.cs', 'AP_Atlas.Core\Testing\FakeLogicEngine.cs',
                   'AP_Atlas_Source\Scripts\Core\CrashGuard.cs', 'AP_Atlas_Source\Scripts\Core\Annotations.cs', 'AP_Atlas_Source\Scripts\Core\Engine\AtlasEngine.cs',
                   'AP_Atlas_Source\Scripts\Core\SelfTest.cs', 'AP_Atlas_Source\Scripts\Core\SelfTest.Reliability.cs', 'AP_Atlas_Source\Scripts\Core\SelfTest.Safety.cs',
                   'AP_Atlas_Source\Scripts\MainTrackerWindow.UiTest.cs') },
    @{ Name = 'Reading a zip without SafeZip'
       Pattern = 'ZipFile\.Open(Read)?(Async)?\s*\((?![^)]*ZipArchiveMode\.Create)|new\s+(System\.IO\.Compression\.)?ZipArchive\s*\((?![^)]*ZipArchiveMode\.Create)|ZipArchive\.CreateAsync\s*\((?![^)]*ZipArchiveMode\.Create)|(?<!CreateEntry\([^()]*\))\.Open(Async)?\s*\(\s*\)'
       Allowed = @('AP_Atlas.Core\SafeZip.cs'); Max = 2 },
    @{ Name = 'Unpacking a zip into a folder'; Pattern = 'ExtractToDirectory|ExtractToFile'
       Allowed = @('AP_Atlas_Source\Scripts\Core\Engine\AtlasEngine.cs'); Max = 3 },
    @{ Name = 'Decoding an image without checking its size first (use PackImages.DecodeImage)'; Pattern = 'Load(Png|Jpg|Webp|Bmp|Tga|Svg|Ktx|Exr|Dds)FromBuffer\s*\(|\bImage\.LoadFromFile\s*\('
       Allowed = @('AP_Atlas_Source\Scripts\Core\PopTracker\PackImages.cs', 'AP_Atlas_Source\Scripts\MainTrackerWindow.VisualCheck.cs'); Max = 3 },
    @{ Name = 'Rich text that reads BBCode outside SafeRichText (outside text could make Godot open a file)'
       Pattern = 'BbcodeEnabled\s*=(?!\s*false\b)|\bSetUseBbcode\s*\(|\bAppendText\s*\(|\bParseBbcode\s*\('
       Allowed = @('AP_Atlas_Source\Scripts\UI\SafeRichText.cs', 'AP_Atlas_Source\Scripts\Core\SelfTest.Safety.cs'); Max = 2 },
    @{ Name = 'Markup in a log message (messages are plain text, shown as written: give the line''s colour as an argument)'
       Pattern = '\b(LogToSystem|LogToDebug|LogInfo|LogWarning|LogError|LogDebug|_logAction|AppendDebugLog)\s*\(.*\[/?(color|bgcolor|b|i|u|s|url|code)[=\]]'; Allowed = @() },
    @{ Name = 'An empty catch that doesn''t say why'; Pattern ='catch(\s*\([^)]*\))?\s*\{\s*\}(?!\s*//)'; Allowed = @() }
)

$files = $roots | ForEach-Object { Get-ChildItem -Path $_ -Recurse -Filter '*.cs' -File } |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$broken = 0
foreach ($rule in $rules) {
    $all = @($files | Select-String -Pattern $rule.Pattern -CaseSensitive:([bool]$rule.CaseSensitive))
    foreach ($hit in $all) {
        $relative = $hit.Path.Substring($repo.Length).TrimStart('\', '/')
        if ($rule.Allowed -contains $relative) { continue }
        Write-Host "GUARD: $($rule.Name) is only allowed in $(if ($rule.Allowed.Count) { $rule.Allowed -join ', ' } else { 'no file' }), but $relative line $($hit.LineNumber) does it:" -ForegroundColor Red
        Write-Host "    $($hit.Line.Trim())"
        $broken++
    }
    # Where it is allowed, a numbered rule allows it only that many times per file: one helper does it, the rest call that.
    if ($rule.Max) {
        foreach ($group in ($all | Group-Object Path)) {
            if ($group.Count -le $rule.Max) { continue }
            $relative = $group.Name.Substring($repo.Length).TrimStart('\', '/')
            Write-Host "GUARD: $($rule.Name): $relative does it $($group.Count) times, but only $($rule.Max) is allowed there (one place does it; everything else goes through that):" -ForegroundColor Red
            foreach ($hit in $group.Group) { Write-Host "    line $($hit.LineNumber): $($hit.Line.Trim())" }
            $broken++
        }
    }
}

# Libraries whose internals Atlas relies on are pinned. Updating one takes the checks CONTRIBUTING lists for it (its tests
# passing isn't enough: they test the behaviour Atlas expects of this version), then this list.
$pinned = @(
    @{ Package = 'MoonSharp'; Version = '2.0.0'; Project = 'AP_Atlas_Source\AP_Atlas.csproj'
       Why = 'the limits on pack scripts rely on its debugger hook, its stacks and how its library handles errors' },
    @{ Package = 'Archipelago.MultiClient.Net'; Version = '6.7.1'; Project = 'AP_Atlas.Core\AP_Atlas.Core.csproj'
       Why = 'AtlasSessions relies on its internal data cache, and on how its connections open, close and end their send loop' }
)
foreach ($pin in $pinned) {
    $text = Get-Content -Raw -LiteralPath (Join-Path $repo $pin.Project)
    $found = [regex]::Match($text, '<PackageReference\s+Include="' + [regex]::Escape($pin.Package) + '"\s+Version="([^"]+)"')
    if ($found.Success -and $found.Groups[1].Value -eq $pin.Version) { continue }
    $now = if ($found.Success) { $found.Groups[1].Value } else { 'no reference' }
    Write-Host "GUARD: $($pin.Package) is pinned at $($pin.Version) in $($pin.Project) ($($pin.Why)), but it's $now. Update it only with the checks in CONTRIBUTING." -ForegroundColor Red
    $broken++
}
# Nullable checks: classes whose files still start with "#nullable disable" predate them and are annotated as they're
# reworked. Counted by class (the file name up to its first dot), so splitting a class into partial files doesn't
# change the count. This number only goes down, and must match: lower it in the same change that migrates a class, so
# the progress can't be undone later.
$nullableOptOutLimit = 55
$optedOut = @($files | Where-Object { (Get-Content -LiteralPath $_.FullName -TotalCount 1) -eq '#nullable disable' } |
    ForEach-Object { $_.Name.Split('.')[0] } | Sort-Object -Unique).Count
if ($optedOut -gt $nullableOptOutLimit) {
    Write-Host "GUARD: $optedOut classes turn nullable checks off, more than the $nullableOptOutLimit allowed. New code keeps them on." -ForegroundColor Red
    $broken++
}
elseif ($optedOut -lt $nullableOptOutLimit) {
    Write-Host "GUARD: only $optedOut classes still turn nullable checks off: lower the limit in check_guards.ps1 to $optedOut, so it can't creep back up." -ForegroundColor Red
    $broken++
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
