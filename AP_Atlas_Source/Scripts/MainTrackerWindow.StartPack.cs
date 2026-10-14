#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;

/// <summary>"Start a pack" for a game no map pack exists for: a PopTracker pack made from the game's names (PackStarter).</summary>
public partial class MainTrackerWindow
{
    /// <summary>Most items a game may have for all of them to go in Key Items; more, and only the progression items the slot has received do.</summary>
    public const int AllItemsInGridUpTo = 60;

    /// <summary>Opens "Start a pack" for a slot's game: the game's names come from the room (or the engine).</summary>
    public void StartPack(string game, SlotTrackerControl? slot)
    {
        var names = GameNames.Best(game);
        if (names?.Locations == null || names.Locations.Count == 0)
        {
            ShowToast(Tr("Atlas doesn't have {0}'s names yet: connect a slot of it first.").Replace("{0}", game), ThemeColors.Warning);
            return;
        }
        var locations = names.Locations.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        var items = (names.Items ?? new Dictionary<string, long>()).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        var grid = items.Count <= AllItemsInGridUpTo ? items.Keys.ToList() : slot?.ProgressionReceived().ToList() ?? new List<long>();
        int areas = locations.Values.Select(PackStarter.AreaLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var dialog = new AP_Atlas.UI.StartPackDialog(game, locations.Count, areas, items.Count, grid.Count, (name, picture) => MakePack(game, name, picture, locations, items, grid));
        AddChild(dialog);
        AP_Atlas.UI.WindowFit.Pop(dialog, 620);
    }

    private void MakePack(string game, string name, string? picturePath, Dictionary<long, string> locations, Dictionary<long, string> items, List<long> grid)
    {
        try
        {
            (byte[], string, int, int)? picture = null;
            if (picturePath != null)
            {
                byte[] bytes = File.ReadAllBytes(picturePath);
                var format = ImageHeader.Format(bytes);
                var size = ImageHeader.Size(bytes);
                if (format is not (ImageFormat.Png or ImageFormat.Jpeg) || size == null || new ImageBudget().Take(bytes) is string)
                {
                    ShowToast(Tr("That picture can't be used (a PNG or JPEG Atlas can show)."), ThemeColors.Warning);
                    return;
                }
                picture = (bytes, format == ImageFormat.Png ? ".png" : ".jpg", size.Value.Width, size.Value.Height);
            }
            string file = Path.Combine(PopTrackerPackLoader.GetPacksDirectory(), string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim() + ".zip");
            if (File.Exists(file))
            {
                ShowToast(Tr("A pack file named {0} exists already: choose another name.").Replace("{0}", Path.GetFileName(file)), ThemeColors.Warning);
                return;
            }
            var started = PackStarter.Build(name, game, locations, items, grid, picture);
            Directory.CreateDirectory(PopTrackerPackLoader.GetPacksDirectory());
            SafeFile.WriteAllBytes(file, started.Zip);
            AP_Atlas.Core.Logger.LogInfo($"Started a map pack for {game}: {Path.GetFileName(file)} ({started.Locations} locations in {started.Areas} areas, {started.Items} items, {started.Tiles} in Key Items).");
            PopTrackerPackLoader.NotifyPacksChanged();
            ShowToast(Tr("Made {0}: every location of {1} is a pin on its map, grouped by area. Right-click a pin to move it where it belongs.").Replace("{0}", name).Replace("{1}", game), ThemeColors.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AP_Atlas.Core.Logger.LogWarning("The started pack couldn't be written: " + ex.Message);
            ShowToast(Tr("The pack couldn't be written: {0}").Replace("{0}", ex.Message), ThemeColors.Error);
        }
    }
}
