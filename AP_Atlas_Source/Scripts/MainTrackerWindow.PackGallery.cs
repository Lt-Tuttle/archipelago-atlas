#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using AP_Atlas.Core;
using AP_Atlas.Core.Testing;

/// <summary>
/// The pack gallery, for the pack lab only: pictures of every pack in a corpus as a connected slot sees it, so the
/// pictures can be read the way the owner reads the screen. Set ATLAS_PACK_GALLERY=&lt;the corpus folder&gt; (its packs/
/// holds the zips, its names/ the games' name tables as the corpus check takes them), ATLAS_PACK_GALLERY_OUT=&lt;a folder
/// for the pictures&gt; and ATLAS_DATA_DIR=&lt;an empty scratch folder&gt;, then start Atlas in a window (not --headless);
/// ATLAS_PACK_GALLERY_ONLY=&lt;words&gt; limits it to the packs whose file names contain them. For each pack: a fake room on
/// this PC with the game's names (every location missing, no items), the pack installed and the slot connected; pictures
/// of the Map Tracker on its first map, Key Items on each layout, and the Pack Doctor's overview; then the pack is
/// removed again. Nothing goes online and nothing outside the scratch folder is written. Tools/run_pack_gallery.ps1
/// does all of this. Exit code: 0 done, 2 refused, 3 failed.
/// </summary>
public partial class MainTrackerWindow
{
    private static string PackGalleryFolder => System.Environment.GetEnvironmentVariable("ATLAS_PACK_GALLERY") ?? "";
    private static bool PackGalleryRequested => !string.IsNullOrWhiteSpace(PackGalleryFolder);
    private static string PackGalleryOut => System.Environment.GetEnvironmentVariable("ATLAS_PACK_GALLERY_OUT") ?? "";

    private void RunPackGallery() => AP_Atlas.Core.Async.Fire(RunPackGalleryAsync(), "running the pack gallery", tellUser: false);

    private async Task RunPackGalleryAsync()
    {
        int code = 3;
        try { code = await PackGalleryAsync(); }
        catch (Exception ex) { GD.Print("GALLERY FAILED: " + ex); }
        GetTree().Quit(code);
    }

    private async Task<int> PackGalleryAsync()
    {
        string corpus = PackGalleryFolder, outDir = PackGalleryOut;
        string packsIn = Path.Combine(corpus, "packs"), namesIn = Path.Combine(corpus, "names");
        if (!Directory.Exists(packsIn) || string.IsNullOrWhiteSpace(outDir))
        {
            GD.Print("GALLERY REFUSED: ATLAS_PACK_GALLERY needs a corpus folder with packs/ and ATLAS_PACK_GALLERY_OUT a folder for the pictures.");
            return 2;
        }
        Directory.CreateDirectory(outDir);
        string only = System.Environment.GetEnvironmentVariable("ATLAS_PACK_GALLERY_ONLY") ?? "";
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        GetTree().Root.GuiDisableInput = true;
        GetTree().Root.Size = VisualCheckWindowSize;
        await VisualCheckWaitAsync(1.0);

        // Every game's names, as the corpus check takes them.
        var tables = new Dictionary<string, AP_Atlas.Core.PopTracker.GameNameTable>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(namesIn))
        {
            foreach (var file in Directory.GetFiles(namesIn, "*.json"))
            {
                if (Path.GetFileName(file).StartsWith("_", StringComparison.Ordinal)) continue;
                try
                {
                    var table = Newtonsoft.Json.JsonConvert.DeserializeObject<AP_Atlas.Core.PopTracker.GameNameTable>(await File.ReadAllTextAsync(file));
                    if (table == null || string.IsNullOrEmpty(table.Game)) continue;
                    AP_Atlas.Core.PopTracker.GameNames.Store(table);
                    tables[table.Game] = table;
                }
                catch (Exception ex) { GD.Print($"GALLERY names {Path.GetFileName(file)} unreadable: {ex.Message}"); }
            }
            AP_Atlas.Core.PopTracker.GameNames.SetKnownGames(tables.Keys.ToList());
        }
        GD.Print($"GALLERY names for {tables.Count} games");

