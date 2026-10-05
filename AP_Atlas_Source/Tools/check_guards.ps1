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
    Run it from anywhere; CI runs it on every push. Exit code 0 means every rule holds.
#>
$ErrorActionPreference = 'Stop'
$scripts = Join-Path (Split-Path -Parent $PSScriptRoot) 'Scripts'

$rules = @(
    @{ Name = 'Opening links or files through Windows'; Pattern = 'OS\.ShellOpen\s*\('; Allowed = @('Core\ExternalLinks.cs') },
    @{ Name = 'Making web requests outside PoliteHttp'; Pattern = 'new\s+(System\.Net\.Http\.)?HttpClient\s*\(|WebClient\s*\(|HttpWebRequest'; Allowed = @('Core\PoliteHttp.cs') },
    @{ Name = 'Reading the Windows registry'; Pattern = 'Microsoft\.Win32\.Registry|RegistryKey'; Allowed = @('Core\Engine\AtlasEngine.cs') },
    @{ Name = 'Searching the PC for Archipelago'; Pattern = 'FindArchipelagoInstalls\s*\('; Allowed = @('Core\Engine\AtlasEngine.cs', 'UI\AtlasEngineWindow.cs') },
    @{ Name = 'Letting Windows run a program by file type'; Pattern = 'UseShellExecute\s*=\s*true'; Allowed = @() },
    @{ Name = 'An async void method (use Async.Fire)'; Pattern = '\basync\s+void\b'; Allowed = @('Core\Async.cs') },
    @{ Name = 'Handing work to the main thread without Ui.Defer'; Pattern = '\)\.CallDeferred\(\)'; Allowed = @('UI\Ui.cs') },
    @{ Name = 'Throwing away a call''s result (use Async.Fire for tasks)'; Pattern = '(?<!var\s)(?<![\w.])_\s*=\s*[^;=>]*\('; Allowed = @('Core\Async.cs', 'Core\SelfTest.Cheese.cs', 'Core\SelfTest.Spheres.cs') }
)

$files = Get-ChildItem -Path $scripts -Recurse -Filter '*.cs' -File
$broken = 0
foreach ($rule in $rules) {
    $hits = $files | Select-String -Pattern $rule.Pattern | Where-Object {
        $relative = $_.Path.Substring($scripts.Length).TrimStart('\', '/')
        -not ($rule.Allowed -contains $relative)
    }
    foreach ($hit in $hits) {
        $relative = $hit.Path.Substring($scripts.Length).TrimStart('\', '/')
        Write-Host "GUARD: $($rule.Name) is only allowed in $(if ($rule.Allowed.Count) { $rule.Allowed -join ', ' } else { 'no file' }), but $relative line $($hit.LineNumber) does it:" -ForegroundColor Red
        Write-Host "    $($hit.Line.Trim())"
        $broken++
    }
}
if ($broken -gt 0) {
    Write-Host "$broken guard rail problem(s)." -ForegroundColor Red
    exit 1
}
Write-Host "Guard rails hold ($($rules.Count) rules, $($files.Count) files)." -ForegroundColor Green
exit 0
