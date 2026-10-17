using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AP_Atlas.Core.Testing;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// The Archipelago update tool (Tools/update_archipelago.py), offline: a stand-in repository pinned to Archipelago 9.9.8, and
/// a stand-in 9.9.9 release served from this PC (GitHub's API, the tag's archive zip, and a package index when the
/// requirements change). A dry run writes nothing tracked; a run pins every file once, in its own line endings, and a
/// rerun asks nothing again; a zip that isn't the tag's archive, a pre-release, a downgrade, an uncommitted file, a removed
/// world and a pin that doesn't match exactly once are refused with nothing written; --rollback puts back only what the
/// tool left; a rerun after a failed build or post-check checks again, and says it's done only after a passed one.
/// </summary>
public class UpdateArchipelagoToolTests
{
    private const string Old = "9.9.8", New = "9.9.9";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string TagObject = "fedcba9876543210fedcba9876543210fedcba98";
    private const string OldRequirements = "colorama>=0.4\n";
    private const string Engine = "AP_Atlas_Source/Scripts/Core/Engine/AtlasEngine.cs";
    private const string IndexFile = "AP_Atlas_Source/Scripts/Core/Engine/Data/archipelago_worlds.json";
    private const string LockFile = "AP_Atlas_Source/Scripts/Core/Engine/Data/engine_packages.lock.txt";
    private const string Runner = "AP_Atlas_Source/Scripts/Core/Engine/Python/atlas_run.py";
    private static readonly string[] Pinned = { Engine, IndexFile, LockFile, "CREDITS.md", "THIRD_PARTY_NOTICES.md", "CHANGELOG.md" };
    private static readonly string OldSha = new('a', 64);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_dry_run_writes_nothing_tracked_and_the_real_run_fetches_nothing_again()
    {
        await using var bench = await Bench.CreateAsync();
        var originals = bench.Snapshot();

        var (code, output) = await bench.ToolAsync(New, "--dry-run", "--no-tracker-check", "--allow-removed-worlds");
        Assert.True(code == 0, output);
        bench.AssertSame(originals);
        Assert.False(File.Exists(bench.WorkFile("state.json")));
        // The release, its tag and the zip: nothing from Universal Tracker's fork.
        Assert.Equal(3, bench.Site.Requests.Count);
        Assert.DoesNotContain(bench.Site.Requests, r => r.Contains("FarisTheAncient", StringComparison.Ordinal));
        Assert.Contains($"+    public const string ArchipelagoVersion = \"{New}\";", File.ReadAllText(bench.WorkFile("new", "pins.diff")));
        Assert.Equal(Hex(SHA256.HashData(bench.Zip)), (string?)JObject.Parse(File.ReadAllText(bench.WorkFile("release.json")))["zip"]?["sha256"]);

        // The real run takes the release, the zip and the index the dry run made.
        (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        Assert.Equal(3, bench.Site.Requests.Count);
        Assert.Contains($"public const string ArchipelagoVersion = \"{New}\";", bench.Text(Engine));
        Assert.True(File.Exists(bench.WorkFile("state.json")));
    }

    [Fact]
    public async Task A_run_pins_every_file_in_its_own_line_endings_and_a_rerun_changes_and_asks_nothing()
    {
        await using var bench = await Bench.CreateAsync();
        var originals = bench.Snapshot();

        var (code, output) = await bench.ToolAsync(New, "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        // The release, its tag, Universal Tracker's releases (only reported) and the zip.
        Assert.Equal(4, bench.Site.Requests.Count);
        Assert.Contains(bench.Site.Requests, r => r.StartsWith("/repos/FarisTheAncient/Archipelago/releases", StringComparison.Ordinal));

        string engine = bench.Text(Engine);
        Assert.Contains($"public const string ArchipelagoVersion = \"{New}\";", engine);
        Assert.Contains($"private const string ArchipelagoUrl = \"https://github.com/ArchipelagoMW/Archipelago/archive/refs/tags/{New}.zip\";", engine);
        Assert.Contains($"private const string ArchipelagoSha256 = \"{Hex(SHA256.HashData(bench.Zip))}\";", engine);
        Assert.Contains($"| Archipelago {New} | The Archipelago contributors | https://github.com/ArchipelagoMW/Archipelago/releases/tag/{New} | MIT |", bench.Text("CREDITS.md"));
        Assert.Contains($"includes Archipelago {New}'s core", bench.Text("THIRD_PARTY_NOTICES.md"));
        foreach (string file in Pinned.Where(f => f != LockFile)) AssertCrlf(bench.Text(file), file);

        // The index from the release: the new world needs the one it imports; the removed one is gone.
        var index = JObject.Parse(bench.Text(IndexFile));
        Assert.Equal(New, (string?)index["archipelago"]);
        Assert.Equal(new string?[] { "Alpha" }, Strings(index["worlds"]?["alpha"]?["games"]));
        Assert.Equal(new string?[] { "alpha" }, Strings(index["worlds"]?["beta"]?["needs"]));
        Assert.Null(index["worlds"]?["gamma"]);

        // The requirements are the same: the lock keeps its hashes and its LF; only its first line names the release.
        string[] oldLock = Encoding.UTF8.GetString(originals[LockFile]).Split('\n'), newLock = bench.Text(LockFile).Split('\n');
        Assert.Contains($"for Archipelago {New},", newLock[0]);
        Assert.Equal(oldLock.Skip(1), newLock.Skip(1));
        Assert.DoesNotContain("\r", bench.Text(LockFile));

        // One draft bullet, at the top of [Unreleased]'s Changed.
        string changelog = bench.Text("CHANGELOG.md");
        Assert.Single(Regex.Matches(changelog, @"^- \*\*The Atlas Engine runs Archipelago " + Regex.Escape(New) + @"\*\*", RegexOptions.Multiline));
        Assert.Contains($"### Changed\r\n- **The Atlas Engine runs Archipelago {New}**", changelog.Split("## [0.0.1]")[0]);

        var report = JObject.Parse(File.ReadAllText(bench.WorkFile("report.json")));
        Assert.Equal(new string?[] { "gamma" }, Strings(report["worlds"]?["worlds_removed"]));
        Assert.Equal(new string?[] { "Gamma" }, Strings(report["worlds"]?["games_removed"]));
        Assert.Equal("v0.3.5", (string?)report["tracker"]?["newest"]);
        Assert.True((bool?)report["tracker"]?["newer"]);
        Assert.Contains("gamma", File.ReadAllText(bench.WorkFile("report.md")));

        // A second run: nothing changes and nothing is asked.
        var pinned = bench.Snapshot();
        (code, output) = await bench.ToolAsync(New, "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        Assert.Equal(4, bench.Site.Requests.Count);
        bench.AssertSame(pinned);

        // The pinned files aren't committed, and that's fine while they're as the tool left them; a hand edit isn't.
        string status = await bench.GitAsync(new[] { "status", "--porcelain", "--" }.Concat(Pinned).ToArray());
        Assert.Equal(Pinned.Length, status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        File.AppendAllText(bench.PathOf("CREDITS.md"), "A hand edit.\r\n");
        (code, output) = await bench.ToolAsync(New, "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 3, output);
        Assert.Contains("CREDITS.md", output);
        foreach (string file in Pinned.Where(f => f != "CREDITS.md")) Assert.Equal(pinned[file], File.ReadAllBytes(bench.PathOf(file)));
    }

    [Fact]
    public async Task A_zip_that_changed_since_it_was_recorded_is_refused_and_both_are_kept()
    {
        await using var bench = await Bench.CreateAsync();
        var (code, output) = await bench.ToolAsync(New, "--dry-run", "--no-tracker-check", "--allow-removed-worlds");
        Assert.True(code == 0, output);
        byte[] first = bench.Zip;

        bench.Zip = Bench.Release(requirements: OldRequirements + "# GitHub made the archive again\n");
        (code, output) = await bench.ToolAsync(New, "--dry-run", "--no-tracker-check", "--allow-removed-worlds", "--fresh");
        Assert.True(code == 5, output);
        Assert.Equal(first, File.ReadAllBytes(bench.WorkFile($"Archipelago-{New}.zip")));
        Assert.Equal(bench.Zip, File.ReadAllBytes(Assert.Single(Directory.GetFiles(bench.Work, $"Archipelago-{New}-*.zip"))));
    }

    [Theory]
    [InlineData("another top folder")]
    [InlineData("another commit")]
    [InlineData("an entry outside its folder")]
    [InlineData("a loose apworld")]
    public async Task A_zip_that_isnt_the_tags_archive_is_refused(string flaw)
    {
        await using var bench = await Bench.CreateAsync();
        var originals = bench.Snapshot();
        bench.Zip = flaw switch
        {
            "another top folder" => Bench.Release(root: $"Archipelago-{Old}/"),
            // The tag is annotated: the zip names the tag's own object, not the commit it leads to.
            "another commit" => Bench.Release(comment: TagObject),
            "an entry outside its folder" => Bench.Release(extra: new[] { ("worlds/../../outside.py", "print('outside')\n") }),
            _ => Bench.Release(extra: new[] { ("worlds/gamma.apworld", "an apworld Archipelago would load instead of a folder") }),
        };
        bench.Annotated = flaw == "another commit";

        var (code, output) = await bench.ToolAsync(New, "--dry-run", "--no-tracker-check", "--allow-removed-worlds");
        Assert.True(code == 5, output);
        Assert.Equal(bench.Annotated ? 4 : 3, bench.Site.Requests.Count);
        Assert.Equal(JTokenType.Null, JObject.Parse(File.ReadAllText(bench.WorkFile("release.json")))["zip"]?.Type);
        Assert.False(File.Exists(bench.WorkFile($"Archipelago-{New}.zip")));
        bench.AssertSame(originals);

        // A rerun checks the refused download again and refuses again, asking nothing (--fresh would fetch it again).
        int asked = bench.Site.Requests.Count;
        (code, output) = await bench.ToolAsync(New, "--dry-run", "--no-tracker-check", "--allow-removed-worlds");
        Assert.True(code == 5, output);
        Assert.Equal(asked, bench.Site.Requests.Count);
        Assert.Contains("--fresh", output);
    }

    [Fact]
    public async Task A_pre_release_is_refused_before_anything_is_downloaded()
    {
        await using var bench = await Bench.CreateAsync();
        bench.Prerelease = true;
        var (code, output) = await bench.ToolAsync(New, "--dry-run", "--no-tracker-check");
        Assert.True(code == 4, output);
        Assert.Single(bench.Site.Requests);
        Assert.False(File.Exists(bench.WorkFile("release.json")));
    }

    [Fact]
    public async Task A_downgrade_an_uncommitted_file_and_a_site_off_this_pc_are_refused_before_anything_is_asked()
    {
        await using var bench = await Bench.CreateAsync();
        var originals = bench.Snapshot();

        var (code, output) = await bench.ToolAsync("9.9.7", "--dry-run");
        Assert.True(code == 3, output);
        (code, output) = await bench.ToolAsync(Old, "--dry-run");
        Assert.True(code == 3, output);
        (code, output) = await bench.ToolAsync("9.9", "--dry-run");
        Assert.True(code == 2, output);

        File.AppendAllText(bench.PathOf("CREDITS.md"), "An uncommitted line.\r\n");
        (code, output) = await bench.ToolAsync(New, "--dry-run");
        Assert.True(code == 3, output);
        Assert.Contains("CREDITS.md", output);
        File.WriteAllBytes(bench.PathOf("CREDITS.md"), originals["CREDITS.md"]);

        (code, output) = await bench.ToolAsync(New, "--dry-run", "--api-base", "https://api.example.com");
        Assert.True(code == 2, output);
        (code, output) = await bench.ToolAsync(New, "--dry-run", "--download-base", "http://127.0.0.2:8080");
        Assert.True(code == 2, output);
        Assert.Empty(bench.Site.Requests);
        bench.AssertSame(originals);
    }

    [Fact]
    public async Task A_world_the_release_removes_needs_the_flag_and_nothing_is_written_without_it()
    {
        await using var bench = await Bench.CreateAsync();
        var originals = bench.Snapshot();
        // A dry run shows it all the same: the removal, the report's worlds and the diff, with the flag a real run needs.
        var (code, output) = await bench.ToolAsync(New, "--dry-run", "--no-tracker-check");
        Assert.True(code == 0, output);
        Assert.Contains("--allow-removed-worlds", output);
        Assert.Equal(new string?[] { "gamma" }, Strings(JObject.Parse(File.ReadAllText(bench.WorkFile("report.json")))["worlds"]?["worlds_removed"]));
        Assert.True(File.Exists(bench.WorkFile("new", "pins.diff")));
        bench.AssertSame(originals);

        (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--skip-build");
        Assert.True(code == 7, output);
        Assert.Contains("gamma", output);
        bench.AssertSame(originals);
        Assert.Empty((await bench.GitAsync(new[] { "status", "--porcelain", "--" }.Concat(Pinned).ToArray())).Trim());
    }

    [Fact]
    public async Task Changed_requirements_build_a_new_lock_from_the_package_index_once_the_credits_name_its_packages()
    {
        await using var bench = await Bench.CreateAsync();
        const string requirements = "fakepkg>=1.0\n";
        bench.Zip = Bench.Release(requirements: requirements);
        var packages = new[] { ("fakepkg", "1.0"), ("requests", "1.0"), ("setuptools", "80.0.0") };
        foreach (var (name, version) in packages) bench.ServePackage(name, version);
        var originals = bench.Snapshot();
        // Settings no child may see: one that would point the engine build at another core, and a secret.
        bench.Variables["ATLAS_ENGINE_CORE"] = Path.Combine(bench.Root, "Builds", $"engine-core-{Old}");
        bench.Variables["SENTRY_AUTH_TOKEN"] = "not-a-token";
        // Git Bash's own temp folder, which Python's tempfile would take before TEMP and TMP.
        bench.Variables["TMPDIR"] = Path.Combine(bench.Root, "not-the-work-folder");

        // Every package the new lock pins must be credited first (by hand: who made it, its license).
        var (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 6, output);
        foreach (var (name, version) in packages)
        {
            Assert.Contains($"| {name} {version} | in CREDITS.md", output);
            Assert.Contains($"| {name} {version} | in THIRD_PARTY_NOTICES.md", output);
        }
        bench.AssertSame(originals);
        // pip ran with no settings of its own and its cache in the work folder.
        var isolation = JObject.Parse(File.ReadAllText(bench.WorkFile("report.json")))["isolation"];
        Assert.Equal(OperatingSystem.IsWindows() ? "nul" : "/dev/null", (string?)isolation?["PIP_CONFIG_FILE"]);
        Assert.StartsWith(bench.Work, (string?)isolation?["PIP_CACHE_DIR"]);
        Assert.StartsWith(bench.Work, (string?)isolation?["TEMP"]);
        Assert.StartsWith(bench.Work, (string?)isolation?["TMPDIR"]);
        string?[] removed = Strings(isolation?["removed"]);
        Assert.Contains("ATLAS_ENGINE_CORE", removed);
        Assert.Contains("SENTRY_AUTH_TOKEN", removed);
        Assert.DoesNotContain("not-a-token", File.ReadAllText(bench.WorkFile("report.json")));

        File.AppendAllText(bench.PathOf("CREDITS.md"), string.Concat(packages.Select(p => $"| {p.Item1} {p.Item2} | Atlas tests | https://example.invalid | MIT |\r\n")));
        File.AppendAllText(bench.PathOf("THIRD_PARTY_NOTICES.md"), string.Concat(packages.Select(p => $"| {p.Item1} {p.Item2} | MIT |\r\n")));
        await bench.CommitAsync("Credit the new packages");
        int asked = bench.Site.Requests.Count;
        (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        Assert.Equal(asked, bench.Site.Requests.Count); // the lock built the first time is used

        string lockText = bench.Text(LockFile);
        Assert.StartsWith($"# The Atlas Engine's Python packages for Archipelago {New},", lockText);
        Assert.Contains($"# requirements-signature: {Signature(Filtered(requirements))}\n", lockText);
        foreach (var (name, version) in packages) Assert.Contains($"{name}=={version} \\\n    --hash=sha256:{bench.WheelHashes[name]}\n", lockText);
        Assert.DoesNotContain("colorama", lockText);
        Assert.DoesNotContain("\r", lockText);
    }

    [Fact]
    public async Task An_interrupted_run_resumes_without_fetching_again()
    {
        await using var bench = await Bench.CreateAsync();
        var originals = bench.Snapshot();
        var (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        var pinned = bench.Snapshot();

        // As if it had stopped after the index: the files as they were, the state without the pins.
        bench.Restore(originals);
        var state = JObject.Parse(File.ReadAllText(bench.WorkFile("state.json")));
        Assert.NotNull(state["phases"]?["index"]);
        ((JObject)state["phases"]!).Remove("pins");
        state["written"] = new JObject();
        state["backups"] = new JObject();
        File.WriteAllText(bench.WorkFile("state.json"), state.ToString());

        (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        Assert.Equal(3, bench.Site.Requests.Count);
        bench.AssertSame(pinned);
    }

    [Fact]
    public async Task A_rerun_after_a_failed_build_or_post_check_checks_again_and_the_done_check_keeps_the_report()
    {
        await using var bench = await Bench.CreateAsync();
        var (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        string core = Path.Combine(bench.Root, "Builds", "engine-core"), stamp = Path.Combine(core, "core.json");
        // Any file stands in for Godot: the build itself never runs here (its record says it's done, or --skip-build).
        string[] build = { New, "--no-tracker-check", "--allow-removed-worlds", "--godot", bench.PathOf(Engine) };

        // As build_engine.ps1 leaves it when its shipped-core check fails: engine-core stamped with the new release, the
        // build started and never recorded as done. Every pin and the stamp say 9.9.9, and the run isn't done.
        bench.Core("engine-core", New);
        bench.SetBuild(null);
        (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        Assert.DoesNotContain("Already at", output);
        Assert.Contains("not ready to ship", output);

        // The build done and its post-check failed (the stamp names no Universal Tracker): a rerun checks again and fails again.
        bench.SetBuild(stamp);
        (code, output) = await bench.ToolAsync(build);
        Assert.True(code == 9, output);
        Assert.DoesNotContain("Already at", output);
        Assert.Contains("failed its check", output);

        // A core that passes: its snapshot is taken, and only then does a rerun say the update is done.
        File.WriteAllText(stamp, new JObject { ["Archipelago"] = New, ["Tracker"] = "v0.3.4", ["PackagesSignature"] = Signature(Filtered(OldRequirements)) }.ToString());
        Directory.CreateDirectory(Path.Combine(core, "archipelago"));
        File.WriteAllText(Path.Combine(core, "archipelago", "Utils.py"), $"__version__ = \"{New}\"\n");
        bench.SetBuild(stamp);
        (code, output) = await bench.ToolAsync(build);
        Assert.True(code == 0, output);
        Assert.Contains("built and checked", output);
        string report = File.ReadAllText(bench.WorkFile("report.md")), reportJson = File.ReadAllText(bench.WorkFile("report.json"));
        Assert.Contains("## Pins", report);
        Assert.Contains("## Post-check", report);

        // The acceptance's last step: it says so, and the report of the run that did the work stays as it was.
        (code, output) = await bench.ToolAsync(build);
        Assert.True(code == 0, output);
        Assert.Contains($"Already at {New}", output);
        Assert.Contains("matches its snapshot", output);
        Assert.Equal(report, File.ReadAllText(bench.WorkFile("report.md")));
        Assert.Equal(reportJson, File.ReadAllText(bench.WorkFile("report.json")));

        // A file written into the core since (a lab run's region diagram) fails the check; the report still stays.
        File.WriteAllText(Path.Combine(core, "archipelago", "Player1.puml"), "@startuml\n");
        (code, output) = await bench.ToolAsync(build);
        Assert.True(code == 9, output);
        Assert.Contains("changed since its post-check", output);
        Assert.Equal(report, File.ReadAllText(bench.WorkFile("report.md")));
    }

    [Fact]
    public async Task Rollback_puts_the_files_and_the_core_back_only_while_the_files_are_as_the_tool_left_them()
    {
        await using var bench = await Bench.CreateAsync();
        var originals = bench.Snapshot();
        var (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        var pinned = bench.Snapshot();
        // A core built for the new release, the old one set aside.
        bench.Core("engine-core", New);
        bench.Core($"engine-core-{Old}", Old);

        File.AppendAllText(bench.PathOf("CREDITS.md"), "A hand edit.\r\n");
        (code, output) = await bench.ToolAsync(New, "--rollback");
        Assert.True(code == 3, output);
        Assert.Contains("CREDITS.md", output);
        Assert.Equal(pinned[Engine], File.ReadAllBytes(bench.PathOf(Engine)));
        Assert.Equal(New, bench.CoreVersion("engine-core"));

        File.WriteAllBytes(bench.PathOf("CREDITS.md"), pinned["CREDITS.md"]);
        (code, output) = await bench.ToolAsync(New, "--rollback");
        Assert.True(code == 0, output);
        bench.AssertSame(originals);
        Assert.Empty((await bench.GitAsync(new[] { "status", "--porcelain", "--" }.Concat(Pinned).ToArray())).Trim());
        Assert.Equal(Old, bench.CoreVersion("engine-core"));
        Assert.Equal(New, bench.CoreVersion($"engine-core-{New}-failed"));
        // Nothing is deleted: the run's state and backups stay, renamed.
        Assert.False(File.Exists(bench.WorkFile("state.json")));
        Assert.True(File.Exists(bench.WorkFile("state.rolled-back-1.json")));
        Assert.True(Directory.Exists(bench.WorkFile("backup.rolled-back-1")));
    }

    [Fact]
    public async Task Rollback_moves_a_half_built_core_aside_even_without_its_stamp()
    {
        await using var bench = await Bench.CreateAsync();
        var (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 0, output);
        // As build_engine.ps1 leaves it when stopped partway: engine-core replaced, its core.json not copied yet.
        bench.Core($"engine-core-{Old}", Old);
        string half = Directory.CreateDirectory(Path.Combine(bench.Root, "Builds", "engine-core", "python")).FullName;
        File.WriteAllText(Path.Combine(half, "python.exe"), "half of a core");

        (code, output) = await bench.ToolAsync(New, "--rollback");
        Assert.True(code == 0, output);
        Assert.Equal(Old, bench.CoreVersion("engine-core"));
        Assert.True(File.Exists(Path.Combine(bench.Root, "Builds", $"engine-core-{New}-failed", "python", "python.exe")));
    }

    [Fact]
    public async Task A_batch_running_beside_it_stops_the_run_before_anything_is_asked()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The busy check reads Windows' process list.");
        await using var bench = await Bench.CreateAsync();
        bench.CheckBusy = true;
        var originals = bench.Snapshot();
        // Something whose command line names a lab batch, started beside the tool (not by it).
        string? python = await TestEnvironment.PythonAsync(Ct);
        var info = new ProcessStartInfo(python!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (string arg in new[] { "-I", "-c", "import sys; sys.stdin.read()", "batch_logic.py" }) info.ArgumentList.Add(arg);
        using var batch = Process.Start(info)!;
        try
        {
            var (code, output) = await bench.ToolAsync(New, "--dry-run", "--no-tracker-check", "--allow-removed-worlds");
            Assert.True(code == 3, output);
            Assert.Contains("Run this alone", output);
            Assert.Contains("batch_logic.py", output);
            Assert.Empty(bench.Site.Requests);
            bench.AssertSame(originals);
        }
        finally
        {
            batch.StandardInput.Close(); // it reads to the end, then exits
            if (!batch.WaitForExit(10_000)) batch.Kill();
        }
    }

    [Fact]
    public async Task A_pin_that_doesnt_match_exactly_once_stops_the_run_with_nothing_written()
    {
        await using var bench = await Bench.CreateAsync();
        string engine = bench.Text(Engine);
        File.WriteAllText(bench.PathOf(Engine), engine.Replace("    public const string ArchipelagoVersion",
            $"    public const string ArchipelagoVersion = \"{Old}\";\r\n    public const string ArchipelagoVersion"));
        await bench.CommitAsync("A pin stated twice");
        var twice = bench.Snapshot();
        var (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 10, output);
        Assert.Contains("ArchipelagoVersion", output);
        bench.AssertSame(twice);

        File.WriteAllText(bench.PathOf(Engine), engine);
        File.WriteAllText(bench.PathOf("CHANGELOG.md"), bench.Text("CHANGELOG.md").Replace("### Changed\r\n- **Something changed.**", "### Fixed\r\n- **Something changed.**"));
        await bench.CommitAsync("No Changed heading under Unreleased");
        var missing = bench.Snapshot();
        (code, output) = await bench.ToolAsync(New, "--no-tracker-check", "--allow-removed-worlds", "--skip-build");
        Assert.True(code == 10, output);
        Assert.Contains("CHANGELOG.md", output);
        bench.AssertSame(missing);
    }

    [Fact]
    public async Task Latest_says_whether_a_newer_release_is_out_with_one_request()
    {
        await using var bench = await Bench.CreateAsync();
        var (code, output) = await bench.ToolAsync("--latest");
        Assert.True(code == 20, output);
        Assert.Equal("/repos/ArchipelagoMW/Archipelago/releases/latest", Assert.Single(bench.Site.Requests));

        bench.Latest = Old;
        (code, output) = await bench.ToolAsync("--latest");
        Assert.True(code == 0, output);
    }

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>The requirements as build_engine_lock.py filters them (these have nothing it leaves out).</summary>
    private static string Filtered(string requirements) => requirements + "requests\nsetuptools<81\n";

    /// <summary>AtlasEngine.PackagesSignature: the first 16 hex digits of the text's SHA-256.</summary>
    private static string Signature(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static string?[] Strings(JToken? list) => list?.Select(t => (string?)t).ToArray() ?? Array.Empty<string?>();

    private static string Crlf(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private static void AssertCrlf(string text, string file)
    {
        int bare = Regex.Matches(text, "(?<!\r)\n").Count;
        Assert.True(bare == 0, $"{file} has {bare} line endings that aren't CRLF");
    }

    /// <summary>A program run to its end (5 minutes at most): its exit code and output, or -1 when it isn't installed.</summary>
    private static Task<(int Code, string Output, string Errors)> ExecAsync(string file, string? folder, params string[] args) => ExecWithAsync(file, folder, null, args);

    /// <summary><see cref="ExecAsync"/> with these variables set for the program (the rest as this process has them).</summary>
    private static async Task<(int Code, string Output, string Errors)> ExecWithAsync(string file, string? folder, IReadOnlyDictionary<string, string>? variables, string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (folder != null) info.WorkingDirectory = folder;
        foreach (string arg in args) info.ArgumentList.Add(arg);
        foreach (var (name, value) in variables ?? new Dictionary<string, string>()) info.Environment[name] = value;
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(Ct);
            var errors = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(5), Ct);
            return (process.ExitCode, await output, await errors);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "", file + " isn't installed here");
        }
    }

    /// <summary>The stand-in repository (committed in its own git), the site that serves the release, and the tool run on them.</summary>
    private sealed class Bench : IAsyncDisposable
    {
        private readonly TempFolder _dir = new();
        private readonly string _python;

        private Bench(string python)
        {
            _python = python;
            Root = Path.Combine(_dir.Path, "repo");
            Site.Answer = (path, _) => Answer(path);
        }

        public FakeWebSite Site { get; } = new();
        public string Root { get; }
        public string Work => Path.Combine(Root, "Builds", "ap-update", New);
        public byte[] Zip { get; set; } = Release();
        public bool Prerelease { get; set; }
        public bool Annotated { get; set; }
        public string Latest { get; set; } = New;
        public Dictionary<string, string> WheelHashes { get; } = new();
        private Dictionary<string, FakeWebSite.FullAnswer> Packages { get; } = new();

        public static async Task<Bench> CreateAsync()
        {
            string? python = await TestEnvironment.PythonAsync(Ct);
            if (python == null) Assert.Skip("Python isn't installed here (CI runs this test).");
            if ((await ExecAsync("git", null, "--version")).Code != 0) Assert.Skip("git isn't installed here (CI runs this test).");
            var (_, version, _) = await ExecAsync(python!, null, "-I", "-c", "import platform; print(platform.python_version())");
            var bench = new Bench(python!);
            try
            {
                bench.WriteRepo(version.Trim());
                await bench.GitAsync("init", "-q");
                await bench.GitAsync("config", "core.autocrlf", "false");
                await bench.CommitAsync($"A stand-in for Atlas, pinned to Archipelago {Old}");
                return bench;
            }
            catch
            {
                await bench.DisposeAsync();
                throw;
            }
        }

        public string PathOf(string file) => Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar));

        public string WorkFile(params string[] parts) => Path.Combine(new[] { Work }.Concat(parts).ToArray());

        public string Text(string file) => File.ReadAllText(PathOf(file));

        public Dictionary<string, byte[]> Snapshot() => Pinned.ToDictionary(f => f, f => File.ReadAllBytes(PathOf(f)));

        public void AssertSame(Dictionary<string, byte[]> snapshot)
        {
            foreach (var (file, bytes) in snapshot) Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(PathOf(file))), file + " changed");
        }

        public void Restore(Dictionary<string, byte[]> snapshot)
        {
            foreach (var (file, bytes) in snapshot) File.WriteAllBytes(PathOf(file), bytes);
        }

        /// <summary>A stand-in engine core in Builds: only its stamp.</summary>
        public void Core(string folder, string archipelago)
        {
            string core = Directory.CreateDirectory(Path.Combine(Root, "Builds", folder)).FullName;
            File.WriteAllText(Path.Combine(core, "core.json"), new JObject { ["Archipelago"] = archipelago }.ToString());
        }

        /// <summary>The run's state as the build left it: started, and done with this stamp's SHA-256 (null: never done).</summary>
        public void SetBuild(string? stamp)
        {
            var state = JObject.Parse(File.ReadAllText(WorkFile("state.json")));
            var record = new JObject { ["started"] = "2026-10-17T00:00:00+00:00", ["old_core"] = Path.Combine(Root, "Builds", $"engine-core-{Old}") };
            if (stamp != null) record["core_json_sha256"] = Hex(SHA256.HashData(File.ReadAllBytes(stamp)));
            state["phases"]!["build"] = record;
            File.WriteAllText(WorkFile("state.json"), state.ToString());
        }

        public string? CoreVersion(string folder) => (string?)JObject.Parse(File.ReadAllText(Path.Combine(Root, "Builds", folder, "core.json")))["Archipelago"];

        /// <summary>
        /// Variables the tool runs with. ATLAS_UPDATE_TEST_SKIP_BUSY=1 unless <see cref="CheckBusy"/>: what else runs on this PC
        /// (a lab batch, another session) doesn't decide these tests; one test checks the busy check by itself.
        /// </summary>
        public Dictionary<string, string> Variables { get; } = new();

        public bool CheckBusy { get; set; }

        /// <summary>update_archipelago.py on this repository, with this PC's site standing in for GitHub and PyPI (later arguments win).</summary>
        public async Task<(int Code, string Output)> ToolAsync(params string[] args)
        {
            string tool = TestEnvironment.RepoFile("AP_Atlas_Source", "Tools", "update_archipelago.py");
            // -B: nothing compiled in the repository's Tools folder (python -I ignores PYTHONDONTWRITEBYTECODE).
            var all = new[] { "-I", "-B", "-X", "utf8", tool, "--repo", Root, "--engine-python", _python, "--api-base", Site.Site, "--download-base", Site.Site, "--pypi-base", Site.Site };
            var variables = new Dictionary<string, string>(Variables);
            if (!CheckBusy) variables["ATLAS_UPDATE_TEST_SKIP_BUSY"] = "1";
            var (code, output, errors) = await ExecWithAsync(_python, Root, variables, all.Concat(args).ToArray());
            return (code, output + errors);
        }

        public async Task<string> GitAsync(params string[] args)
        {
            var (code, output, errors) = await ExecAsync("git", Root, new[] { "-c", "user.name=Atlas tests", "-c", "user.email=tests@atlas.invalid", "-c", "commit.gpgsign=false" }.Concat(args).ToArray());
            Assert.True(code == 0, $"git {string.Join(' ', args)}: {errors}");
            return output;
        }

        public async Task CommitAsync(string message)
        {
            await GitAsync(new[] { "add", "--" }.Concat(Pinned).Append(Runner).ToArray());
            await GitAsync("commit", "-q", "-m", message);
        }

        /// <summary>A tiny wheel on the package index: its page, the file, and PyPI's JSON with its hash.</summary>
        public void ServePackage(string name, string version)
        {
            byte[] wheel = Wheel(name, version);
            string file = $"{name}-{version}-py3-none-any.whl", hash = Hex(SHA256.HashData(wheel));
            WheelHashes[name] = hash;
            Packages[$"/files/{file}"] = new FakeWebSite.FullAnswer(200, wheel, "application/octet-stream");
            Packages[$"/simple/{name}/"] = new FakeWebSite.FullAnswer(200, Encoding.UTF8.GetBytes($"<!DOCTYPE html><html><body><a href=\"/files/{file}#sha256={hash}\">{file}</a></body></html>"), "text/html");
            Packages[$"/pypi/{name}/{version}/json"] = Json(new JObject { ["urls"] = new JArray(new JObject { ["digests"] = new JObject { ["sha256"] = hash } }) });
        }

        private FakeWebSite.FullAnswer Answer(string path)
        {
            const string api = "/repos/ArchipelagoMW/Archipelago";
            if (Packages.TryGetValue(path, out var package)) return package;
            return path switch
            {
                $"{api}/releases/tags/{New}" => Json(new JObject
                {
                    ["tag_name"] = New,
                    ["draft"] = false,
                    ["prerelease"] = Prerelease,
                    ["published_at"] = "2026-10-01T12:00:00Z",
                    ["html_url"] = $"https://github.com/ArchipelagoMW/Archipelago/releases/tag/{New}",
                    ["body"] = "Gamma left Archipelago. ```Notes``` are data."
                }),
                $"{api}/git/ref/tags/{New}" => Json(new JObject { ["object"] = new JObject { ["type"] = Annotated ? "tag" : "commit", ["sha"] = Annotated ? TagObject : Commit } }),
                $"{api}/git/tags/{TagObject}" => Json(new JObject { ["object"] = new JObject { ["type"] = "commit", ["sha"] = Commit } }),
                $"/ArchipelagoMW/Archipelago/archive/refs/tags/{New}.zip" => new FakeWebSite.FullAnswer(200, Zip, "application/zip"),
                $"{api}/releases/latest" => Json(new JObject { ["tag_name"] = Latest }),
                "/repos/FarisTheAncient/Archipelago/releases?per_page=10" => Json(new JArray(
                    new JObject { ["tag_name"] = "Tracker_v0.4.0", ["draft"] = false, ["prerelease"] = true },
                    new JObject { ["tag_name"] = "Tracker_v0.3.5", ["draft"] = false, ["prerelease"] = false },
                    new JObject { ["tag_name"] = "Tracker_v0.3.4", ["draft"] = false, ["prerelease"] = false })),
                _ => new FakeWebSite.FullAnswer(404, Array.Empty<byte>())
            };
        }

        private static FakeWebSite.FullAnswer Json(JToken value) => new(200, Encoding.UTF8.GetBytes(value.ToString()));

        /// <summary>
        /// A stand-in release zip, as GitHub makes a tag's archive: one top folder, the commit as the zip's comment, and what the
        /// tool and make_worlds_index.py read (Utils.py, ModuleUpdate.py's Python range, requirements.txt, worlds that register
        /// their games, one importing another).
        /// </summary>
        public static byte[] Release(string root = $"Archipelago-{New}/", string comment = Commit, string requirements = OldRequirements, (string Name, string Text)[]? extra = null)
        {
            var files = new List<(string Name, string Text)>
            {
                ("Utils.py", $"__version__ = \"{New}\"\n"),
                ("ModuleUpdate.py", "import sys\n\nupdate_ran = False\nif sys.platform in (\"win32\", \"darwin\") and not (3, 8, 0) <= sys.version_info < (3, 99, 0):\n    raise RuntimeError(\"Incompatible Python Version found.\")\n"),
                ("requirements.txt", requirements),
                ("worlds/__init__.py", """
                    # Stands in for Archipelago's worlds: every folder here and in the user folder (ATLAS_ENGINE_DATA\ap\worlds) loads.
                    import importlib
                    import os

                    failed_world_loads = {}
                    for _folder in (os.path.dirname(__file__), os.path.join(os.environ.get('ATLAS_ENGINE_DATA', ''), 'ap', 'worlds')):
                        if not os.path.isdir(_folder):
                            continue
                        for _name in sorted(os.listdir(_folder)):
                            if _name.startswith(('_', '.')) or not os.path.isfile(os.path.join(_folder, _name, '__init__.py')):
                                continue
                            try:
                                importlib.import_module('worlds.' + _name)
                            except Exception as e:
                                failed_world_loads[_name] = str(e)

                    """),
                ("worlds/AutoWorld.py", """
                    class AutoWorldRegister(type):
                        world_types = {}

                        def __new__(mcs, name, bases, namespace):
                            cls = super().__new__(mcs, name, bases, namespace)
                            if 'game' in namespace:
                                AutoWorldRegister.world_types[namespace['game']] = cls
                            return cls


                    class World(metaclass=AutoWorldRegister):
                        pass

                    """),
                ("worlds/_lib/__init__.py", "# A library the worlds share: not a world.\n"),
                World("generic", "Archipelago"),
                World("checksfinder", "ChecksFinder"),
                World("alttp", "A Link to the Past"),
                World("bumpstik", "Bumper Stickers", needs: "alttp"),
                World("alpha", "Alpha"),
                World("beta", "Beta", needs: "alpha"),
            };
            files.AddRange(extra ?? Array.Empty<(string, string)>());
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                // The folders first, the top one first of all, as GitHub lists them.
                var folders = new SortedSet<string>(StringComparer.Ordinal) { "" };
                foreach (var (name, _) in files)
                {
                    for (int slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1)) folders.Add(name[..(slash + 1)]);
                }
                foreach (string folder in folders) zip.CreateEntry(root + folder);
                foreach (var (name, text) in files)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(root + name).Open(), new UTF8Encoding(false));
                    writer.Write(text);
                }
            }
            // The comment is the zip's last field: its length ends the end record (22 bytes, written with none), the text follows.
            byte[] bytes = stream.ToArray(), note = Encoding.ASCII.GetBytes(comment);
            bytes[^2] = (byte)note.Length;
            bytes[^1] = (byte)(note.Length >> 8);
            return bytes.Concat(note).ToArray();
        }

        /// <summary>A world folder that registers its game (and imports another world first, when it needs one).</summary>
        private static (string Name, string Text) World(string folder, string game, string? needs = null)
        {
            string imports = needs == null ? "" : $"from worlds.{needs} import World as _Needed  # noqa: F401\n";
            return ($"worlds/{folder}/__init__.py", imports + $"from worlds.AutoWorld import World\n\n\nclass Stand(World):\n    game = '{game}'\n");
        }

        /// <summary>A wheel with nothing in it but what pip reads: its metadata.</summary>
        private static byte[] Wheel(string name, string version)
        {
            string info = $"{name}-{version}.dist-info/";
            var files = new[]
            {
                ("METADATA", $"Metadata-Version: 2.1\nName: {name}\nVersion: {version}\n"),
                ("WHEEL", "Wheel-Version: 1.0\nGenerator: atlas-tests\nRoot-Is-Purelib: true\nTag: py3-none-any\n"),
                ("RECORD", $"{info}METADATA,,\n{info}WHEEL,,\n{info}RECORD,,\n"),
            };
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (file, text) in files)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(info + file).Open(), new UTF8Encoding(false));
                    writer.Write(text);
                }
            }
            return stream.ToArray();
        }

