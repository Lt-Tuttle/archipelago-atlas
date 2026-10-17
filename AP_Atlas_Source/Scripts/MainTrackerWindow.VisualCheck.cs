using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The visual check, for testing only. Set ATLAS_VISUALCHECK=&lt;a folder for the pictures&gt; and ATLAS_DATA_DIR=&lt;an empty
/// scratch folder&gt;, then start Atlas in a window (not --headless). Atlas starts as on a first run, opens a fixed set of
/// screens, saves a picture of each and exits. With ATLAS_VISUALCHECK_BASELINE=&lt;the pictures of an earlier run&gt;, each
/// picture is compared with the earlier one and a ".diff.png" marks every pixel that changed: run it before and after a
/// change (a Godot upgrade, a theme) to see exactly what moved. Tools/run_visualcheck.ps1 does all of this.
/// Exit code: 0 all pictures taken (and the same as the baseline), 1 some differ from the baseline, 2 refused, 3 failed.
/// </summary>
public partial class MainTrackerWindow
{
    private static string VisualCheckFolder => System.Environment.GetEnvironmentVariable("ATLAS_VISUALCHECK") ?? "";

    private static bool VisualCheckRequested => !string.IsNullOrWhiteSpace(VisualCheckFolder);

    // One fixed window size, so pictures from the same PC compare pixel for pixel.
    private static readonly Vector2I VisualCheckWindowSize = new Vector2I(1600, 1500);

    // A pixel counts as changed when one of its colour channels moved by more than this (out of 255).
    private const int VisualCheckTolerance = 24;

    private readonly List<string> _visualCheckLog = new List<string>();
    private readonly List<(string Name, Image Picture)> _visualCheckPictures = new List<(string, Image)>();

    /// <summary>Called first thing in _Ready: refuses (and quits) unless the data folder is an empty scratch folder.</summary>
    private bool VisualCheckAllowed()
    {
        string problem = AP_Atlas.Core.SelfTest.ScratchFolderProblem(null);
        if (problem != null)
        {
            GD.PrintErr("VISUALCHECK REFUSED: " + problem);
            GetTree().Quit(2);
            return false;
        }
        // Log lines in the window show this time instead of the clock's, so pictures from two runs can match.
        AP_Atlas.Core.Logger.DisplayClock = () => new System.DateTime(2000, 1, 1, 12, 0, 0);
        return true;
    }

    private void RunVisualCheck() => AP_Atlas.Core.Async.Fire(RunVisualCheckAsync(), "running the visual check", tellUser: false);

    private async Task RunVisualCheckAsync()
    {
        int code = 3;
        try { code = await VisualCheckAsync(); }
        catch (System.Exception ex) { VisualCheckPrint("VISUALCHECK FAILED: " + ex); }
        try { AP_Atlas.Core.SafeFile.WriteAllText(Path.Combine(VisualCheckFolder, "visualcheck_results.txt"), string.Join(System.Environment.NewLine, _visualCheckLog)); }
        catch (System.Exception ex) { GD.PrintErr("Couldn't save the visual check results: " + ex.Message); }
        GetTree().Quit(code);
    }

