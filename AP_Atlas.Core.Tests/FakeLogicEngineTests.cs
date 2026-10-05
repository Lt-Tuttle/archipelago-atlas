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

        var journal = engine.Journal();
        Assert.Equal(1, engine.Starts);
        Assert.Equal(new[] { "init", "update", "update", "explain", "update", "update" },
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
        // first prints a log line and a late answer to the request before, then the answer.
        engine.Chatter = true;
        engine.Apply();
        await using (var second = Bridge.Start(python, engine))
        {
            await second.AskAsync(Init(2000, 2001, 2002), ct);
            int before = second.Lines.Count;
            var answer = await second.AskAsync(Update(new long[] { 1000, 1001 }, 2000, 2001, 2002), ct);
            Assert.Equal(new long[] { 2000, 2001, 2002 }, Ids(answer["reachable"]));
            var extra = second.Lines.Skip(before).SkipLast(1).ToList();
            Assert.Equal(2, extra.Count);
            Assert.Equal("Fake engine: thinking about request 2", extra[0]);
            Assert.Equal(1, (int)JObject.Parse(extra[1])["id"]!);
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

    /// <summary>Three locations: one open, one behind the Sword (or a glitch), one (excluded) behind both. The goal needs both.</summary>
    private static FakeLogicEngine SmallWorld(TempFolder dir)
    {
        string engineDir = Path.Combine(dir.Path, "engine");
        var engine = new FakeLogicEngine(Path.Combine(engineDir, "archipelago"));
        File.Copy(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "Python", "atlas_run.py"), Path.Combine(engineDir, "atlas_run.py"));
        engine.Pool.AddRange(new[] { new FakeItem(1000, "Sword", 1), new FakeItem(1001, "Shield", 1), new FakeItem(1002, "Rupee", 0) });
        engine.Locations.AddRange(new[] { new FakeLocation(2000, "Cave Chest"), new FakeLocation(2001, "Locked Door", 1000), new FakeLocation(2002, "Tower Top", 1000, 1001) });
        engine.Goal = new long[] { 1000, 1001 };
        engine.Excluded.Add(2002);
        engine.Glitched.Add(2001);
        engine.DataChecksum = "feedface";
        engine.Apply();
        return engine;
    }

    private static JObject Init(params long[] locations) =>
        new() { ["action"] = "init", ["game"] = "Test Game", ["player_name"] = "Tester", ["slot"] = 1, ["slot_data"] = new JObject(), ["all_locations"] = new JArray(locations) };

    private static JObject Update(long[] items, params long[] missing) =>
        new() { ["action"] = "update", ["items"] = new JArray(items), ["missing_locations"] = new JArray(missing) };

    private static long[] Ids(JToken? list) => list?.Select(id => (long)id).ToArray() ?? Array.Empty<long>();

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
            string engineDir = Path.GetDirectoryName(engine.Root)!;
            var info = new ProcessStartInfo(python)
            {
                WorkingDirectory = engine.Root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in new[] { "-u", "-X", "utf8", Path.Combine(engineDir, "atlas_run.py"), engine.Root, "UltimateBridge" }) info.ArgumentList.Add(arg);
            info.Environment["TEMP"] = Path.Combine(engineDir, "temp");
            info.Environment["TMP"] = Path.Combine(engineDir, "temp");
            var process = Process.Start(info)!;
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