        /// <summary>Atlas's pinned files as a repository has them, for Archipelago 9.9.8 (the lock in LF, the rest in CRLF).</summary>
        private void WriteRepo(string pythonVersion)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(IndexFile))!);
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(Runner))!);
            File.Copy(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "Python", "atlas_run.py"), PathOf(Runner));
            File.WriteAllText(PathOf(Engine), Crlf(
                "// Stands in for AtlasEngine.cs: the lines Tools/update_archipelago.py reads and pins.",
                "public static class AtlasEngine",
                "{",
                $"    public const string PythonVersion = \"{pythonVersion}\";",
                $"    public const string ArchipelagoVersion = \"{Old}\";",
                $"    private const string ArchipelagoUrl = \"https://github.com/ArchipelagoMW/Archipelago/archive/refs/tags/{Old}.zip\";",
                $"    private const string ArchipelagoSha256 = \"{OldSha}\";",
                "    public const string TrackerVersion = \"v0.3.4\";",
                $"    private const string TrackerSha256 = \"{new string('c', 64)}\";",
                "}"));
            static JObject W(string game, params string[] needs) => new() { ["bytes"] = 100, ["games"] = new JArray(game), ["needs"] = new JArray(needs), ["requirements"] = false };
            var index = new JObject
            {
                ["archipelago"] = Old,
                ["core_worlds"] = new JArray("checksfinder", "generic", "_lib"),
                ["worlds"] = new JObject { ["alpha"] = W("Alpha"), ["alttp"] = W("A Link to the Past"), ["bumpstik"] = W("Bumper Stickers", "alttp"), ["gamma"] = W("Gamma") },
                ["zip_bytes"] = 1000,
                ["zip_root"] = $"Archipelago-{Old}/",
                ["zip_sha256"] = OldSha,
            };
            File.WriteAllText(PathOf(IndexFile), Crlf(index.ToString().Replace("\r\n", "\n").Split('\n')));
            File.WriteAllText(PathOf(LockFile), string.Join("\n",
                $"# The Atlas Engine's Python packages for Archipelago {Old}, Python {pythonVersion} on Windows (64-bit wheels only):",
                "# exact versions, and the SHA-256 of every file PyPI publishes for them. Atlas installs these with --require-hashes,",
                "# so nothing else can be installed. Built by Tools/build_engine_lock.py on 2026-10-05; rebuild when",
                "# AtlasEngine.ArchipelagoVersion changes.",
                $"# requirements-signature: {Signature(Filtered(OldRequirements))}",
                "",
                "colorama==0.4.6 \\",
                $"    --hash=sha256:{new string('b', 64)}") + "\n");
            File.WriteAllText(PathOf("CREDITS.md"), Crlf(
                "# Credits",
                "",
                "| Component | Made by | Official releases | License |",
                "|---|---|---|---|",
                $"| Archipelago {Old} | The Archipelago contributors | https://github.com/ArchipelagoMW/Archipelago/releases/tag/{Old} | MIT |",
                "",
                "| Package | Made by | Project | License |",
                "|---|---|---|---|",
                "| colorama 0.4.6 | Jonathan Hartley | https://github.com/tartley/colorama | BSD |"));
            File.WriteAllText(PathOf("THIRD_PARTY_NOTICES.md"), Crlf(
                "# Third-party notices",
                "",
                $"The Atlas Engine that ships with Atlas includes Archipelago {Old}'s core.",
                "",
                "| colorama 0.4.6 | BSD |"));
            File.WriteAllText(PathOf("CHANGELOG.md"), Crlf(
                "# Changelog",
                "",
                "## [Unreleased]",
                "",
                "### Added",
                "- **Something new.**",
                "",
                "### Changed",
                "- **Something changed.**",
                "",
                "## [0.0.1]",
                "",
                "### Changed",
                "- **Something older.**"));
        }

        public async ValueTask DisposeAsync()
        {
            await Site.DisposeAsync();
            // git keeps its objects read-only, which would stop the folder's deletion.
            try
            {
                foreach (string file in Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (IOException) { } // a file still open: the system temp folder is cleaned up eventually
            catch (UnauthorizedAccessException) { }
            _dir.Dispose();
        }
    }
}
