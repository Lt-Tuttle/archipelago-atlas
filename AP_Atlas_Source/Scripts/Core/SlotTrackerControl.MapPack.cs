#nullable disable
using Godot;
using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Packets;
using Archipelago.MultiClient.Net.Models;
using System.Linq;
using System.Threading.Tasks;
using Color = Godot.Color;

/// <summary>The map pack: loading it, the game's names and the pack's index, and the pack's own scripts.</summary>
public partial class SlotTrackerControl : MarginContainer
{
    // =====================================================================
    // Map pack
    // =====================================================================

    private void LoadMapPack() => AP_Atlas.Core.Async.Fire(LoadMapPackAsync(), $"loading {_slotName}'s map pack");

    /// <summary>The pack's images, used while this slot is open (released when it ends).</summary>
    private IDisposable _packImages;

    private async Task LoadMapPackAsync()
    {
        string game = Session?.ConnectionInfo?.Game;
        if (string.IsNullOrEmpty(game)) return;

        // The pack, and its images decoded here, off the main thread (a big pack takes seconds).
        var (pack, images) = await System.Threading.Tasks.Task.Run(() =>
        {
            var found = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.LoadPackForGame(game, null);
            return (found, found == null ? null : AP_Atlas.Core.PopTracker.PackImages.Use(found));
        });

        if (_ended || !GodotObject.IsInstanceValid(this) || _mapTracker == null)
        {
            images?.Dispose();
            return;
        }

        if (pack != null)
        {
            _packImages?.Dispose(); // a pack this slot loaded before
            _packImages = images;
            Pack = pack;
            RebuildPackIndex();
            StartPackScripts();
            AppendDebugLog($"[MapTracker] Loaded pack '{pack.Manifest?.Name}' for {game}.");
            // First use of a pack (or a new version): let the Pack Doctor check it in the background.
            AP_Atlas.Core.Async.Fire(AP_Atlas.Core.PopTracker.PackDoctorService.CheckAsync(pack), "checking a map pack");
            RaiseStateChanged();
        }
        else
        {
            AppendDebugLog($"[MapTracker] No installed map pack matches '{game}'.");
        }
    }

    // =====================================================================
    // Game names (the server's data package) and the pack's index
    // =====================================================================

    /// <summary>Each game's data checksum from the server's RoomInfo.</summary>
    public IReadOnlyDictionary<string, string> ServerDataChecksums => Model.DataChecksums;

    public string ServerChecksumFor(string game) =>
        game != null && ServerDataChecksums != null && ServerDataChecksums.TryGetValue(game, out var c) && !string.IsNullOrEmpty(c) ? c : null;

    /// <summary>
    /// Puts this game's data package (item and location name tables) in Atlas's name tables, so the Pack Doctor and other
    /// slots can use it offline later. Connecting has usually stored it already (once per game version); only when it
    /// hasn't is the server asked, once per connection.
    /// </summary>
    private void RequestGameNames()
    {
        if (Session == null || string.IsNullOrEmpty(Game)) return;
        string game = Game;
        // Names are fixed by the checksum: when the stored copy has the same one, there's nothing to do.
        string checksum = ServerChecksumFor(game);
        var stored = AP_Atlas.Core.PopTracker.GameNames.Server(game);
        if (checksum != null && stored != null && stored.Version == checksum && stored.Locations.Count > 0)
        {
            AppendDebugLog($"Names for {game} are already stored for checksum {checksum[..Math.Min(8, checksum.Length)]}; not downloading them again.");
            return;
        }
        if (checksum == null)
        {
            AskServerForGameNames();
            return;
        }
        // Read off the main thread (a large game's names take a moment), then use them, or ask the server if they're missing.
        var dataPackages = DataManager.DataPackages;
        AP_Atlas.Core.Async.Then(Task.Run(() => dataPackages.TryGet(game, checksum, out var data) ? data : null), data => AP_Atlas.UI.Ui.Defer(null, () =>
        {
            if (data != null) UseGameNames(game, data);
            else if (GodotObject.IsInstanceValid(this) && Session != null) AskServerForGameNames();
        }), "reading the stored game names");
    }

    private void AskServerForGameNames()
    {
        Session.Socket.PacketReceived += OnDataPackagePacket;
        AP_Atlas.Core.Async.Fire(Session.Socket.SendPacketAsync(new GetDataPackagePacket { Games = new[] { Game } }), "asking the server for game names", tellUser: false);
    }

