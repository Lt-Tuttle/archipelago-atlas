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
    private static string VisualCheckFolder => System.Environment.GetEnvironmentVariable("ATLAS_VISUALCHECK");

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
        string baseline = System.Environment.GetEnvironmentVariable("ATLAS_VISUALCHECK_BASELINE");
        if (!string.IsNullOrWhiteSpace(baseline) && !Directory.Exists(baseline))
        {
            VisualCheckPrint($"VISUALCHECK REFUSED: the baseline folder {baseline} doesn't exist.");
            return 2;
        }
        GetTree().Root.Size = VisualCheckWindowSize;
        await VisualCheckWaitAsync(1.5);

        // Every tab as a new user first sees it.
        for (int tab = 0; tab < _workspaceSwitcher.TabCount; tab++)
        {
            host.ShowGlobalTab(tab);
            await VisualCheckWaitAsync(0.5);
            await VisualCheckPictureAsync($"tab{tab + 1:00}_{VisualCheckSlug(_workspaceSwitcher.GetTabTitle(tab))}");
        }
        host.ShowGlobalTab(0);
        await VisualCheckWaitAsync(0.3);

        // The engine window opens before anything is allowed, so it never goes online.
        await VisualCheckWindowAsync("engine", OpenEngineSetup, 1.5);
        await VisualCheckWindowAsync("privacy_empty", OpenPrivacy);
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.WriteArchipelago, @"C:\Games\Archipelago", true);
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, true);
        _appSettings.ApprovedApworldSources.Add("github.com/example/some-apworld");
        await VisualCheckWindowAsync("privacy_granted", OpenPrivacy);
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

        VisualCheckPrint($"VISUALCHECK TAKEN: {_visualCheckPictures.Count} pictures in {Path.GetFullPath(VisualCheckFolder)}");
        return string.IsNullOrWhiteSpace(baseline) ? 0 : VisualCheckCompare(Path.GetFullPath(baseline));
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

    private async Task VisualCheckPictureAsync(string name)
    {
        // Frame-time reports in the status bar ("Hitch …") differ from run to run: pictures show the bar at rest.
        if (_globalStatusLabel != null && _globalStatusLabel.Text.StartsWith("Hitch ")) _globalStatusLabel.Text = "Ready";
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
