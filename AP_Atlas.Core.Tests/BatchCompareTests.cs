using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// The lab's batch comparison (Tools/compare_batches.py) on small made-up batches: every class, a batch run in parts merged
/// into the file a control run takes its seeds from, a removal the update expects, the control's verdict, the names diff,
/// and a traceback through the bridge as the lab loads it (UltimateBridge.apworld) found and blocking, also inside a seed
/// test's answer. And what a batch lends a control run (Tools/batch_logic.py): the YAML and seed that failed to generate.
/// </summary>
public class BatchCompareTests
{
    [Fact]
    public async Task Each_game_is_classed_the_parts_merge_and_a_regression_through_the_bridge_blocks()
    {
        var ct = TestContext.Current.CancellationToken;
        string? python = await TestEnvironment.PythonAsync(ct);
        if (python == null)
        {
            Assert.Skip("Python isn't installed here (CI runs this test).");
            return;
        }
        using var dir = new TempFolder();
        string bridgeTrace = """
            Traceback (most recent call last):
              File "C:\lab\ap\worlds\UltimateBridge.apworld\UltimateBridge\__init__.py", line 1890, in atlas_generate
            KeyError: 'slot'
            """;
        // A seed test whose rebuild failed under the bridge's hooks: its score_error is the start's answer, the traceback inside.
        var startAnswer = new JObject
        {
            ["message"] = "The world couldn't be rebuilt.",
            ["attempts"] = new JArray(new JObject
            {
                ["error"] = """
                    Traceback (most recent call last):
                      File "C:\lab\ap\worlds\tracker.apworld\tracker\TrackerCore.py", line 377, in run_generator
                      File "C:\lab\ap\worlds\UltimateBridge.apworld\UltimateBridge\__init__.py", line 640, in fixed_generation_seed
                    TypeError: an integer is required
                    """
            })
        };
        var rebuildsWrong = Done(false);
        rebuildsWrong["score_error"] = startAnswer;
        // A world's own failure comes up through the bridge's entry frame too, as every component's does: not Atlas's code.
        string worldTrace = """
            Traceback (most recent call last):
              File "C:\lab\ap\worlds\UltimateBridge.apworld\UltimateBridge\__init__.py", line 1290, in atlas_seed_test
              File "C:\lab\ap\worlds\crashes.apworld\crashes\Rules.py", line 5, in set_rules
            ValueError: no such region
            """;
        Write(dir.File("old.json"), new JObject
        {
            ["Steady"] = Done(true),
            ["Slipped"] = Done(true),
            ["Broken"] = Done(true),
            ["Crashes"] = Done(false),
            ["Fixed"] = Done(false),
            ["Generates Now"] = Failed("generate", "Exception: no"),
            ["Wobbly"] = Done(false, late: 2),
            ["Old Game"] = Done(true),
            ["Gone"] = Done(true),
            ["Rebuilds Wrong"] = Done(true)
        });
        // The new batch in two parts, each with what it ran on beside it (a wildcard names both; the .meta.json isn't a part).
        Write(dir.File("batch.p1.json"), new JObject
        {
            ["Steady"] = Done(true),
            ["Broken"] = Failed("generate", "KeyError: 'slot'", bridgeTrace),
            ["Generates Now"] = Done(false),
            ["Wobbly"] = Done(false, late: 5)
        });
        Write(dir.File("batch.p2.json"), new JObject
        {
            ["Slipped"] = Done(false, late: 1),
            ["Crashes"] = Failed("score", worldTrace),
            ["Fixed"] = Done(true),
            ["Brand New"] = Done(true),
            ["Rebuilds Wrong"] = rebuildsWrong
        });
        foreach (string part in new[] { "batch.p1.meta.json", "batch.p2.meta.json" })
            Write(dir.File(part), new JObject { ["archipelago"] = "9.9.9", ["bridge_sha256"] = "ab12", ["runs"] = new JArray(new JObject { ["archipelago"] = "9.9.9", ["bridge_sha256"] = "ab12" }) });
        // The control: the regressions on the old core with the same seeds. Slipped differs there too; Broken doesn't.
        Write(dir.File("control.json"), new JObject { ["Slipped"] = Done(false, late: 1), ["Broken"] = Done(true) });
        // The load reports (game: world module): Old Game and Gone leave, Brand New comes; Slipped's locations change.
        var oldGames = new JObject
        {
            ["Archipelago"] = "",
            ["Steady"] = "steady",
            ["Slipped"] = "slipped",
            ["Broken"] = "broken",
            ["Crashes"] = "crashes",
            ["Fixed"] = "fixed",
            ["Generates Now"] = "generates",
            ["Wobbly"] = "wobbly",
            ["Old Game"] = "oldgame",
            ["Gone"] = "gone",
            ["Rebuilds Wrong"] = "rebuilds"
        };
        var newGames = new JObject
        {
            ["Archipelago"] = "",
            ["Steady"] = "steady",
            ["Slipped"] = "slipped",
            ["Broken"] = "broken",
            ["Crashes"] = "crashes",
            ["Fixed"] = "fixed",
            ["Generates Now"] = "generates",
            ["Wobbly"] = "wobbly",
            ["Brand New"] = "brandnew",
            ["Rebuilds Wrong"] = "rebuilds"
        };
        Names(dir.File("names-old"), oldGames, new JObject { ["Chest"] = 1, ["Door"] = 2 });
        Names(dir.File("names-new"), newGames, new JObject { ["Chest"] = 1, ["Gate"] = 2, ["Tower"] = 3 });

        string report = Path.Combine(dir.Path, "report", "compare.md");
        string[] arguments =
        {
            "-I", "-X", "utf8", TestEnvironment.RepoFile("AP_Atlas_Source", "Tools", "compare_batches.py"), "--old", dir.File("old.json"),
            "--new", dir.File("batch.p*.json"), "--control", dir.File("control.json"), "--names-old", dir.File("names-old"),
            "--names-new", dir.File("names-new"), "--expected-removed", "oldgame", "--out", report
        };
        var (code, output) = await PythonAsync(python, arguments, ct);
        Assert.True(code == 0, "compare_batches.py failed: " + output);

        // The parts merged into batch.json (the parts' name without .pN), for a control run's --seeds-from.
        var merged = JObject.Parse(File.ReadAllText(dir.File("batch.json"))).Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { "Brand New", "Broken", "Crashes", "Fixed", "Generates Now", "Rebuilds Wrong", "Slipped", "Steady", "Wobbly" }, merged);