    private void OnDataPackagePacket(ArchipelagoPacketBase packet)
    {
        if (packet is not DataPackagePacket dp || dp.DataPackage?.Games == null) return;
        string game = Game;
        if (!dp.DataPackage.Games.TryGetValue(game, out var data) || data.ItemLookup == null) return;
        Session.Socket.PacketReceived -= OnDataPackagePacket;
        AP_Atlas.UI.Ui.Defer(null, () => UseGameNames(game, data));
    }

    /// <summary>
    /// Keeps a game's names in the name tables (saved for offline use) and, while this slot is open, pairs its map pack
    /// with them. Main thread.
    /// </summary>
    private void UseGameNames(string game, GameData data)
    {
        AP_Atlas.Core.PopTracker.GameNames.Store(new AP_Atlas.Core.PopTracker.GameNameTable
        {
            Game = game,
            Source = AP_Atlas.Core.PopTracker.GameNames.ServerSource,
            Fetched = DateTime.Now,
            Version = data.Checksum ?? "",
            Items = new Dictionary<string, long>(data.ItemLookup),
            Locations = new Dictionary<string, long>(data.LocationLookup ?? new Dictionary<string, long>())
        });
        if (!GodotObject.IsInstanceValid(this)) return;
        RebuildPackIndex();
        // The server's names are the most accurate; re-check the pack against them.
        if (Pack != null) AP_Atlas.Core.Async.Fire(AP_Atlas.Core.PopTracker.PackDoctorService.CheckAsync(Pack), "checking a map pack");
    }

    /// <summary>The pairing of the loaded pack with this slot's locations and items.</summary>
    public AP_Atlas.Core.PopTracker.PackIndex PackIndex { get; private set; }

    /// <summary>
    /// (Re)pairs the pack with this slot: location names from the session, item names from the engine's pool,
    /// received items and the pack's mapped ids. Rebuilt when the pool arrives or the user fixes the pack.
    /// </summary>
    public void RebuildPackIndex()
    {
        if (Pack == null || Session == null) return;
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Pair map pack with slot");
        var locationNames = new Dictionary<long, string>();
        foreach (var id in Session.Locations.AllLocations) locationNames[id] = Session.Locations.GetLocationNameFromId(id) ?? "";
        var itemNames = new Dictionary<long, string>();
        // The game's full item table (server data package, else the local install), then what this slot has seen.
        var table = AP_Atlas.Core.PopTracker.GameNames.Best(Game);
        if (table != null) foreach (var kv in table.ItemsById()) itemNames[kv.Key] = kv.Value;
        foreach (var p in Model.Logic.Engine.LastItemPool ?? new List<WorldItemInfo>()) if (p.Id != 0 && p.Name != null) itemNames[p.Id] = p.Name;
        foreach (var i in Session.Items.AllItemsReceived) if (i.ItemName != null) itemNames[i.ItemId] = i.ItemName;
        foreach (var id in Pack.ItemMapping.Keys)
        {
            if (itemNames.ContainsKey(id)) continue;
            string n = Session.Items.GetItemName(id, Game);
            if (!string.IsNullOrEmpty(n)) itemNames[id] = n;
        }
        // The user's Pack Doctor fixes apply to a light copy; the author's pack stays as loaded.
        EffectivePack = AP_Atlas.Core.PopTracker.PackFixes.Effective(Pack);
        PackIndex = new AP_Atlas.Core.PopTracker.PackIndex(EffectivePack, locationNames, itemNames);
        AP_Atlas.Core.PopTracker.PackFixes.ApplyLinks(PackIndex);
        _mapTracker?.LoadPack(EffectivePack, PackIndex);
        _progressionTracker?.SetPack(EffectivePack, PackIndex);
        StateChanged?.Invoke();
    }

    // =====================================================================
    // The pack's own scripts (item states and seed-setting indicators, exactly as the pack defines them)
    // =====================================================================

    /// <summary>The options and data the server sent this slot at login.</summary>
    public Dictionary<string, object> SlotDataSnapshot => _slotData;
    /// <summary>The pack's scripts running for this slot (null until ready, or if the pack has none).</summary>
    public AP_Atlas.Core.PopTracker.PackScriptHost PackScripts => _scripts?.Scripts;

    // One per slot, so a restart queues behind work still running on the previous scripts.
    private AP_Atlas.Core.PopTracker.PackScriptRunner _scripts;
    private int _scriptItemsQueued;
    private readonly HashSet<long> _scriptLocationsQueued = new HashSet<long>();

