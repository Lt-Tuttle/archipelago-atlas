using System.Diagnostics;
using AP_Atlas.Core.Testing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// The fake logic engine that Atlas's UI test runs logic on answers the way the real engine does, through the real
/// runner (atlas_run.py), and misbehaves only when asked to.
/// </summary>
public class FakeLogicEngineTests
{
    [Fact]
    public async Task Answers_what_is_in_logic_from_its_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        string? python = await TestEnvironment.PythonAsync(ct);
        if (python == null)
        {
            Assert.Skip("Python isn't installed here (CI runs this test).");
            return;
        }
        using var dir = new TempFolder();
        var engine = SmallWorld(dir);
        await using var bridge = Bridge.Start(python, engine);

        var ready = await bridge.AskAsync(Init(2000, 2001, 2002), ct);
        Assert.Equal("ready", (string?)ready["status"]);
        // What the engine printed went to its error output: Atlas's channel carries answers only.
        Assert.DoesNotContain("Fake engine: printed as a world loads", bridge.Lines);
        Assert.Equal(new[] { "1000 Sword 1", "1001 Shield 1", "1002 Rupee 0" },
            ready["item_pool"]!.Select(item => $"{item["id"]} {item["name"]} {item["flags"]}"));
        Assert.Equal(new long[] { 3, 3, 0, 0 }, new[] { "expected", "got", "missing", "extra" }.Select(field => (long)ready["yaml"]![field]!));
        Assert.True((bool)ready["yaml"]!["match"]!);
        Assert.Equal("feedface", (string?)ready["data_checksum"]);

        // Nothing received: only what needs nothing, the glitched location shown as such, and the goal out of reach.
        var start = await bridge.AskAsync(Update(Array.Empty<long>(), 2000, 2001, 2002), ct);
        Assert.Equal(new long[] { 2000 }, Ids(start["reachable"]));
        Assert.Equal(new long[] { 2001 }, Ids(start["glitched"]));
        Assert.Equal(new long[] { 2002 }, Ids(start["excluded"]));
        Assert.False((bool)start["goal"]!);

        var sword = await bridge.AskAsync(Update(new long[] { 1000 }, 2000, 2001, 2002), ct);
        Assert.Equal(new long[] { 2000, 2001 }, Ids(sword["reachable"]));
        Assert.Empty(Ids(sword["glitched"]));
        Assert.False((bool)sword["goal"]!);

        // Why the tower isn't in logic yet: the one item it still lacks.
        var why = await bridge.AskAsync(new JObject { ["action"] = "explain", ["location"] = 2002, ["analyze"] = true }, ct);
        Assert.Equal("Tower Top", (string?)why["location"]);
        Assert.Equal("Has Sword and Has Shield", (string?)why["rule"]);
        Assert.False((bool)why["in_logic"]!);
        Assert.Equal(new[] { "Shield" }, why["single_unlocks"]!.Select(name => (string?)name));

        var both = await bridge.AskAsync(Update(new long[] { 1000, 1001 }, 2000, 2001, 2002), ct);
        Assert.Equal(new long[] { 2000, 2001, 2002 }, Ids(both["reachable"]));
        Assert.True((bool)both["goal"]!);

        // Checked locations aren't missing any more, so they're never in logic.
        var later = await bridge.AskAsync(Update(new long[] { 1000, 1001 }, 2002), ct);
        Assert.Equal(new long[] { 2002 }, Ids(later["reachable"]));

        // What each item opens, in order, in one request: the chest from the start, the door by the Sword, the tower by
        // the Shield; the goal with both.
        var steps = await bridge.AskAsync(new JObject
        {
            ["action"] = "steps",
            ["base"] = new JArray(),
            ["items"] = new JArray(1000, 1001),
            ["start"] = true,
            ["missing_locations"] = new JArray(2000, 2001, 2002)
        }, ct);
        Assert.Equal(new long[] { 2000 }, Ids(steps["start"]));
        Assert.Equal(new[] { "2001", "2002" }, steps["steps"]!.Select(step => string.Join(",", Ids(step))));
        Assert.True((bool)steps["goal"]!);
        Assert.Equal(new long[] { 2002 }, Ids(steps["excluded"]));