    private async Task<int> VisualCheckAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        string? baseline = System.Environment.GetEnvironmentVariable("ATLAS_VISUALCHECK_BASELINE");
        if (!string.IsNullOrWhiteSpace(baseline) && !Directory.Exists(baseline))
        {
            VisualCheckPrint($"VISUALCHECK REFUSED: the baseline folder {baseline} doesn't exist.");
            return 2;
        }
        // The pictures are taken at a fixed scale, never this PC's: 100%, or ATLAS_VISUALCHECK_SCALE (1.5 = Windows' 150%).
        float scale = float.TryParse(System.Environment.GetEnvironmentVariable("ATLAS_VISUALCHECK_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float asked) && asked > 0 ? asked : 1f;
        AP_Atlas.UI.WindowFit.TestScale = scale;
        ApplyZoom();
        // The PC's own mouse plays no part in the pictures: resting over where the window draws, it hovered a button and
        // its tooltip showed in a picture. Every step here is driven from code, never through input.
        GetTree().Root.GuiDisableInput = true;
        GetTree().Root.Size = VisualCheckWindowSize;
        await VisualCheckWaitAsync(1.5);

        // Every tool's tab as a new user first sees it.
        foreach (var tool in AP_Atlas.UI.Tool.All)
        {
            host.ShowTool(tool);
            await VisualCheckWaitAsync(0.5);
            await VisualCheckPictureAsync($"tab{tool.Index + 1:00}_{VisualCheckSlug(tool.Title)}");
        }
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await VisualCheckWaitAsync(0.3);

        // A game's page: its card (no engine in the check's folder: Set up the Atlas Engine first) and its folds; then Advanced open, with the versions known offline (nothing is asked of GitHub).
        host.ShowTool(AP_Atlas.UI.Tool.Games);
        _gamesPage!.Select("Dark Souls Remastered");
        await VisualCheckWaitAsync(0.5);
        await VisualCheckPictureAsync("games_detail");
        AP_Atlas.UI.Kit.ToggleFold(_gamesPage, "games:advanced");
        await VisualCheckWaitAsync(0.5);
        await VisualCheckPictureAsync("games_detail_advanced");
        AP_Atlas.UI.Kit.ToggleFold(_gamesPage, "games:advanced"); // as it was
        // All map packs, at the foot of the Games list (no pack in a fresh folder: what it says then, and its tools).
        _gamesPage.SelectAllPacks();
        await VisualCheckWaitAsync(0.5);
        await VisualCheckPictureAsync("games_all_packs");
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await VisualCheckWaitAsync(0.3);
        // Add games as Home's button opens it, every row's words read (a fresh folder: nothing ticked; the engine leads, not set up).
        OpenAddGames();
        await VisualCheckWaitForAsync(() => AP_Atlas.UI.AddGamesDialog.Showing?.Panel.FactsPending == 0, 15);
        await VisualCheckPictureAsync("add_games");
        // Its confirm for a made-up plan (two apworlds, an official game, a guide's pack), a step of a made-up run with the dots
        // (held still: a moving dot would differ from run to run), and the summary of a made-up batch; nothing is read, downloaded or changed.
        AP_Atlas.UI.AddGamesDialog.Showing?.Panel.ShowConfirmForTests(VisualCheckConfirmSample());
        await VisualCheckWaitAsync(0.6);
        await VisualCheckPictureAsync("add_games_confirm");
        AP_Atlas.UI.AddGamesDialog.Showing?.Panel.ShowRunningForTests(3, new AP_Atlas.Core.Games.AddGamesStatus("Game 2 of 3: Downloading Example Saga's logic…", 0.38f));
        await VisualCheckWaitAsync(0.6);
        await VisualCheckPictureAsync("add_games_running");
        AP_Atlas.UI.AddGamesDialog.Showing?.Panel.ShowSummaryForTests(VisualCheckSummarySample());
        await VisualCheckWaitAsync(0.6);
        await VisualCheckPictureAsync("add_games_summary");
        AP_Atlas.UI.AddGamesDialog.Showing?.QueueFree();
        await VisualCheckWaitAsync(0.3);
        await VisualCheckWindowAsync("review_fixes", () => AP_Atlas.UI.ReviewFixesDialog.Show(this, Tr("What Atlas fixed in Sample Quest map"), VisualCheckReviewSample, _ => { }, _ => { }));
        // Map packs on GitHub for made-up results, from the Games page where the search shows it (nothing is asked of GitHub).
        host.ShowTool(AP_Atlas.UI.Tool.Games);
        AcceptDialog? candidates = null;
        await VisualCheckWindowAsync("map_packs_github", () => candidates = _packManagerPanel.ShowPackCandidatesForTests(VisualCheckCandidatesSample()));
        candidates?.QueueFree(); // it's the pack panel's child, not the window's
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await VisualCheckWaitAsync(0.3);

        // The Atlas Engine page with its Advanced part open (which engine, its parts, the log); nothing goes online.
        ShowEnginePage(System.Array.Empty<string>());
        await VisualCheckWaitAsync(1.0);
        await VisualCheckPictureAsync("engine");
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await VisualCheckWaitAsync(0.3);
        // The guide as the Help window renders it (the documents built in, through Markdown).
        await VisualCheckWindowAsync("help_guide", () => OpenHelp(null), 1.0);
        await VisualCheckWindowAsync("about", ShowAbout, 1.0);
        // The setup window on its Welcome page (Home's welcome card opens it there on a first start).
        await VisualCheckWindowAsync("setup_welcome", () => OpenSetupWizard(resume: false), 1.0);
        // Its multiworld and slots page (no YAML, no game box), its Add games page (a fresh folder: the engine leads) and Ready with its links.
        await VisualCheckWindowAsync("setup_multiworld", () => OpenSetupWizard(resume: false).ShowPageForTests(AP_Atlas.UI.SetupSteps.Multiworld.Id), 1.0);
        var setupBefore = VisualCheckOpenWindows();
        var setupGames = OpenSetupWizard(resume: false);
        setupGames.ShowPageForTests(AP_Atlas.UI.SetupSteps.Games.Id);
        await VisualCheckWaitForAsync(() => setupGames.AddGamesPanelShown?.FactsPending == 0, 15);
        await VisualCheckWaitAsync(0.5);
        await VisualCheckPictureAsync("setup_add_games");
        foreach (var window in VisualCheckOpenWindows().Except(setupBefore)) window.QueueFree();
        await VisualCheckWaitAsync(0.3);
        await VisualCheckWindowAsync("setup_ready", () => OpenSetupWizard(resume: false).ShowPageForTests("ready"), 1.0);
        OpenPrivacy();
        await VisualCheckWaitAsync(0.5);
        await VisualCheckPictureAsync("privacy_empty");
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.WriteArchipelago, @"C:\Games\Archipelago", true);
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, true);
        _appSettings.ApprovedApworldSources.Add("github.com/example/some-apworld");
        OpenPrivacy(); // drawn again with the answers kept
        await VisualCheckWaitAsync(0.5);
        await VisualCheckPictureAsync("privacy_granted");
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await VisualCheckWaitAsync(0.3);
        await VisualCheckWindowAsync("permission_find", () =>
            AP_Atlas.UI.PermissionDialog.Ask(this, _appSettings, AP_Atlas.Core.Permissions.FindArchipelago, null, null, _ => { }));

