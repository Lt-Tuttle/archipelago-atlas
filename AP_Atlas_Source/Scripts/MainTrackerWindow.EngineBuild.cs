#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.EngineSetup;
using Godot;

/// <summary>
/// The release build's two engine steps, headless (Tools/build_engine.ps1 runs them): building the core that ships with
/// Atlas, and checking a shipped core works on a fresh data folder. Neither opens the window.
/// </summary>
public partial class MainTrackerWindow
{
    /// <summary>ATLAS_BUILD_ENGINE=1 (with ATLAS_DATA_DIR naming the build's own folder): Atlas's own engine setup, every download allowed, then the core's stamp.</summary>
    private static bool EngineBuildRequested => System.Environment.GetEnvironmentVariable("ATLAS_BUILD_ENGINE") == "1";

    /// <summary>ATLAS_CHECK_SHIPPED_ENGINE=1 (a fresh ATLAS_DATA_DIR holding Archipelago's zip): the shipped core gets ready and a game's world unpacks and loads.</summary>
    private static bool ShippedEngineCheckRequested => System.Environment.GetEnvironmentVariable("ATLAS_CHECK_SHIPPED_ENGINE") == "1";

    /// <summary>The game the shipped-core check unpacks: small, and it imports another world (A Link to the Past), so the closure is tried too.</summary>
    private const string ShippedCheckGame = "Bumper Stickers";

    private void RunEngineBuild() => AP_Atlas.Core.Async.Fire(RunEngineBuildAsync(), "building the engine", tellUser: false);

    private async Task RunEngineBuildAsync()
    {
        int code = 1;
        try
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("ATLAS_DATA_DIR")))
                GD.PrintErr("ENGINEBUILD REFUSED: ATLAS_DATA_DIR must name the build's own folder.");
            else
            {
                // Never a shipped core here: this builds one, the way a copy of Atlas run from source sets its engine up.
                AtlasEngine.Initialize(new AppSettings(), null);
                var install = EngineInstall.Portable();
                bool ok = await AtlasEngine.SetUpAsync(install, line => GD.Print("ENGINE " + line), null, CancellationToken.None);
                if (!ok) GD.PrintErr("ENGINEBUILD FAILED: " + AtlasEngine.LastSetupProblem);
                else if (!AtlasEngine.WorldsZipPresent) GD.PrintErr("ENGINEBUILD FAILED: Archipelago's zip wasn't kept for the game worlds.");
                else if (AtlasEngine.Worlds.Worlds.Keys.Any(AtlasEngine.WorldPresent)) GD.PrintErr("ENGINEBUILD FAILED: the core holds game worlds it should unpack on demand.");
                else
                {
                    AtlasEngine.WriteCoreStamp();
                    GD.Print($"ENGINEBUILD DONE: Python {AtlasEngine.State.Python}, Archipelago {AtlasEngine.State.Archipelago}, Universal Tracker {AtlasEngine.State.Tracker}, packages {AtlasEngine.State.PackagesSignature}");
                    code = 0;
                }
            }
        }
        catch (Exception ex) { GD.PrintErr("ENGINEBUILD CRASHED: " + ex); }
        GetTree().Quit(code);
    }

    private void RunShippedEngineCheck() => AP_Atlas.Core.Async.Fire(RunShippedEngineCheckAsync(), "checking the shipped engine", tellUser: false);

    private async Task RunShippedEngineCheckAsync()
    {
        int code = 1;
        try
        {
            AtlasEngine.Initialize(new AppSettings(), System.IO.Path.GetDirectoryName(OS.GetExecutablePath()));
            var install = EngineInstall.Portable();
            if (!AtlasEngine.IsShipped) GD.PrintErr("SHIPPEDENGINE FAILED: no engine ships beside this Atlas.");
            else
            {
                string before = CoreSnapshot(AtlasEngine.CoreDir);
                bool ready = await AtlasEngine.PrepareShippedAsync(install, line => GD.Print("ENGINE " + line), CancellationToken.None);
                bool unpacked = ready && await AtlasEngine.EnsureWorldsAsync(install, new[] { ShippedCheckGame }, false, line => GD.Print("ENGINE " + line), CancellationToken.None);
                var check = unpacked ? await AtlasEngine.RunCheckAsync(install, line => GD.Print("ENGINE " + line), CancellationToken.None) : null;
                string after = CoreSnapshot(AtlasEngine.CoreDir);
                if (!ready) GD.PrintErr("SHIPPEDENGINE FAILED: the shipped core didn't pass its health check: " + AtlasEngine.LastSetupProblem);
                else if (!unpacked) GD.PrintErr($"SHIPPEDENGINE FAILED: {ShippedCheckGame}'s world wasn't unpacked from the kept zip.");
                else if (check == null || !check.Passed || !check.Games.Contains(ShippedCheckGame)) GD.PrintErr($"SHIPPEDENGINE FAILED: {ShippedCheckGame} didn't load once unpacked: {check?.Problem ?? "no check"}; games {string.Join(", ", check?.Games ?? new())}");
                else if (before != after) GD.PrintErr("SHIPPEDENGINE FAILED: something wrote into the shipped core.");
                else
                {
                    GD.Print($"SHIPPEDENGINE DONE: the core passed its health check and {ShippedCheckGame} loaded from Archipelago's zip; nothing was written into the core.");
                    code = 0;
                }
            }
        }
        catch (Exception ex) { GD.PrintErr("SHIPPEDENGINE CRASHED: " + ex); }
        GetTree().Quit(code);
    }

    /// <summary>Every file of a folder with its size and time: two equal snapshots mean nothing was written there.</summary>
    private static string CoreSnapshot(string dir) =>
        string.Join("\n", new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetRelativePath(dir, f.FullName)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}").OrderBy(s => s, StringComparer.Ordinal));
}
