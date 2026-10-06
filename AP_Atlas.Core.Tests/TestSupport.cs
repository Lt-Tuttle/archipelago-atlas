using System.Diagnostics;
using AP_Atlas.Core.Testing;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>A new, empty folder under the system temp folder, deleted afterwards. Tests never touch real data.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atlas-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { } // a file still open: the system temp folder is cleaned up eventually
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>What the tests use from this computer and the repository.</summary>
internal static class TestEnvironment
{
    /// <summary>A Python on this computer that runs, or null (the Windows Store alias exists even without Python).</summary>
    public static async Task<string?> PythonAsync(CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-c", "import sys; print(sys.executable)" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process == null) return null;
            string path = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            return process.ExitCode == 0 && File.Exists(path) ? path : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no python on the PATH
        }
    }

    /// <summary>A file of the repository, found from the tests' folder upwards.</summary>
    public static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Couldn't find " + System.IO.Path.Combine(parts) + " above the test folder.");
    }
}

/// <summary>The fake logic engine as the tests use it: a small world, its requests, and how Atlas starts an engine.</summary>
internal static class FakeEngines
{
    /// <summary>Three locations: one open, one behind the Sword (or a glitch), one (excluded) behind both. The goal needs both.</summary>
    public static FakeLogicEngine SmallWorld(TempFolder dir)
    {
        string engineDir = System.IO.Path.Combine(dir.Path, "engine");
        var engine = new FakeLogicEngine(System.IO.Path.Combine(engineDir, "archipelago"));
        File.Copy(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "Python", "atlas_run.py"), System.IO.Path.Combine(engineDir, "atlas_run.py"));
        engine.Pool.AddRange(new[] { new FakeItem(1000, "Sword", 1), new FakeItem(1001, "Shield", 1), new FakeItem(1002, "Rupee", 0) });
        engine.Locations.AddRange(new[] { new FakeLocation(2000, "Cave Chest"), new FakeLocation(2001, "Locked Door", 1000), new FakeLocation(2002, "Tower Top", 1000, 1001) });
        engine.Goal = new long[] { 1000, 1001 };
        engine.Excluded.Add(2002);
        engine.Glitched.Add(2001);
        engine.DataChecksum = "feedface";
        engine.Apply();
        return engine;
    }

    /// <summary>How Atlas starts the engine in portable mode: python -u -X utf8 atlas_run.py &lt;root&gt; UltimateBridge.</summary>
    public static ProcessStartInfo StartInfo(string python, FakeLogicEngine engine)
    {
        string engineDir = System.IO.Path.GetDirectoryName(engine.Root)!;
        var info = new ProcessStartInfo(python)
        {
            WorkingDirectory = engine.Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "-u", "-X", "utf8", System.IO.Path.Combine(engineDir, "atlas_run.py"), engine.Root, "UltimateBridge" }) info.ArgumentList.Add(arg);
        info.Environment["TEMP"] = System.IO.Path.Combine(engineDir, "temp");
        info.Environment["TMP"] = System.IO.Path.Combine(engineDir, "temp");
        return info;
    }

    public static JObject Init(params long[] locations) =>
        new() { ["action"] = "init", ["game"] = "Test Game", ["player_name"] = "Tester", ["slot"] = 1, ["slot_data"] = new JObject(), ["all_locations"] = new JArray(locations) };

    public static JObject Update(long[] items, params long[] missing) =>
        new() { ["action"] = "update", ["items"] = new JArray(items), ["missing_locations"] = new JArray(missing) };

    public static long[] Ids(JToken? list) => list?.Select(id => (long)id).ToArray() ?? Array.Empty<long>();
}

/// <summary>What the tests save with SafeFile.</summary>
public sealed class Sample
{
    public int Version { get; set; }
    public List<string> Items { get; set; } = new();
}
