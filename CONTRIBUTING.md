# Contributing to The Archipelago Atlas

Thanks for helping. This page covers how to build and test Atlas, the rules every change follows, and how to propose one. By taking part you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## The principles every change keeps

1. **Stability first.**
   - Data is never lost.
   - A failure is never shown as a valid result.
   - Every new protection gets a self-test.
2. **Light on servers.**
   - Archipelago's servers are volunteer-run, so Atlas sends as few requests as it can, one at a time, and backs off after a failure.
   - Every other site gets the same care.
3. **Your PC is yours.** Atlas never does any of these without asking:
   - reads or writes outside its own folder,
   - searches the PC,
   - contacts a new site.

   It asks at the moment it needs to, and remembers only "Always allow", which the user can take back.
4. **Professional.**
   - Plain language.
   - A consistent UI.
   - Credited and properly licensed.
   - Developer details only in developer views.

There's also the no-cheating rule: anything spoiler-adjacent (like sphere data) is only shown where it's allowed, and never in race mode.

## Building and running

You need:
- Windows 10 or 11.
- The .NET 10 SDK: 10.0.401 or a later 10.0.4xx patch (`global.json` pins it, so local formatting matches CI's).
- Godot 4.7.2 (.NET build) unzipped into `Godot_Engine/Godot_v4.7.2-stable_mono_win64/` in the repository folder (git ignores it), or set `ATLAS_GODOT` to its `_console.exe`.
- Python 3 on the PATH, for the tests that run the engine's runner and the fake logic engine. Without it those tests are skipped; CI always runs them (on Python 3.12).

| What | How |
|---|---|
| Build | `dotnet build AP_Atlas_Source/AP_Atlas.sln` |
| Run | `Launch_The_Archipelago_Atlas.bat` |
| Self-test and UI test | `AP_Atlas_Source/Tools/run_selftest.ps1` (builds, then runs the self-test and the UI test, each in a new, empty scratch folder; never your real data). Set `ATLAS_SELFTEST_SETUP=1` to also set up the portable engine from nothing (about 55 MB of downloads), after changing engine setup. While working on a few checks, `ATLAS_SELFTEST_ONLY` and `ATLAS_UITEST_ONLY` (part of a check's or scenario's name) run only those |
| Guard rails | `AP_Atlas_Source/Tools/check_guards.ps1` |
| Pre-push checks | Turn them on once per clone: `git config core.hooksPath .githooks`. Every push then first runs the guard rails, the build, formatting and the unit tests (about a minute), and stops if one fails. Never skip them (`--no-verify`) |
| Unit tests | `dotnet test --solution AP_Atlas_Source/AP_Atlas.sln` (`AP_Atlas.Core.Tests`: fast, no Godot) |
| Formatting | `dotnet format whitespace AP_Atlas_Source/AP_Atlas.sln` (C# files use CRLF line endings) |
| Visual check | `AP_Atlas_Source/Tools/run_visualcheck.ps1 [-Baseline <folder>]` (pictures of the main screens, as a new user sees them; with `-Baseline`, a `.diff.png` marks every changed pixel) |

CI runs the build, formatting, guard rails, unit tests and self-test on every push and pull request.

The repository holds three .NET projects:
- `AP_Atlas_Source/AP_Atlas.csproj`: the Godot app (the window and everything that needs Godot).
- `AP_Atlas.Core/`: code that doesn't need Godot (saving, logging, the web client, parsers). It can't touch the window, so it's safe on any thread.
- `AP_Atlas.Core.Tests/`: its xUnit tests.

New code that doesn't need Godot belongs in `AP_Atlas.Core`, with tests. The visual check needs a graphics card, so run it yourself before and after any change to how Atlas looks, and compare on the same PC.

## Writing code

- **Language and UI:** C# 14 on .NET 10. The UI is built in code from Godot Controls. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains how Atlas fits together.
- **Use the shared building blocks; don't work around them:**

  | For | Use |
  |---|---|
  | Saving, and deleting what was saved | `SafeFile` (`SafeFile.Delete` removes the backup too) |
  | Web requests | `PoliteHttp` (`GitHubApi` for GitHub) |
  | Opening links and folders | `ExternalLinks` |
  | Anything outside Atlas's folder, or a new site | `Permissions` with `PermissionDialog` |
  | Connecting to an Archipelago server | `SessionManager` (one connection at a time, time limits, careful reconnects; its sessions keep the games' names in Atlas's folder) |
  | Starting a program | `EngineInstall.StartInfo` or `AtlasEngine.SetupStartInfo` (keep its temporary files and caches in Atlas's folder) |
  | Secrets | `Secrets` |
  | Work nobody awaits (button handlers, background checks) | `Async.Fire(task, "what it's doing")`, never `async void` or `_ = …` |
  | Updating the window from another thread, or later | `Ui.Defer(owner, …)`, never `Callable.From(…).CallDeferred()` |
  | Running Lua | `PackScriptHost`, where a pack's scripts run under limits |
  | Saving a file | `SafeFile`. It keeps a backup and survives a crash mid-save. The few places that write files directly (the log, a crash report, an export the user chose) are listed in the guard rails |
  | Closing a server connection | `SessionManager` (a connection still opening is closed as it opens; its thread is given back) |
  | Background work | `Task.Run` or async code, never a thread of its own |
  | Memory | Leave collections to .NET: forcing one (`GC.Collect`) pauses all of Atlas |
  | A failure you choose to ignore | Say why on the same line (`catch { } // it exited meanwhile`), or log it |
  | A view following an event | A `TreeSubscriptions` child (`AddChild(new TreeSubscriptions().On(subscribe, unsubscribe))`), never `+=` in `_Ready`: it follows the view in and out of the window, so the view can be moved |

  The guard rails enforce the riskiest of these, locally before each push and in CI. A rule with a number allows only that many uses in its file: one helper does the job, and everything else calls it.
- **Warnings are errors:** any compiler, analyzer, MSBuild or NuGet warning stops the build (`Directory.Build.props`). Fix the cause rather than silencing it. A NuGet warning about a known vulnerability means updating that package.
  - So does dead code: a private member nothing uses, or a private field nothing reads (IDE0051, IDE0052). A member only a serializer uses (it's set by name, so nothing calls it) gets `[SuppressMessage("Style", "IDE0051", Justification = "…")]` saying so.
- **Null checks:**
  - Nullable reference checks are on, and new code keeps them on.
  - Files that start with `#nullable disable` predate the checks. When you rework one, annotate it and remove that line, then lower the limit in `Tools/check_guards.ps1`.
  - The guard rails stop the number of such files from growing, and fail until the limit is lowered when one is migrated, so it can't creep back up.
- **Async code:** the build runs Microsoft's async analyzers. An unobserved task, `async void`, a blocking wait on unfinished work, or `ContinueWith` without a `TaskScheduler` stops the build. Methods that return a task end in `Async`.
- **Tests:** every new protection gets a check in `Scripts/Core/SelfTest*.cs` or `AP_Atlas.Core.Tests`, and tests never touch real data. Connection tests use `FakeArchipelagoServer`, never a real server; Cheese Tracker tests use `FakeCheeseServer`, never the real site; downloads and other web requests use the self-test's `FakeWebServer`; and the UI test runs logic on `FakeLogicEngine`. No test reaches a real site. What the window does with them gets a scenario in `MainTrackerWindow.UiTest.cs`. A test's expected results are written out in the test, never read from the code it checks.
- **Nothing outside Atlas's folder:** the self-test and the visual check fail if a run writes anything to the user's folders or the temp folder (the footprint check). Godot's log and shader cache stay off in `project.godot`.
- **Let the window idle:** Atlas runs in Godot's low-processor mode, which draws only when something on screen changes. Anything that changes the screen every frame keeps the whole window drawing, about 145 times a second. Watch for:
  - a node that runs every frame and changes what's shown, such as a `Camera2D` left processing;
  - a timer that re-applies the same value where setting it always redraws, such as `AddThemeColorOverride` with the same colour.
  - Set such values only when they change. The UI test's "Idle" scenario checks a connected slot.
- **Pinned libraries:** the guard rails pin MoonSharp (2.0.0) and Archipelago.MultiClient.Net (6.7.1), because Atlas relies on how they work inside. Dependabot may propose an update; CI then fails until it's done properly:
  - **MoonSharp:** the limits on pack scripts rely on its debugger hook seeing every step (nested calls and coroutines included), on its fixed stacks, on `table.sort` dropping errors that aren't Lua's, and on which library calls throw .NET exceptions. Re-check each against the new version's source (decompile it), then run the pack script self-tests and the corpus check (`ATLAS_SELFTEST_PACKS`).
- **Updating Archipelago.MultiClient.Net:** `AtlasSessions` relies on the library's internal data cache, and on how a connection's send loop ends once it's woken (see `LibraryThreads`). The unit tests check both, and that the library reaches nothing else on the PC. If they fail, look at what changed before using the new version.
- **The logic engine's channel:** an engine's standard output carries answers only, each with its request's id; Atlas takes nothing else for an answer. Bridge components write answers with `send()`, and everything that prints goes to standard error (the runner and `protect_channel()` see to it), which Atlas logs. A bridge that can't load says so with `{"event": "boot_failed"}`.
- **Map pack scripts:** each piece of a pack's scripts' work runs under limits (see `ScriptLimits` in `PackScriptHost`). Keep it that way when adding to the PopTracker API:
  - Make each function the scripts can call with `Callback`, so its failures are Lua errors.
  - Call Lua from C# only through `CallLua`, and compile it only through `Compile` (on the compiler's thread: `OnCompilerThread`), which refuses code nested too deep for the compiler's stack.
  - A library function that can build something big in one call (as `string.rep` can) needs a check before it runs.
  - The self-tests "Pack scripts: work that runs away is stopped…" cover each way a script can run away. The corpus check (`ATLAS_SELFTEST_PACKS`) shows that real packs stay far below the limits.
- **Godot's `.uid` files:** Godot makes one next to each script (`Foo.cs.uid`) when it opens or imports the project. Commit it with the script; the guard rails check.
- **Third-party code, data or art:**
  - Use only things under a license compatible with MIT (MIT, BSD, Apache-2.0, OFL for fonts, and so on), and credit them in [CREDITS.md](CREDITS.md). Anything that ships in Atlas also goes in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
  - Never copy GPL or AGPL code (PopTracker, Cheese Tracker) into Atlas.
  - Never use anything that has no license.

## Proposing a change

1. For anything bigger than a small fix, open an issue first so we can agree on the approach.
2. Keep pull requests focused, and fill in the checklist.
3. Add your change to `CHANGELOG.md` under "Unreleased".

By contributing, you agree that your contribution is licensed under the project's [MIT License](LICENSE).

## Versions and releases

- **Version numbers:** Atlas uses [Semantic Versioning](https://semver.org). The version is set once, in `Directory.Build.props` at the repository root, and every project shares it.
- **Releasing:** `AP_Atlas_Source/Tools/bump_version.ps1 <version>` sets the version and opens its changelog section. Pushing a tag like `v0.1.0-beta.1` builds the release.

## Security

Please report security problems privately, as described in [SECURITY.md](SECURITY.md).
