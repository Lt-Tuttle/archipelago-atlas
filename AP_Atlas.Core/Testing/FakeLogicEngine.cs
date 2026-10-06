using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Testing;

/// <summary>An item in the fake engine's world: its id, name and Archipelago item flags (1 progression, 2 useful, 4 trap).</summary>
internal sealed record FakeItem(long Id, string Name, int Flags);

/// <summary>A location in the fake engine's world, and the items it needs (all of them) to be in logic.</summary>
internal sealed record FakeLocation(long Id, string Name, params long[] Needs);

/// <summary>
/// A fake logic engine for tests: fake_engine.py in a stand-in Archipelago folder. Atlas starts it the way it starts the
/// real engine (atlas_run.py runs its UltimateBridge component, with the engine folder's environment), and it answers
/// the same requests (start a slot, what's in logic, why) from the rules set here instead of a real world. It can also
/// misbehave on purpose: crash on an item, answer late, fail to load, start a process of its own, or send a log line, a
/// stale answer and an answer-like line without an id first. It writes every request it gets to a journal, which the
/// test reads. It touches nothing outside its folder (bar a process that sleeps until it's stopped) and never goes online.
/// Used by the unit tests and by Atlas's UI test (ATLAS_UITEST); Atlas itself never sets one up.
/// </summary>
internal sealed class FakeLogicEngine
{
    private readonly string _rules, _journal;

    /// <summary>Lays out the stand-in Archipelago folder in <paramref name="root"/>, with empty rules and an empty journal.</summary>
    public FakeLogicEngine(string root)
    {
        Root = Path.GetFullPath(root);
        string state = Path.Combine(Root, "fake_engine");
        _rules = Path.Combine(state, "rules.json");
        _journal = Path.Combine(state, "journal.jsonl");
        Directory.CreateDirectory(state);
        File.Delete(_journal); // a new engine: nothing an earlier one did counts
        Directory.CreateDirectory(Path.Combine(Root, "worlds", "tracker"));
        // What atlas_run.py uses of Archipelago, and the Universal Tracker that Atlas checks is installed.
        Write("ModuleUpdate.py", "# Stands in for Archipelago's ModuleUpdate: the fake engine has nothing to install.\nupdate_ran = False\n");
        Write("Utils.py", """
            # Stands in for Archipelago's Utils: the parts atlas_run.py uses.
            import os
            __version__ = 'fake'


            def cache_path(*path):
                return os.path.join(os.path.dirname(os.path.abspath(__file__)), 'cache', *path)

            """);
        Write(Path.Combine("worlds", "__init__.py"), "# Stands in for Archipelago's worlds: no games, only the fake engine.\n");
        Write(Path.Combine("worlds", "LauncherComponents.py"),
            "# Stands in for Archipelago's LauncherComponents: the fake engine is the UltimateBridge component.\nfrom fake_engine import Component, components  # noqa: F401\n");
        Write(Path.Combine("worlds", "tracker", "TrackerCore.py"), "# Stands in for the Universal Tracker, which Atlas checks is installed. The fake engine doesn't use it.\n");
        using (var script = typeof(FakeLogicEngine).Assembly.GetManifestResourceStream("AP_Atlas.Core.Testing.fake_engine.py")
            ?? throw new InvalidOperationException("The fake engine's script isn't built into AP_Atlas.Core."))
        using (var reader = new StreamReader(script, Encoding.UTF8))
            Write("fake_engine.py", reader.ReadToEnd());
        Apply();
    }

    /// <summary>The stand-in Archipelago folder.</summary>
    public string Root { get; }

    /// <summary>The slot's item pool, as the engine reports it when a slot starts.</summary>
    public List<FakeItem> Pool { get; } = new();

    /// <summary>The world's locations. Only the server's missing locations are ever in logic.</summary>
    public List<FakeLocation> Locations { get; } = new();

    /// <summary>The items the goal needs, or null when the engine can't tell whether the goal is reachable.</summary>
    public long[]? Goal { get; set; }

    /// <summary>Locations the seed excluded.</summary>
    public List<long> Excluded { get; } = new();

    /// <summary>Locations reachable only with glitches (when they're not in logic anyway).</summary>
    public List<long> Glitched { get; } = new();

    /// <summary>The world's data checksum, as the installed apworld reports it (null: none reported).</summary>
    public string? DataChecksum { get; set; }

    /// <summary>Starting a slot fails with this error code and message, as when its game isn't installed (null: it starts).</summary>
    public (string Code, string Message)? StartError { get; set; }

    /// <summary>The engine crashes (its process exits mid-request) on an update that includes this item.</summary>
    public long? CrashOnItem { get; set; }

