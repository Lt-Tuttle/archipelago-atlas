using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// The logic bridge's rebuild helpers (atlas_bridge.py) on stand-ins for Archipelago 0.6.7: a failure said in a few words,
/// a YAML's newer "requires" version dropped only when the world allows the engine, an apworld made for another core
/// refused with the version it needs, and the seed's priority and excluded checks read from the rebuilt world.
/// </summary>
public class BridgeRebuildTests
{
    private const string Probe = """
        import json, sys, types
        stub, bridge, fits, newer = sys.argv[1:5]
        sys.path[:0] = [stub, bridge]
        import atlas_bridge as b

        out = {}
        texts = [
            'ValueError: Encountered 1 error(s) in player files. See logs for full tracebacks.\n\n'
            '1. File player.yaml document #1 (name: EldenTuttle) is invalid. Please fix your yaml.\n'
            'Exception: Settings reports required version of generator is at least 0.6.8, however generator is of version 0.6.7',
            'Exception: Settings reports required version of world "EldenRing" is at least 0.1.2, however world is of version 0.1.1.',
            'Please fix your yaml.\nValueError: Could not find option "bogus" for "Goal"\n Which caused: Options.OptionError: Error generating option goal in Test Game',
            'Traceback (most recent call last):\n  File "x.py", line 1\nKeyError: \'ut_status\'',
            'Exception: ' + 'x' * 150,
            '',
        ]
        out['reasons'] = [list(b.plain_reason(t)) for t in texts]

        class Fits:
            zip_path = fits
        class Newer:
            zip_path = newer
        class Shipped:
            pass  # a world that comes with Archipelago: no apworld file
        doc = {'name': 'P', 'game': 'G', 'requires': {'version': '0.6.8', 'game': {'G': '0.1.2'}}, 'G': {}}
        relaxed, dropped = b.relax_requires(Fits, doc)
        out['fits'] = [relaxed['requires'], dropped, doc['requires']['version']]
        kept, dropped = b.relax_requires(Newer, doc)
        out['newer'] = [kept['requires'], dropped]
        shipped, dropped = b.relax_requires(Shipped, doc)
        out['shipped'] = [shipped['requires'], dropped]
        same, dropped = b.relax_requires(Fits, {'requires': {'version': '0.6.7'}})
        out['same'] = [same['requires'], dropped]
        try:
            b.refuse_unfit_apworld(newer)
            out['refused'] = None
        except RuntimeError as e:
            out['refused'] = str(e)
        b.refuse_unfit_apworld(fits)

        import worlds
        worlds.world_sources = [types.SimpleNamespace(is_zip=False, resolved_path=stub),
                                types.SimpleNamespace(is_zip=True, resolved_path=fits),
                                types.SimpleNamespace(is_zip=True, resolved_path=newer)]
        out['unloaded'] = [b.unloaded_reason('G'), b.unloaded_reason('Other')]

        def loc(name, address, kind):
            return types.SimpleNamespace(name=name, address=address, progress_type=types.SimpleNamespace(name=kind))
        locations = [loc('Set by the world', 1, 'PRIORITY'), loc('Listed', 2, 'DEFAULT'), loc('Listed, excluded by the seed', 3, 'EXCLUDED'),
                     loc('Listed, in exclude_locations', 4, 'DEFAULT'), loc('An event', None, 'PRIORITY'), loc('Plain', 5, 'DEFAULT')]
        options = types.SimpleNamespace(
            priority_locations=types.SimpleNamespace(value={'Listed', 'Listed, excluded by the seed', 'Listed, in exclude_locations'}),
            exclude_locations=types.SimpleNamespace(value={'Listed, in exclude_locations'}))
        world = types.SimpleNamespace(options=options)
        core = types.SimpleNamespace(player_id=1, multiworld=types.SimpleNamespace(get_locations=lambda player: locations), get_current_world=lambda: world)
        excluded = b.progress_ids(core, 'EXCLUDED')
        out['excluded'] = sorted(excluded)
        out['priority'] = sorted(b.priority_ids(core, excluded))
        sys.__stdout__.write(json.dumps(out) + '\n')
        """;