    /// <summary>Starts the pack's scripts: init, the clear handler with this slot's options, then every item and check so far.</summary>
    private void StartPackScripts()
    {
        if (Pack == null || Session == null) return;
        var slotData = Newtonsoft.Json.Linq.JToken.FromObject(_slotData ?? new Dictionary<string, object>());
        var items = Session.Items.AllItemsReceived.Select(i => (i.ItemId, i.ItemName, Player: i.Player?.Slot ?? 0)).ToList();
        var locations = Session.Locations.AllLocationsChecked.Select(id => (Id: id, Name: Session.Locations.GetLocationNameFromId(id) ?? "")).ToList();
        _scriptItemsQueued = items.Count;
        _scriptLocationsQueued.Clear();
        foreach (var l in locations) _scriptLocationsQueued.Add(l.Id);
        _scripts ??= new AP_Atlas.Core.PopTracker.PackScriptRunner(work => AP_Atlas.UI.Ui.Defer(this, () => work()),
            message => AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] {message}"), PackScriptsStopped);
        _scripts.Start(Pack, PlayerSlot, Team, slotData, items, locations, (host, ms) =>
        {
            if (host == null) { AppendDebugLog($"[MapTracker] The pack has no scripts/init.lua; Key Items use the pack's item mappings only."); return; }
            AppendDebugLog($"[MapTracker] Ran the pack's scripts in {ms} ms: {host.Errors.Count} error(s)" +
                           (host.UnsupportedApis.Count > 0 ? $", unsupported APIs: {string.Join(", ", host.UnsupportedApis)}" : ""));
            foreach (var e in host.Errors) AppendDebugLog("[MapTracker] Pack script error: " + e);
            UpdateKeyItemsUI();
            StateChanged?.Invoke();
        });
    }
    /// <summary>The pack's scripts were stopped (a piece of their work went over its limits): the log and Key Items say why.</summary>
    private void PackScriptsStopped(string reason)
    {
        AppendDebugLog($"[MapTracker] The pack's scripts were stopped: {reason}. Key Items use the pack's item mappings only.");
        AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] The map pack's scripts were stopped: {reason}.");
        UpdateKeyItemsUI();
        StateChanged?.Invoke();
    }
    /// <summary>Feeds items received since the scripts last saw the list.</summary>
    private void FeedNewItemsToScripts()
    {
        if (Pack == null || Session == null || _scripts == null) return;
        var all = Session.Items.AllItemsReceived;
        if (all.Count <= _scriptItemsQueued) return;
        var fresh = all.Skip(_scriptItemsQueued).Select((i, n) => (Index: _scriptItemsQueued + n, i.ItemId, i.ItemName, Player: i.Player?.Slot ?? 0)).ToList();
        _scriptItemsQueued = all.Count;
        _scripts.FeedItems(fresh, UpdateKeyItemsUI);
    }
    private void FeedNewChecksToScripts()
    {
        if (Pack == null || Session == null || _scripts == null) return;
        var fresh = Session.Locations.AllLocationsChecked.Where(id => _scriptLocationsQueued.Add(id))
            .Select(id => (Id: id, Name: Session.Locations.GetLocationNameFromId(id) ?? "")).ToList();
        if (fresh.Count == 0) return;
        _scripts.FeedLocations(fresh);
    }
    /// <summary>A tile's state from the pack's scripts, or null when they aren't running.</summary>
    public AP_Atlas.Core.PopTracker.PackScriptHost.TileState ScriptStateOf(string code)
    {
        var host = PackScripts;
        if (host == null || !host.Cleared) return null;
        // StateOf reads the scripts' objects; serialize with the queue by only reading when it's idle.
        if (!_scripts.Idle) return null;
        return host.StateOf(code);
    }
    /// <summary>The seed's settings as the pack shows them (its settings grid plus anything set from slot data).</summary>
    public List<AP_Atlas.Core.PopTracker.PackScriptHost.SettingInfo> SeedSettings()
    {
        var host = PackScripts;
        if (host == null || !host.Cleared || !_scripts.Idle) return new List<AP_Atlas.Core.PopTracker.PackScriptHost.SettingInfo>();
        var settingCodes = (EffectivePack ?? Pack).ItemGridGroups.Where(g => g.LooksLikeSettings).SelectMany(g => g.Rows.SelectMany(r => r));
        return host.Settings(settingCodes);
    }
    /// <summary>The loaded pack with the user's fixes applied (what the views show).</summary>
    public AP_Atlas.Core.PopTracker.LoadedPack EffectivePack { get; private set; }

    private void OnPackFixesChanged(string packKey)
    {
        if (Pack != null && AP_Atlas.Core.PopTracker.PackFixes.KeyFor(Pack) == packKey) RebuildPackIndex();
    }
}