        var journal = engine.Journal();
        Assert.Equal(1, engine.Starts);
        Assert.Equal(new[] { "init", "update", "update", "explain", "update", "update", "steps" },
            journal.Select(entry => (string?)entry["request"]?["action"]).OfType<string>());
        Assert.Equal(new long[] { 1000, 1001 }, Ids(engine.Requests("update")[^1]["items"]));
    }

    [Fact]
    public async Task Misbehaves_only_as_asked()
    {
        var ct = TestContext.Current.CancellationToken;
        string? python = await TestEnvironment.PythonAsync(ct);
        if (python == null)
        {
            Assert.Skip("Python isn't installed here (CI runs this test).");
            return;
        }
        using var dir = new TempFolder();
        var engine = SmallWorld(dir);
        engine.CrashOnItem = 1001;
        engine.Apply();

        // A crash on the item: the process exits mid-request, without answering.
        await using (var first = Bridge.Start(python, engine))
        {
            await first.AskAsync(Init(2000, 2001, 2002), ct);
            await first.AskAsync(Update(new long[] { 1000 }, 2000, 2001, 2002), ct);
            await first.SendAsync(Update(new long[] { 1000, 1001 }, 2000, 2001, 2002), ct);
            Assert.Null(await first.ReadLineAsync(ct));
            Assert.Equal(3, await first.ExitCodeAsync(ct));
        }
        Assert.Equal(1, engine.Crashes);

        // It crashed as often as it was told to (once): a new engine answers the same request. Asked to chatter, it
        // first writes a log line, a late answer to the request before and an answer-like line without an id, then
        // the answer.
        engine.Chatter = true;
        engine.Apply();
        await using (var second = Bridge.Start(python, engine))
        {
            await second.AskAsync(Init(2000, 2001, 2002), ct);
            int before = second.Lines.Count;
            var answer = await second.AskAsync(Update(new long[] { 1000, 1001 }, 2000, 2001, 2002), ct);
            Assert.Equal(new long[] { 2000, 2001, 2002 }, Ids(answer["reachable"]));
            var extra = second.Lines.Skip(before).SkipLast(1).ToList();
            Assert.Equal(3, extra.Count);
            Assert.Equal("Fake engine: thinking about request 2", extra[0]);
            Assert.Equal(1, (int)JObject.Parse(extra[1])["id"]!);
            Assert.Null(JObject.Parse(extra[2])["id"]);
        }
        Assert.Equal(1, engine.Crashes);
        Assert.Equal(2, engine.Starts);

        // A slot that can't start: the error the real engine gives, and no answers about logic afterwards.
        engine.Chatter = false;
        engine.StartError = ("world_missing", "Test Game is not installed in this engine.");
        engine.Apply();
        await using (var third = Bridge.Start(python, engine))
        {
            var refused = await third.AskAsync(Init(2000, 2001, 2002), ct);
            Assert.Equal("error", (string?)refused["status"]);
            Assert.Equal("world_missing", (string?)refused["code"]);
            Assert.Equal("Test Game is not installed in this engine.", (string?)refused["message"]);
            var unstarted = await third.AskAsync(Update(Array.Empty<long>(), 2000), ct);
            Assert.Equal("The logic engine has not been started for a slot yet.", (string?)unstarted["error"]);
        }
    }

    [Fact]
    public async Task The_health_check_lists_the_games_in_its_worlds_folder_and_leaves_out_the_failures_it_is_told()
    {
        var ct = TestContext.Current.CancellationToken;
        string? python = await TestEnvironment.PythonAsync(ct);
        if (python == null)
        {
            Assert.Skip("Python isn't installed here (CI runs this test).");
            return;
        }
        using var dir = new TempFolder();
        var engine = SmallWorld(dir);
        // Without check rules the engine has no health check, as before.
        Assert.Equal("boot_failed", (string?)(await RunOnceAsync(python, engine, "AtlasCheck", ct))["event"]);
        string worlds = System.IO.Path.Combine(dir.Path, "worlds");
        Directory.CreateDirectory(System.IO.Path.Combine(worlds, "unpacked"));
        File.WriteAllText(System.IO.Path.Combine(worlds, "unpacked", "archipelago.json"), "{\"game\": \"Unpacked Game\"}");
        WriteApworld(System.IO.Path.Combine(worlds, "alpha.apworld"), "Alpha Game");
        WriteApworld(System.IO.Path.Combine(worlds, "broken.apworld"), "Broken Game");
        engine.CheckWorldsDir = worlds;
        engine.CheckFailures["broken"] = "ModuleNotFoundError: No module named 'nothing'";
        engine.Apply();
        var check = await RunOnceAsync(python, engine, "AtlasCheck", ct);
        Assert.Equal(new[] { "Alpha Game", "Unpacked Game" }, check["games"]!.Select(g => (string?)g));
        Assert.Equal(new[] { "broken" }, check["failed"]!.Select(g => (string?)g));
        Assert.Equal("ModuleNotFoundError: No module named 'nothing'", (string?)check["failed_details"]!["broken"]!["error"]);
        Assert.True((bool)check["tracker"]!);
        Assert.NotNull(check["python"]);
        Assert.Equal(2, engine.ComponentRequests("AtlasCheck").Count);
    }

    /// <summary>A one-shot component run as Atlas runs it (nothing on stdin): its first answer.</summary>
    private static async Task<JObject> RunOnceAsync(string python, FakeLogicEngine engine, string component, CancellationToken ct)
    {
        using var process = Process.Start(FakeEngines.StartInfo(python, engine, component))!;
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync(ct);
        while (await process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct) is { } line)
            if (line.StartsWith('{')) return JObject.Parse(line);
        throw new EndOfStreamException("the engine answered nothing: " + await errors);
    }

    private static void WriteApworld(string path, string game)
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(System.IO.Path.GetFileNameWithoutExtension(path) + "/archipelago.json").Open());
        writer.Write("{\"game\": \"" + game + "\"}");
    }

    [Fact]
    public async Task Loads_the_seeds_apworld_and_says_what_a_world_that_cannot_start_is_as_the_bridge_does()
    {
        var ct = TestContext.Current.CancellationToken;
        string? python = await TestEnvironment.PythonAsync(ct);
        if (python == null)
        {
            Assert.Skip("Python isn't installed here (CI runs this test).");
            return;
        }
        using var dir = new TempFolder();
        var engine = SmallWorld(dir); // its installed world's data: "feedface"
        string seeds = System.IO.Path.Combine(dir.Path, "seed.apworld"), broken = System.IO.Path.Combine(dir.Path, "broken.apworld");
        WriteStandIn(seeds, "5eed", "2.0.0", match: true);
        System.IO.File.WriteAllText(broken, "not a zip");
        engine.RebuildMatch = false;
        engine.Apply();
        await using var bridge = Bridge.Start(python, engine);

        // The installed world: its own data, and a rebuild the rules say doesn't match the server's.
        var installed = await bridge.AskAsync(Init(2000, 2001, 2002), ct);
        Assert.Equal("feedface", (string?)installed["data_checksum"]);
        Assert.False((bool)installed["yaml"]!["match"]!);

        // The seed's apworld in its place: its data and version, and its own word on the rebuild.
        var swapped = await bridge.AskAsync(WithOverride(Init(2000, 2001, 2002), seeds, "5eed"), ct);
        Assert.Equal("5eed", (string?)swapped["data_checksum"]);
        Assert.Equal("2.0.0", (string?)swapped["world_version"]);
        Assert.True((bool)swapped["yaml"]!["apworld_override"]!["used"]!);
        Assert.True((bool)swapped["yaml"]!["apworld_override"]!["matches"]!);
        Assert.True((bool)swapped["yaml"]!["match"]!);

        // The installed world is the seed's already: it's kept.
        var kept = await bridge.AskAsync(WithOverride(Init(2000, 2001, 2002), seeds, "feedface"), ct);
        Assert.False((bool)kept["yaml"]!["apworld_override"]!["used"]!);
        Assert.Equal("feedface", (string?)kept["data_checksum"]);

        // A copy that can't be loaded: said so, and the installed world runs.
        var failed = await bridge.AskAsync(WithOverride(Init(2000, 2001, 2002), broken, "5eed"), ct);
        Assert.NotNull(failed["yaml"]!["apworld_override"]!["error"]);
        Assert.Equal("feedface", (string?)failed["data_checksum"]);

        // A world that couldn't be rebuilt still says what it is; a missing world can't.
        engine.StartError = ("generation_failed", "None of the YAMLs tried could rebuild this world.");
        engine.Apply();
        var refused = await bridge.AskAsync(Init(2000), ct);
        Assert.Equal("generation_failed", (string?)refused["code"]);
        Assert.Equal("feedface", (string?)refused["data_checksum"]);
        engine.StartError = ("world_missing", "Test Game is not installed in this engine.");
        engine.Apply();
        var missing = await bridge.AskAsync(Init(2000), ct);
        Assert.Null(missing["data_checksum"]);
    }

    private static JObject WithOverride(JObject init, string apworld, string expected)
    {
        init["apworld_override"] = apworld;
        init["expected_checksum"] = expected;
        return init;
    }

    private static void WriteStandIn(string path, string checksum, string version, bool match)
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        using var writer = new System.IO.StreamWriter(zip.CreateEntry("test_game/fake_world.json").Open());
        writer.Write(new JObject { ["game"] = "Test Game", ["data_checksum"] = checksum, ["world_version"] = version, ["match"] = match }.ToString());
    }

    private static FakeLogicEngine SmallWorld(TempFolder dir) => FakeEngines.SmallWorld(dir);

    private static JObject Init(params long[] locations) => FakeEngines.Init(locations);

    private static JObject Update(long[] items, params long[] missing) => FakeEngines.Update(items, missing);

    private static long[] Ids(JToken? list) => FakeEngines.Ids(list);

    /// <summary>The engine run as Atlas runs it in portable mode: python -u -X utf8 atlas_run.py &lt;root&gt; UltimateBridge.</summary>
    private sealed class Bridge : IAsyncDisposable
    {
        private readonly Process _process;
        private int _nextId;

        private Bridge(Process process) => _process = process;

        /// <summary>Every line the engine printed so far.</summary>
        public List<string> Lines { get; } = new();

        public static Bridge Start(string python, FakeLogicEngine engine)
        {
            var process = Process.Start(FakeEngines.StartInfo(python, engine))!;
            process.ErrorDataReceived += (_, _) => { }; // drained, so a chatty engine can't block on a full pipe
            process.BeginErrorReadLine();
            return new Bridge(process);
        }

        /// <summary>Sends a request and returns the answer with its id (skipping log lines and stale answers).</summary>
        public async Task<JObject> AskAsync(JObject request, CancellationToken ct)
        {
            int id = await SendAsync(request, ct);
            while (true)
            {
                string line = await ReadLineAsync(ct) ?? throw new EndOfStreamException("the engine closed without answering request " + id);
                if (!line.StartsWith('{')) continue;
                var reply = JObject.Parse(line);
                if ((int?)reply["id"] == id) return reply;
            }
        }

        public async Task<int> SendAsync(JObject request, CancellationToken ct)
        {
            int id = ++_nextId;
            request["id"] = id;
            await _process.StandardInput.WriteLineAsync(request.ToString(Formatting.None).AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
            return id;
        }

        /// <summary>The engine's next line, or null once it has closed its output.</summary>
        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            string? line = await _process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (line != null) Lines.Add(line.Trim());
            return line?.Trim();
        }

        public async Task<int> ExitCodeAsync(CancellationToken ct)
        {
            await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Closing its input ends it, as when Atlas stops it.
                if (!_process.HasExited) await _process.StandardInput.DisposeAsync();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                _process.Kill(entireProcessTree: true);
            }
            _process.Dispose();
        }
    }
}