        var o = JObject.Parse(File.ReadAllText(Path.Combine(dir.Path, "report", "compare.json")));
        var games = (JObject)o["games"]!;
        string Class(string game) => (string)games[game]!["class"]!;
        Assert.Equal("unchanged", Class("Steady"));
        Assert.Equal("regression", Class("Slipped"));
        Assert.Equal("exact → differs", (string?)games["Slipped"]!["reason"]);
        Assert.Equal("regression", Class("Broken"));
        Assert.Equal("regression", Class("Crashes"));
        Assert.Equal("improvement", Class("Fixed"));
        Assert.Equal("improvement", Class("Generates Now"));
        Assert.Equal("improvement", Class("Brand New"));
        Assert.Equal("informational", Class("Wobbly"));
        // Removed by the update, as said (by its world's module): not a regression. A game that just stops loading is one.
        Assert.Equal("informational", Class("Old Game"));
        Assert.Equal("removed, as expected", (string?)games["Old Game"]!["reason"]);
        Assert.Equal("regression", Class("Gone"));
        Assert.Equal("no longer loads", (string?)games["Gone"]!["reason"]);
        Assert.Null(games["Archipelago"]);
        Assert.Equal(2, o["new_runs"]!.Count());

        // The control shows Slipped too; Broken only fails on the new core, through the bridge, so it blocks. Crashes fails in
        // the world's own code: no block.
        Assert.Equal("the control shows it too", (string?)games["Slipped"]!["control"]);
        Assert.False((bool)games["Slipped"]!["blocks"]!);
        Assert.Equal("only on the new core", (string?)games["Broken"]!["control"]);
        Assert.True((bool)games["Broken"]!["own_code"]!);
        Assert.True((bool)games["Broken"]!["blocks"]!);
        Assert.Equal("not in the control run", (string?)games["Crashes"]!["control"]);
        Assert.False((bool)games["Crashes"]!["own_code"]!);
        Assert.False((bool)games["Crashes"]!["blocks"]!);
        // The traceback in a seed test's answer (a dict, a list in it) is read as text, newlines and all: Atlas's hook, so it blocks.
        Assert.Equal("exact → differs", (string?)games["Rebuilds Wrong"]!["reason"]);
        Assert.True((bool)games["Rebuilds Wrong"]!["own_code"]!);
        Assert.True((bool)games["Rebuilds Wrong"]!["blocks"]!);
        Assert.Equal(new[] { "Broken", "Rebuilds Wrong" }, o["blocking"]!.Select(g => (string?)g));

        // Slipped's names: id 2 renamed (Door → Gate), id 3 added.
        var locations = games["Slipped"]!["names"]!["locations"]!;
        Assert.Equal((1, 0, 1), ((int)locations["added"]!, (int)locations["removed"]!, (int)locations["renamed"]!));