        var zips = Directory.GetFiles(packsIn, "*.zip").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Where(f => only.Length == 0 || Path.GetFileName(f).Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        string packsDir = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory();
        Directory.CreateDirectory(packsDir);
        var lines = new List<string> { "pack\tgame\tnames\tpictures\tnote" };
        int n = 0;
        foreach (string zip in zips)
        {
            n++;
            string key = Path.GetFileNameWithoutExtension(zip);
            string packOut = Path.Combine(outDir, key);
            string note = "";
            int pictures = 0;
            string game = "";
            bool named = false;
            try
            {
                var pack = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(zip);
                if (pack == null) { note = "not a pack"; lines.Add($"{key}\t\tno\t0\t{note}"); continue; }
                game = AP_Atlas.Core.PopTracker.GameNames.ResolveGame(pack.Manifest) ?? pack.Manifest?.GameName ?? "";
                named = tables.TryGetValue(game, out var names);
                Directory.CreateDirectory(packOut);
                string installed = Path.Combine(packsDir, Path.GetFileName(zip));
                File.Copy(zip, installed, overwrite: true);
                AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();

                await using var server = new FakeArchipelagoServer { SlotGame = game }; // its one slot is "Tester"
                server.Games[game] = new FakeGame("gallery" + n.ToString("0000") + "0000000000000000000000000000000",
                    names?.Items ?? new Dictionary<string, long>(), names?.Locations ?? new Dictionary<string, long>());
                var profile = new MultiworldProfile { Name = "Gallery", ServerUrl = server.Url.ToString() };
                profile.Slots.Clear();
                profile.Slots.Add("Tester");
                _profiles.Add(profile);
                try
                {
                    await OnConnectSlotPressedAsync("Tester", profile);
                    var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view", 30);
                    await UiTestWaitForAsync(() => slot.Pack, "the pack to load", 60);
                    await VisualCheckWaitAsync(2.5); // the scripts, the map's first fit, the layouts' measure
                    host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
                    await VisualCheckWaitAsync(0.8);
                    await GalleryPictureAsync(packOut, "map_first");
                    pictures++;
                    host.ShowTool(AP_Atlas.UI.Tool.KeyItems);
                    await VisualCheckWaitAsync(0.6);
                    var keyItems = slot.ProgressionTracker;
                    await GalleryPictureAsync(packOut, "items_auto");
                    pictures++;
                    foreach (string layout in keyItems.LayoutChoices())
                    {
                        keyItems.SetLayout(layout);
                        await VisualCheckWaitAsync(0.4);
                        await GalleryPictureAsync(packOut, "items_" + VisualCheckSlug(layout.Replace("atlas:", "")));
                        pictures++;
                    }
                    OpenPackDoctor(installed);
                    await VisualCheckWaitAsync(2.0);
                    await GalleryPictureAsync(packOut, "doctor");
                    pictures++;
                    foreach (var window in GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>().ToList()) window.QueueFree();
                    await VisualCheckWaitAsync(0.3);
                }
                finally
                {
                    if (_profiles.Contains(profile)) DeleteProfile(profile);
                    await VisualCheckWaitAsync(0.4);
                    AP_Atlas.Core.SafeFile.Delete(installed);
                    AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
                    await VisualCheckWaitAsync(0.3);
                }
            }
            catch (Exception ex)
            {
                note = ex.Message.Replace('\t', ' ').Replace('\n', ' ');
                GD.Print($"GALLERY {key}: {note}");
            }
            lines.Add($"{key}\t{game}\t{(named ? "yes" : "no")}\t{pictures}\t{note}");
            GD.Print($"GALLERY [{n}/{zips.Count}] {key}: {pictures} pictures{(note.Length > 0 ? " (" + note + ")" : "")}");
        }
        AP_Atlas.Core.SafeFile.WriteAllText(Path.Combine(outDir, "gallery.tsv"), string.Join("\n", lines));
        GD.Print($"GALLERY DONE: {zips.Count} packs, pictures in {Path.GetFullPath(outDir)}");
        return 0;
    }

    private async Task GalleryPictureAsync(string folder, string name)
    {
        if (_globalStatusLabel != null && _globalStatusLabel.Text.StartsWith("Hitch ")) _globalStatusLabel.Text = Tr("Ready");
        GetViewport().GuiReleaseFocus();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
        var picture = GetViewport().GetTexture().GetImage();
        if (picture == null || picture.IsEmpty()) throw new InvalidOperationException("nothing was drawn (the gallery needs a window)");
        picture.Convert(Image.Format.Rgb8);
        AP_Atlas.Core.SafeFile.WriteAllBytes(Path.Combine(folder, name + ".png"), picture.SavePngToBuffer());
    }
}