        // A new multiworld, then its Properties.
        OnAddProfilePressed();
        await VisualCheckWaitAsync(0.5);
        await VisualCheckPictureAsync("profile_new");
        var profile = _profiles.Last();
        AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForProfile(profile.Id, profile.Slots.FirstOrDefault()));
        await VisualCheckWaitAsync(0.8);
        await VisualCheckPictureAsync("profile_properties");

        // A live slot's logic on the UI test's fake engine and server (only with a Python for it): the one banner, ★ rows,
        // Priority only, the card's ★ count, the slot's Properties, a world that couldn't be rebuilt.
        if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("ATLAS_UITEST_PYTHON"))) await VisualCheckLiveLogicAsync();
        else VisualCheckPrint("VISUALCHECK SKIP live logic: no Python (ATLAS_UITEST_PYTHON)");

        VisualCheckPrint($"VISUALCHECK TAKEN: {_visualCheckPictures.Count} pictures in {Path.GetFullPath(VisualCheckFolder)}");
        return string.IsNullOrWhiteSpace(baseline) ? 0 : VisualCheckCompare(Path.GetFullPath(baseline));
    }

    /// <summary>A live slot's logic pictured: priority stars, Priority only, the slot's Properties, a world not rebuilt.</summary>
    private async Task VisualCheckLiveLogicAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var engine = StartFakeEngine(UiTestPython(), fake =>
        {
            fake.Priority.AddRange(new long[] { 2000, 2001 });
            fake.Unsure.Add(2002);
            fake.RebuildLacks.Add(2003);
        });
        await using var server = LogicWorldServer();
        server.SlotName = "Quill";
        var profile = new MultiworldProfile { Name = "Sample multiworld", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Quill");
        _profiles.Add(profile);
        try
        {
            await OnConnectSlotPressedAsync("Quill", profile);
            await VisualCheckWaitForAsync(() => SlotView(profile.Id, "Quill") is { LogicSettled: true } s && s.Model.Logic.Trust != null, 30);
            var slot = SlotView(profile.Id, "Quill") ?? throw new System.InvalidOperationException("the live slot didn't connect");
            host.ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await VisualCheckWaitAsync(0.8);
            await VisualCheckPictureAsync("logic_priority");
            slot.ShowPriorityOnly(true);
            await VisualCheckWaitAsync(0.3);
            await VisualCheckPictureAsync("logic_priority_only");
            slot.ShowPriorityOnly(false);
            AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForSlot(profile.Id, "Quill"));
            await VisualCheckWaitAsync(0.8);
            await VisualCheckPictureAsync("slot_properties_priority");
            engine.StartError = ("generation_failed", "None of the YAMLs tried could rebuild this world.");
            engine.StartReason = "Needs Archipelago 0.6.8";
            engine.Apply();
            slot.RetryLogicEngine();
            await VisualCheckWaitForAsync(() => slot.EngineProblem?.Code == "generation_failed", 30);
            await VisualCheckWaitAsync(0.5);
            await VisualCheckPictureAsync("logic_not_rebuilt");
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
        }
    }

    /// <summary>Opens a window, takes its picture, then closes whatever the step opened.</summary>
    private async Task VisualCheckWindowAsync(string name, System.Action open, double settle = 0.6)
    {
        var before = VisualCheckOpenWindows();
        open();
        await VisualCheckWaitAsync(settle);
        await VisualCheckPictureAsync(name);
        foreach (var window in VisualCheckOpenWindows().Except(before)) window.QueueFree();
        await VisualCheckWaitAsync(0.3);
    }

    private List<Window> VisualCheckOpenWindows() => GetTree().Root.GetChildren().Concat(GetChildren()).OfType<Window>().ToList();

    private async Task VisualCheckWaitAsync(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task VisualCheckWaitForAsync(System.Func<bool> done, double seconds)
    {
        for (double waited = 0; waited < seconds && !done(); waited += 0.1) await VisualCheckWaitAsync(0.1);
    }

    /// <summary>
    /// A made-up batch for the summary's picture: a game with the guide's pack, one whose pack pins few of its checks (another
    /// version's), an official game (Archipelago's version), one with no pack known.
    /// </summary>
    private static AP_Atlas.Core.EngineSetup.GameAdderRun VisualCheckSummarySample()
    {
        var o = new AP_Atlas.Core.Games.AddGamesOutcome(new[] { ("Sample Quest", true, true), ("Example Saga", true, true), ("Model Kingdom", true, true), ("Demo Odyssey", true, true) });
        o.For("Sample Quest").Logic = AP_Atlas.Core.Games.AddStep.Done("2.1.0");
        o.For("Sample Quest").Map = AP_Atlas.Core.Games.AddStep.Done("github.com/sample/quest-tracker v2.0");
        o.For("Sample Quest").PackFromGuide = true;
        o.For("Example Saga").Logic = AP_Atlas.Core.Games.AddStep.Done("0.9.3");
        o.For("Example Saga").Map = AP_Atlas.Core.Games.AddStep.Done("github.com/example/saga-map v1.4");
        o.For("Model Kingdom").Logic = AP_Atlas.Core.Games.AddStep.Done("0.6.7");
        o.For("Model Kingdom").LogicShips = true;
        o.For("Model Kingdom").Map = AP_Atlas.Core.Games.AddStep.Done("github.com/model/kingdom-map v3");
        o.For("Demo Odyssey").Logic = AP_Atlas.Core.Games.AddStep.Done("1.0.0");
        o.For("Demo Odyssey").Map = AP_Atlas.Core.Games.AddStep.Unavailable("no map pack is known for it yet");
        o.PacksToLook.Add(new AP_Atlas.Core.Games.PackToLook("Sample Quest", "sample quest map", "Sample Quest map", "Sample Quest map.zip", 12, 1129, 1150));
        o.PacksToLook.Add(new AP_Atlas.Core.Games.PackToLook("Example Saga", "example saga tracker", "Example Saga tracker", "Example Saga tracker.zip", 35, 31, 1288));
        o.PacksToLook.Add(new AP_Atlas.Core.Games.PackToLook("Model Kingdom", "model kingdom map", "Model Kingdom map", "Model Kingdom map.zip", 0, 420, 420));
        return new AP_Atlas.Core.EngineSetup.GameAdderRun(o);
    }

    /// <summary>A made-up plan for the confirm's picture: a community game with its pack, one whose guide's pack is read once its logic is in, an official game.</summary>
    private static AP_Atlas.Core.Games.AddPlan VisualCheckConfirmSample() => AP_Atlas.Core.Games.AddGamesPlan.Make(new[]
    {
        new AP_Atlas.Core.Games.AddGameFacts
        {
            Game = "Sample Quest",
            Versions = new[] { new AP_Atlas.Core.Games.IndexVersionFacts("2.1.0", "https://github.com/sample/quest-apworld/releases/download/v2.1.0/sample_quest.apworld", "aa", 380_000) },
            Packs = new[] { new AP_Atlas.Core.Games.IndexPackFacts("sample/quest-tracker", "v1.4", "tracker.zip", "bb", 2_100_000, 97, System.Array.Empty<string>()) }
        },
        new AP_Atlas.Core.Games.AddGameFacts
        {
            Game = "Example Saga",
            Versions = new[] { new AP_Atlas.Core.Games.IndexVersionFacts("0.9.3", "https://github.com/example/saga-apworld/releases/download/v0.9.3/example_saga.apworld", "cc", 1_250_000) }
        },
        new AP_Atlas.Core.Games.AddGameFacts
        {
            Game = "Demo Odyssey",
            Official = true,
            WorldsToUnpack = new[] { new AP_Atlas.Core.Games.WorldFolderFacts("demo_odyssey", 2_400_000, false) },
            Packs = new[] { new AP_Atlas.Core.Games.IndexPackFacts("demo/odyssey-map", "v2", "odyssey.zip", "dd", 900_000, 92, System.Array.Empty<string>()) }
        }
    }, new AP_Atlas.Core.Games.AddContext { WorldsZipBytes = 22_424_521 });

    /// <summary>Made-up search results for the picture of Map packs on GitHub: the guide's pick, a measured one, a fork for another version.</summary>
    private static List<(string Game, List<AP_Atlas.Core.MapPackManagerControl.PackCandidate> Candidates, string? Problem)> VisualCheckCandidatesSample() => new()
    {
        ("Sample Quest", new List<AP_Atlas.Core.MapPackManagerControl.PackCandidate>
        {
            new() { Repo = "sample/quest-tracker", Description = "PopTracker pack for Sample Quest", Stars = 42, Updated = new System.DateTime(2026, 9, 30), Recommended = true },
            new() { Repo = "fan/quest-map", Description = "Maps and items", Stars = 17, Updated = new System.DateTime(2026, 8, 12), IndexFit = 97, IndexChecks = "1116 of 1150", IndexListed = true },
            new() { Repo = "fork/quest-map", Description = "A fork for the old apworld", Stars = 2, Updated = new System.DateTime(2025, 12, 1), Fork = true, IndexFit = 31, IndexListed = true, IndexNotes = { "other_version" } }
        }, null)
    };

    /// <summary>Three made-up fixes Atlas made by itself, for the picture of Review fixes.</summary>
    private static IReadOnlyList<AP_Atlas.Core.PopTracker.PackChange> VisualCheckReviewSample() => new[]
    {
        new AP_Atlas.Core.PopTracker.PackChange("links", "link:Cave|Chest", "Cave / Chest shows Cave Chest Room", "automatic", "its name matches 88%", new System.DateTime(2026, 10, 16, 9, 2, 0), null),
        new AP_Atlas.Core.PopTracker.PackChange("links", "link:Shrine|Bell", "Shrine / Bell shows Shrine Bell", "automatic", null, new System.DateTime(2026, 10, 16, 9, 1, 0), null),
        new AP_Atlas.Core.PopTracker.PackChange("tiles", "tile:hookshot", "Tile Hookshot: shows Hookshot", "automatic", "the only 'Hookshot' in the game", new System.DateTime(2026, 10, 16, 9, 0, 0), null)
    };

    private async Task VisualCheckPictureAsync(string name)
    {
        // Frame-time reports in the status bar ("Hitch …") differ from run to run: pictures show the bar at rest.
        if (_globalStatusLabel != null && _globalStatusLabel.Text.StartsWith("Hitch ")) _globalStatusLabel.Text = "Ready";
        // A text box's caret is drawn only while the OS window is the active one, which differs from run to run: no control keeps the focus in a picture.
        GetViewport().GuiReleaseFocus();
        // Let deferred layout finish, then draw now: in low-processor mode Godot draws only when something changed.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
        var picture = GetViewport().GetTexture().GetImage();
        if (picture == null || picture.IsEmpty())
            throw new System.InvalidOperationException($"Nothing was drawn for {name}: the visual check needs a window (it can't run with --headless).");
        picture.Convert(Image.Format.Rgb8);
        AP_Atlas.Core.SafeFile.WriteAllBytes(Path.Combine(VisualCheckFolder, name + ".png"), picture.SavePngToBuffer());
        _visualCheckPictures.Add((name, picture));
        VisualCheckPrint("VISUALCHECK PICTURE " + name);
    }

    /// <summary>Compares each picture with the baseline's; returns 1 if any differ (or one is missing), else 0.</summary>
    private int VisualCheckCompare(string baseline)
    {
        int differ = 0;
        foreach (var (name, picture) in _visualCheckPictures)
        {
            string earlierPath = Path.Combine(baseline, name + ".png");
            if (!File.Exists(earlierPath)) { VisualCheckPrint($"VISUALCHECK NEW {name} (the baseline has no picture of it)"); continue; }
            var earlier = Image.LoadFromFile(earlierPath);
            if (earlier == null || earlier.IsEmpty()) { VisualCheckPrint($"VISUALCHECK CHANGED {name}: the baseline's picture can't be read"); differ++; continue; }
            earlier.Convert(Image.Format.Rgb8);
            if (earlier.GetSize() != picture.GetSize())
            {
                VisualCheckPrint($"VISUALCHECK CHANGED {name}: the picture is {picture.GetWidth()}x{picture.GetHeight()}, the baseline's {earlier.GetWidth()}x{earlier.GetHeight()}");
                differ++;
                continue;
            }
            var (changed, area, diff) = VisualCheckDiff(earlier, picture);
            if (changed == 0) { VisualCheckPrint($"VISUALCHECK SAME {name}"); continue; }
            differ++;
            AP_Atlas.Core.SafeFile.WriteAllBytes(Path.Combine(VisualCheckFolder, name + ".diff.png"), diff.SavePngToBuffer());
            double percent = 100.0 * changed / (picture.GetWidth() * picture.GetHeight());
            VisualCheckPrint($"VISUALCHECK CHANGED {name}: {changed} pixels ({percent:0.###}%) between ({area.Position.X},{area.Position.Y}) and ({area.End.X - 1},{area.End.Y - 1}); see {name}.diff.png");
        }
        var taken = _visualCheckPictures.Select(p => p.Name).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(baseline, "*.png").Where(f => !f.EndsWith(".diff.png", System.StringComparison.OrdinalIgnoreCase)))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (taken.Contains(name)) continue;
            VisualCheckPrint($"VISUALCHECK MISSING {name} (in the baseline, not taken this time)");
            differ++;
        }
        VisualCheckPrint(differ == 0 ? "VISUALCHECK DONE: every picture matches the baseline" : $"VISUALCHECK DONE: {differ} picture(s) differ from the baseline");
        return differ == 0 ? 0 : 1;
    }

    /// <summary>The changed pixels, where they are, and a picture of them: the earlier picture faded to grey with changes in magenta.</summary>
    private static (int Changed, Rect2I Area, Image Diff) VisualCheckDiff(Image earlier, Image now)
    {
        int width = now.GetWidth(), height = now.GetHeight();
        byte[] a = earlier.GetData(), b = now.GetData();
        var diff = new byte[a.Length];
        int changed = 0, minX = width, minY = height, maxX = -1, maxY = -1;
        for (int pixel = 0, i = 0; pixel < width * height; pixel++, i += 3)
        {
            int delta = System.Math.Max(System.Math.Abs(a[i] - b[i]), System.Math.Max(System.Math.Abs(a[i + 1] - b[i + 1]), System.Math.Abs(a[i + 2] - b[i + 2])));
            if (delta > VisualCheckTolerance)
            {
                changed++;
                int x = pixel % width, y = pixel / width;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                diff[i] = 255; diff[i + 1] = 0; diff[i + 2] = 255;
            }
            else
            {
                byte grey = (byte)((a[i] * 30 + a[i + 1] * 59 + a[i + 2] * 11) / 300);
                diff[i] = diff[i + 1] = diff[i + 2] = grey;
            }
        }
        var area = changed == 0 ? new Rect2I() : new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return (changed, area, Image.CreateFromData(width, height, false, Image.Format.Rgb8, diff));
    }

    private static string VisualCheckSlug(string title) =>
        new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

    private void VisualCheckPrint(string line)
    {
        _visualCheckLog.Add(line);
        GD.Print(line);
    }
}