    [Fact]
    public async Task Rebuilds_say_why_they_failed_ignore_a_newer_stamp_the_world_allows_and_read_the_priority_checks()
    {
        var ct = TestContext.Current.CancellationToken;
        string? python = await TestEnvironment.PythonAsync(ct);
        if (python == null)
        {
            Assert.Skip("Python isn't installed here (CI runs this test).");
            return;
        }
        using var dir = new TempFolder();
        // Stand-ins for what the bridge imports of Archipelago 0.6.7; the bridge itself; two apworlds; the probe apart.
        string stub = Directory.CreateDirectory(Path.Combine(dir.Path, "archipelago")).FullName;
        Directory.CreateDirectory(Path.Combine(stub, "worlds"));
        File.WriteAllText(Path.Combine(stub, "Utils.py"), """
            import typing
            __version__ = '0.6.7'


            class Version(typing.NamedTuple):
                major: int
                minor: int
                build: int


            def tuplize_version(version):
                return Version(*(int(piece) for piece in version.split('.')))


            version_tuple = tuplize_version(__version__)
            """);
        File.WriteAllText(Path.Combine(stub, "worlds", "__init__.py"), "");
        File.WriteAllText(Path.Combine(stub, "worlds", "LauncherComponents.py"), """
            class Type:
                CLIENT = 'client'


            class Component:
                def __init__(self, *args, **kwargs):
                    pass


            components = []
            """);
        string bridge = Directory.CreateDirectory(Path.Combine(dir.Path, "bridge")).FullName;
        File.Copy(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "Python", "atlas_bridge.py"), Path.Combine(bridge, "atlas_bridge.py"));
        string worlds = Directory.CreateDirectory(Path.Combine(dir.Path, "apworlds")).FullName;
        string fits = Apworld(worlds, "fits", "0.6.7"), newer = Apworld(worlds, "newer", "0.6.9");
        string probe = Path.Combine(Directory.CreateDirectory(Path.Combine(dir.Path, "probe")).FullName, "probe.py");
        File.WriteAllText(probe, Probe);

        var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-I", "-X", "utf8", probe, stub, bridge, fits, newer }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(1), ct);
        Assert.True(process.ExitCode == 0, "the probe failed: " + await errors);
        var o = JObject.Parse((await output).Trim().Split('\n').Last());

        var reasons = o["reasons"]!.Select(r => ((string?)r[0], (string?)r[1])).ToList();
        Assert.Equal(("Needs Archipelago 0.6.8", "needs_archipelago"), reasons[0]);
        Assert.Equal(("Needs EldenRing apworld 0.1.2", "needs_world_version"), reasons[1]);
        Assert.Equal(("Option goal rejected", "option"), reasons[2]);
        Assert.Equal(("KeyError: 'ut_status'", "other"), reasons[3]);
        Assert.Equal(100, reasons[4].Item1!.Length);
        Assert.EndsWith("…", reasons[4].Item1);
        Assert.Equal(((string?)null, "other"), reasons[5]);

        // The world allows 0.6.7: the stamp goes, the game's version stays, and the YAML read is untouched.
        Assert.Null(o["fits"]![0]!["version"]);
        Assert.Equal("0.1.2", (string?)o["fits"]![0]!["game"]!["G"]);
        Assert.Equal("0.6.8", (string?)o["fits"]![1]);
        Assert.Equal("0.6.8", (string?)o["fits"]![2]);
        // The world itself needs 0.6.9: the stamp stays, so Generate refuses and says which version.
        Assert.Equal("0.6.8", (string?)o["newer"]![0]!["version"]);
        Assert.Equal(JTokenType.Null, o["newer"]![1]!.Type);
        Assert.Equal("0.6.8", (string?)o["shipped"]![1]);
        Assert.Equal("0.6.7", (string?)o["same"]![0]!["version"]);
        Assert.Equal(JTokenType.Null, o["same"]![1]!.Type);
        Assert.Equal("Needs Archipelago 0.6.9", (string?)o["refused"]);
        // A game the engine skipped because its apworld needs a newer core says which version; another game says nothing.
        Assert.Equal("Needs Archipelago 0.6.9", (string?)o["unloaded"]![0]);
        Assert.Equal(JTokenType.Null, o["unloaded"]![1]!.Type);

        // PRIORITY set by the world, plus the standard list minus exclude_locations and the seed's excluded; no event.
        Assert.Equal(new long[] { 3 }, o["excluded"]!.Select(t => (long)t).ToArray());
        Assert.Equal(new long[] { 1, 2 }, o["priority"]!.Select(t => (long)t).ToArray());
    }

    private static string Apworld(string folder, string name, string minimum)
    {
        string path = Path.Combine(folder, name + ".apworld");
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(name + "/archipelago.json").Open());
        writer.Write(new JObject { ["game"] = "G", ["minimum_ap_version"] = minimum, ["world_version"] = "0.1.2" }.ToString());
        return path;
    }
}