    /// <summary>How many times it crashes on that item, counted across restarts; afterwards it answers.</summary>
    public int CrashTimes { get; set; } = 1;

    /// <summary>How long the engine waits before answering, in seconds, by request (init, update, explain).</summary>
    public Dictionary<string, double> Delays { get; } = new();

    /// <summary>
    /// Before each answer, the engine writes a log line, a late answer to the request before, and an answer-like line
    /// without an id, all on Atlas's channel.
    /// </summary>
    public bool Chatter { get; set; }

    /// <summary>The engine can't load (as when the tracker won't import): it says so with this error, and ends. Null: it loads.</summary>
    public string? BootError { get; set; }

    /// <summary>When it starts, the engine starts a process of its own that sleeps (as a world could): stopping the engine must end it too.</summary>
    public bool SpawnChild { get; set; }

    /// <summary>The engine's "steps" answers leave the last item's step out (a broken answer).</summary>
    public bool ShortSteps { get; set; }

    /// <summary>Before each answer, the engine writes one line this many characters long on Atlas's channel (0: none).</summary>
    public int FloodAnswers { get; set; }

    /// <summary>Before each answer, the engine writes one line this many characters long on standard error (0: none).</summary>
    public int FloodErrors { get; set; }

    /// <summary>Writes the rules. The engine reads them for every request, so changes apply to the next one.</summary>
    public void Apply()
    {
        var rules = new JObject
        {
            ["pool"] = new JArray(Pool.Select(item => new JObject { ["id"] = item.Id, ["name"] = item.Name, ["flags"] = item.Flags })),
            ["locations"] = new JArray(Locations.Select(loc => new JObject { ["id"] = loc.Id, ["name"] = loc.Name, ["needs"] = new JArray(loc.Needs) })),
            ["goal"] = Goal == null ? JValue.CreateNull() : new JArray(Goal),
            ["excluded"] = new JArray(Excluded),
            ["glitched"] = new JArray(Glitched),
            ["data_checksum"] = DataChecksum,
            ["start_error"] = StartError is { } error ? new JObject { ["code"] = error.Code, ["message"] = error.Message } : JValue.CreateNull(),
            ["crash"] = CrashOnItem is { } item ? new JObject { ["on_item"] = item, ["times"] = CrashTimes } : JValue.CreateNull(),
            ["delays"] = JObject.FromObject(Delays),
            ["chatter"] = Chatter,
            ["boot_error"] = BootError,
            ["spawn_child"] = SpawnChild,
            ["short_steps"] = ShortSteps,
            ["flood"] = new JObject { ["stdout"] = FloodAnswers, ["stderr"] = FloodErrors }
        };
        // Replaced whole, so the engine never reads half a file. It may be reading the old one this moment: then retry.
        string temp = _rules + ".tmp";
        File.WriteAllText(temp, rules.ToString(Formatting.Indented));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, _rules, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>What the engine has received and done so far, oldest first: {"request": …} and {"event": "start" or "crash"}.</summary>
    public IReadOnlyList<JObject> Journal()
    {
        if (!File.Exists(_journal)) return Array.Empty<JObject>();
        string text;
        using (var stream = new FileStream(_journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            text = reader.ReadToEnd();
        var entries = new List<JObject>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The engine may be writing the last line right now: a line that isn't whole yet is left for the next read.
            try { entries.Add(JObject.Parse(line)); }
            catch (JsonReaderException) { } // not whole yet: read next time
        }
        return entries;
    }

    /// <summary>The requests of one kind (init, update, explain) the engine received, oldest first.</summary>
    public IReadOnlyList<JObject> Requests(string action) =>
        Journal().Select(entry => entry["request"] as JObject).OfType<JObject>().Where(request => (string?)request["action"] == action).ToList();

    /// <summary>How many times an engine process started, and how many crashed on purpose.</summary>
    public int Starts => Journal().Count(entry => (string?)entry["event"] == "start");

    /// <summary>The process ids of the engines started so far.</summary>
    public IReadOnlyList<int> StartedPids => Journal().Where(entry => (string?)entry["event"] == "start").Select(entry => (int)entry["pid"]!).ToList();
    public int Crashes => Journal().Count(entry => (string?)entry["event"] == "crash");

    /// <summary>The process ids of the processes the engine started of its own (<see cref="SpawnChild"/>), oldest first.</summary>
    public IReadOnlyList<int> Children => Journal().Where(entry => (string?)entry["event"] == "child").Select(entry => (int)entry["child_pid"]!).ToList();

    private void Write(string relative, string text) => File.WriteAllText(Path.Combine(Root, relative), text.ReplaceLineEndings("\n"));
}
