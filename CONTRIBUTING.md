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

| What | How |
|---|---|
| Build | `dotnet build AP_Atlas_Source/AP_Atlas.sln` |
| Run | `Launch_The_Archipelago_Atlas.bat` |
| Self-test | `AP_Atlas_Source/Tools/run_selftest.ps1` (builds, then tests in a new, empty scratch folder; never your real data). Set `ATLAS_SELFTEST_SETUP=1` to also set up the portable engine from nothing (about 55 MB of downloads), after changing engine setup |
| Guard rails | `AP_Atlas_Source/Tools/check_guards.ps1` |
| Formatting | `dotnet format whitespace AP_Atlas_Source/AP_Atlas.csproj` (C# files use CRLF line endings) |
| Visual check | `AP_Atlas_Source/Tools/run_visualcheck.ps1 [-Baseline <folder>]` (pictures of the main screens, as a new user sees them; with `-Baseline`, a `.diff.png` marks every changed pixel) |

CI runs the build, formatting, guard rails and self-test on every push and pull request. The visual check needs a graphics card, so run it yourself before and after any change to how Atlas looks, and compare on the same PC.

## Writing code

- **Language and UI:** C# 14 on .NET 10. The UI is built in code from Godot Controls. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains how Atlas fits together.
- **Use the shared building blocks; don't work around them:**

  | For | Use |
  |---|---|
  | Saving | `SafeFile` |
  | Web requests | `PoliteHttp` (`GitHubApi` for GitHub) |
  | Opening links and folders | `ExternalLinks` |
  | Anything outside Atlas's folder, or a new site | `Permissions` with `PermissionDialog` |
  | Secrets | `Secrets` |
  | Work nobody awaits (button handlers, background checks) | `Async.Fire(task, "what it's doing")`, never `async void` or `_ = …` |

  The guard rails enforce the riskiest of these in CI.
- **Async code:** the build runs Microsoft's async analyzers. An unobserved task, `async void`, a blocking wait on unfinished work, or `ContinueWith` without a `TaskScheduler` stops the build. Methods that return a task end in `Async`.
- **Tests:** every new protection gets a check in `Scripts/Core/SelfTest*.cs`, and tests never touch real data.
- **Godot's `.uid` files:** Godot makes one next to each script (`Foo.cs.uid`). Commit it with the script.
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

- **Version numbers:** Atlas uses [Semantic Versioning](https://semver.org). The version is set once, in `AP_Atlas_Source/AP_Atlas.csproj`.
- **Releasing:** `AP_Atlas_Source/Tools/bump_version.ps1 <version>` sets the version and opens its changelog section. Pushing a tag like `v0.1.0-beta.1` builds the release.

## Security

Please report security problems privately, as described in [SECURITY.md](SECURITY.md).