        // compare.regressions.txt: what the control can run again (the regressions the new batch ran), one game a line.
        Assert.Equal(new[] { "Broken", "Crashes", "Rebuilds Wrong", "Slipped" }, File.ReadAllLines(Path.Combine(dir.Path, "report", "compare.regressions.txt")));
        Assert.Contains("**BLOCKS** Broken", File.ReadAllText(report));
    }

    [Fact]
    public async Task A_control_run_takes_the_lent_yaml_and_seed_that_failed_to_generate()
    {
        var ct = TestContext.Current.CancellationToken;
        string? python = await TestEnvironment.PythonAsync(ct);
        if (python == null)
        {
            Assert.Skip("Python isn't installed here (CI runs this test).");
            return;
        }
        using var dir = new TempFolder();
        // batch_logic.py's own steps: a lent YAML fails, a fresh one takes the players folder; what the entry lends a control
        // run then (both failed: the lent YAML and its seed), after a seed came of the fresh one (it and its spoiler's seed),
        // and with no spoiler (the seed recorded).
        const string script = """
            import json, os, sys
            sys.path.insert(0, sys.argv[1])
            import batch_logic
            folder = os.path.join(sys.argv[2], 'Game')
            players, output = batch_logic.fresh_folder(folder)
            entry = {'seed': '4242'}
            batch_logic.set_aside(entry, folder, ('Lent.yaml', b'name: Lent\n', '4242'), 'the lent YAML failed')
            entry['yaml_path'] = os.path.join(players, 'AtlasTest.yaml')
            with open(entry['yaml_path'], 'wb') as f:
                f.write(b'name: Fresh\n')
            out = {'failed': batch_logic.old_seed(entry)}
            entry['multidata'] = os.path.join(folder, 'AP_1.archipelago')
            for name, text in (('AP_1.archipelago', ''), ('AP_1_Spoiler.txt', 'Archipelago Version 9.9.9  -  Seed: 777\n')):
                with open(os.path.join(folder, name), 'w') as f:
                    f.write(text)
            out['generated'] = batch_logic.old_seed(entry)
            out['no_spoiler'] = batch_logic.old_seed({'yaml_path': entry['yaml_path'], 'seed': '55'})
            print(json.dumps({k: [v[0], v[1].decode(), v[2]] for k, v in out.items()}))
            print(json.dumps(entry))
            """;
        var (code, output) = await PythonAsync(python, new[] { "-I", "-B", "-X", "utf8", "-c", script, Path.GetDirectoryName(TestEnvironment.RepoFile("AP_Atlas_Source", "Tools", "batch_logic.py"))!, dir.Path }, ct);
        Assert.True(code == 0, "batch_logic.py failed: " + output);
        string[] lines = output.Trim().Split('\n');
        var lent = JObject.Parse(lines[0]);
        Assert.Equal(new[] { "Lent.yaml", "name: Lent\n", "4242" }, lent["failed"]!.Select(t => (string?)t));
        Assert.Equal(new[] { "AtlasTest.yaml", "name: Fresh\n", "777" }, lent["generated"]!.Select(t => (string?)t));
        Assert.Equal(new[] { "AtlasTest.yaml", "name: Fresh\n", "55" }, lent["no_spoiler"]!.Select(t => (string?)t));
        var entry = JObject.Parse(lines[1]);
        Assert.Equal("the lent YAML failed", (string?)entry["reuse_error"]);
        Assert.False((bool)entry["yaml_reused"]!);
        Assert.Null((string?)entry["seed"]); // the fresh YAML generates with a seed of its own
        Assert.Equal(Path.Combine(dir.Path, "Game", "reused", "Lent.yaml"), (string?)entry["reused_yaml_path"]);
    }

    /// <summary>A Python run to its end (a minute at most): its exit code, and its output with its errors after.</summary>
    private static async Task<(int Code, string Output)> PythonAsync(string python, IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(1), ct);
        return (process.ExitCode, await output + await errors);
    }

    private static JObject Done(bool exact, int late = 0) =>
        new() { ["step"] = "done", ["exact"] = exact, ["late"] = late, ["early"] = 0, ["unsure"] = 0, ["finished"] = true };

    private static JObject Failed(string step, string error, string? trace = null) =>
        new() { ["step"] = step, ["error"] = error, ["trace"] = trace, ["finished"] = true };

    private static void Write(string path, JObject json) => File.WriteAllText(path, json.ToString());

    /// <summary>A names folder: the load report with these games (game: module) and Slipped's name table.</summary>
    private static void Names(string folder, JObject games, JObject slippedLocations)
    {
        Directory.CreateDirectory(folder);
        Write(Path.Combine(folder, "_load_report.json"), new JObject { ["games"] = games, ["failed"] = new JArray(), ["errors"] = new JObject() });
        Write(Path.Combine(folder, "Slipped (local install).json"), new JObject { ["Game"] = "Slipped", ["Items"] = new JObject { ["Key"] = 10 }, ["Locations"] = slippedLocations });
    }
}
