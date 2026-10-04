using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>
    /// Runs a PopTracker pack's own Lua scripts (init.lua → autotracking → clear/item/location handlers) in a
    /// sandbox, so tile states (items and seed-setting indicators) come out exactly as the pack defines them, for
    /// any game. The sandbox has no file, network or OS access; scripts can only read the pack's own files.
    /// Every PopTracker API the scripts touch is emulated; anything unknown is a harmless no-op that's recorded,
    /// so the Pack Doctor can report it. Not thread-safe: call from one thread at a time.
    /// </summary>
    public sealed class PackScriptHost
    {
        /// <summary>A tile's state as the pack's scripts left it.</summary>
        public sealed class TileState
        {
            public bool Active;
            public int Stage;
            public int Count;
            /// <summary>The scripts changed this object (otherwise the state is just the default).</summary>
            public bool Touched;
        }

        public LoadedPack Pack { get; }
        public string Variant { get; }
        public bool Initialized { get; private set; }
        public bool Cleared { get; private set; }
        public bool HasClearHandler => _clearHandlers.Count > 0;
        public bool HasItemHandler => _itemHandlers.Count > 0;

        /// <summary>Script errors (file and message), for the Doctor and the debug log.</summary>
        public List<string> Errors { get; } = new List<string>();

        /// <summary>PopTracker APIs the scripts used that Atlas doesn't emulate (they did nothing).</summary>
        public HashSet<string> UnsupportedApis { get; } = new HashSet<string>();

        /// <summary>Codes the scripts looked up that the pack doesn't define.</summary>
        public HashSet<string> UnknownCodes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The scripts' print() output (capped).</summary>
        public List<string> Log { get; } = new List<string>();

        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Script _script;

        private readonly List<DynValue> _clearHandlers = new List<DynValue>();
        private readonly List<DynValue> _itemHandlers = new List<DynValue>();
        private readonly List<DynValue> _locationHandlers = new List<DynValue>();
        private readonly Dictionary<string, List<DynValue>> _watches = new Dictionary<string, List<DynValue>>(StringComparer.OrdinalIgnoreCase);

        // Pack items (by definition) and the Lua tables standing in for them.
        private sealed class PackObject
        {
            public PopTrackerItem Def;
            public Dictionary<string, DynValue> Fields = new Dictionary<string, DynValue>();
            public bool Touched;
            public Table Proxy;
        }
        private readonly Dictionary<PopTrackerItem, PackObject> _objects = new Dictionary<PopTrackerItem, PackObject>();
        private readonly Dictionary<string, Table> _locationObjects = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Table> _luaItems = new List<Table>();
        private Table _archipelago;
        private int _watchDepth;

        private PackScriptHost(LoadedPack pack, string variant)
        {
            Pack = pack;
            Variant = variant;
        }

        // =====================================================================
        // Loading
        // =====================================================================

        /// <summary>Reads the pack's scripts and data files from its zip. Null if the pack has no init.lua.</summary>
        public static PackScriptHost Load(LoadedPack pack)
        {
            if (pack == null || string.IsNullOrEmpty(pack.SourcePath) || !File.Exists(pack.SourcePath)) return null;
            string variant = DefaultVariant(pack.Manifest);
            var host = new PackScriptHost(pack, variant);
            using (var zip = ZipFile.OpenRead(pack.SourcePath))
            {
                string root = pack.RootPrefix ?? "";
                foreach (var e in zip.Entries)
                {
                    if (!e.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = e.FullName.Substring(root.Length).TrimStart('/');
                    if (!(rel.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase))) continue;
                    using var s = e.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    host._files[rel] = DecodeText(ms.ToArray());
                }
            }
            return host.ResolveFile("scripts/init.lua") == null ? null : host;
        }

        private static string DefaultVariant(PopTrackerManifest m)
        {
            if (m?.Variants == null || !m.Variants.Properties().Any()) return "standard";
            return m.Variants.ContainsKey("standard") ? "standard" : m.Variants.Properties().First().Name;
        }

        /// <summary>UTF-8, or Windows-1252-ish text (some packs save scripts that way).</summary>
        private static string DecodeText(byte[] bytes)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
        }

        /// <summary>A pack file as PopTracker would see it: the variant's override first, then the base file.</summary>
        private string ResolveFile(string path)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            if (!string.IsNullOrEmpty(Variant) && _files.TryGetValue(Variant + "/" + path, out var v)) return v;
            return _files.TryGetValue(path, out var text) ? text : null;
        }

        private sealed class PackLoader : ScriptLoaderBase
        {
            private readonly PackScriptHost _host;
            public PackLoader(PackScriptHost host) { _host = host; ModulePaths = new[] { "?.lua", "?", "scripts/?.lua" }; }
            public override object LoadFile(string file, Table globalContext) => _host.ResolveFile(file) ?? throw new ScriptRuntimeException($"file '{file}' isn't in the pack");
            public override bool ScriptFileExists(string name) => _host.ResolveFile(name) != null;
            public override string ResolveModuleName(string modname, Table globalContext) =>
                base.ResolveModuleName(modname.Replace('.', '/'), globalContext) ?? base.ResolveModuleName(modname, globalContext);
        }

        // =====================================================================
        // Running
        // =====================================================================

        /// <summary>Runs scripts/init.lua (which loads the rest, including the autotracking handlers).</summary>
        public bool Initialize()
        {
            if (Initialized) return Errors.Count == 0;
            Initialized = true;
            // Sandbox: no io/os (except clock/time), no file loading except the pack's own files via require/loadfile.
            _script = new Script(CoreModules.Preset_SoftSandbox | CoreModules.LoadMethods);
            _script.Options.ScriptLoader = new PackLoader(this);
            _script.Options.DebugPrint = s => { if (Log.Count < 500) Log.Add(s); };
            InstallApi();
            return Run("scripts/init.lua", () => _script.DoString(ResolveFile("scripts/init.lua"), null, "scripts/init.lua"));
        }

        /// <summary>Calls the clear handlers with the slot's data, as PopTracker does on connect.</summary>
        public void Clear(int playerNumber, int teamNumber, JToken slotData)
        {
            if (_script == null) return;
            _archipelago["PlayerNumber"] = playerNumber;
            _archipelago["TeamNumber"] = teamNumber;
            // Read-tracking tables: each option the script reads is noted, and credited to the next tile it sets.
            var data = Tracked(slotData ?? new JObject(), "");
            SlotData = slotData;
            _inClear = true;
            try
            {
                foreach (var h in _clearHandlers)
                {
                    _pendingReads.Clear();
                    Run("clear handler", () => _script.Call(h, data));
                }
            }
            finally { _inClear = false; }
            _pendingReads.Clear();
            Cleared = true;
        }

        /// <summary>Feeds received items (index is the item's position in the received list, from 1).</summary>
        public void ApplyItem(int index, long itemId, string itemName, int fromPlayer)
        {
            if (_script == null) return;
            foreach (var h in _itemHandlers) Run("item handler", () => _script.Call(h, index, itemId, itemName ?? "", fromPlayer));
        }

        public void ApplyLocation(long locationId, string locationName)
        {
            if (_script == null) return;
            foreach (var h in _locationHandlers) Run("location handler", () => _script.Call(h, locationId, locationName ?? ""));
        }

        private bool Run(string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (InterpreterException ex)
            {
                AddError($"{what}: {ex.DecoratedMessage ?? ex.Message}");
            }
            catch (Exception ex)
            {
                AddError($"{what}: {ex.Message}");
            }
            return false;
        }

        private void AddError(string message)
        {
            if (Errors.Count < 50 && !Errors.Contains(message)) Errors.Add(message);
        }

        // =====================================================================
        // Reading results
        // =====================================================================

        /// <summary>The state of the object a code names, or null if no pack object provides it.</summary>
        public TileState StateOf(string code)
        {
            if (_script == null || string.IsNullOrEmpty(code)) return null;
            if (Pack.ItemsByCode.TryGetValue(code, out var def))
            {
                var o = Obj(def);
                return new TileState
                {
                    Active = o.Fields.TryGetValue("Active", out var a) && a.CastToBool(),
                    Stage = o.Fields.TryGetValue("CurrentStage", out var s) && s.Type == DataType.Number ? (int)s.Number : 0,
                    Count = o.Fields.TryGetValue("AcquiredCount", out var c) && c.Type == DataType.Number ? (int)c.Number : 0,
                    Touched = o.Touched
                };
            }
            var lua = FindLuaItem(code);
            if (lua == null) return null;
            var provided = CallField(lua, "ProvidesCodeFunc", DynValue.NewTable(lua), DynValue.NewString(code));
            int n = provided.Type == DataType.Number ? (int)provided.Number : provided.Type == DataType.Boolean && provided.Boolean ? 1 : 0;
            return new TileState { Active = n > 0, Stage = n, Count = n, Touched = true };
        }

        // =====================================================================
        // PopTracker API emulation
        // =====================================================================

        private void InstallApi()
        {
            var g = _script.Globals;
            g["PopVersion"] = "0.32.0";
            g["AccessibilityLevel"] = MakeTable(new Dictionary<string, object>
            {
                ["None"] = 0,
                ["Partial"] = 1,
                ["Inspect"] = 3,
                ["SequenceBreak"] = 5,
                ["Normal"] = 6,
                ["Cleared"] = 7
            });

            // Tracker
            var tracker = ApiTable("Tracker");
            tracker["ActiveVariantUID"] = Variant;
            tracker["BulkUpdate"] = false;
            tracker["AllowDeferredLogicUpdate"] = false;
            foreach (var name in new[] { "AddItems", "AddMaps", "AddLocations", "AddLayouts", "UiHint" }) tracker[name] = NoOp();
            tracker["FindObjectForCode"] = Callback(args => FindObjectForCode(args.Count > 1 ? args[1].CastToString() : null));
            tracker["ProviderCountForCode"] = Callback(args => DynValue.NewNumber(ProviderCount(args.Count > 1 ? args[1].CastToString() : null)));
            g["Tracker"] = tracker;

            // ScriptHost
            var host = ApiTable("ScriptHost");
            host["LoadScript"] = Callback(args =>
            {
                string path = args.Count > 1 ? args[1].CastToString() : null;
                string code = path == null ? null : ResolveFile(path);
                if (code == null) { AddError($"LoadScript: '{path}' isn't in the pack"); return DynValue.False; }
                return Run(path, () => _script.DoString(code, null, path)) ? DynValue.True : DynValue.False;
            });
            host["CreateLuaItem"] = Callback(_ => DynValue.NewTable(CreateLuaItem()));
            host["AddWatchForCode"] = Callback(args =>
            {
                if (args.Count > 3 && args[3].Type == DataType.Function)
                {
                    string code = args[2].CastToString() ?? "*";
                    if (!_watches.TryGetValue(code, out var list)) _watches[code] = list = new List<DynValue>();
                    list.Add(args[3]);
                }
                return DynValue.True;
            });
            foreach (var name in new[] { "RemoveWatchForCode", "AddOnFrameHandler", "RemoveOnFrameHandler", "AddMemoryWatch", "RemoveMemoryWatch", "AddOnLocationSectionChangedHandler", "RemoveOnLocationSectionHandler" })
                host[name] = NoOp(DynValue.True);
            g["ScriptHost"] = host;

            // Archipelago
            _archipelago = ApiTable("Archipelago");
            _archipelago["PlayerNumber"] = -1;
            _archipelago["TeamNumber"] = 0;
            _archipelago["AddClearHandler"] = Callback(args => AddHandler(_clearHandlers, args));
            _archipelago["AddItemHandler"] = Callback(args => AddHandler(_itemHandlers, args));
            _archipelago["AddLocationHandler"] = Callback(args => AddHandler(_locationHandlers, args));
            foreach (var name in new[] { "AddScoutHandler", "AddBouncedHandler", "AddRetrievedHandler", "AddSetReplyHandler", "SetNotify", "Get", "LocationChecks", "LocationScouts", "StatusUpdate", "Bounce", "Set" })
                _archipelago[name] = NoOp(DynValue.True);
            g["Archipelago"] = _archipelago;

            // AutoTracker: "connected" for AP, nothing else.
            var auto = ApiTable("AutoTracker");
            auto["GetConnectionState"] = Callback(args => DynValue.NewNumber(args.Count > 1 && (args[1].CastToString() ?? "").Equals("AP", StringComparison.OrdinalIgnoreCase) ? 3 : 0));
            g["AutoTracker"] = auto;

            var images = ApiTable("ImageReference");
            images["FromPackRelativePath"] = Callback(args => args.Count > 1 ? args[1] : DynValue.Nil);
            images["FromImageReference"] = Callback(args => args.Count > 1 ? args[1] : DynValue.Nil);
            g["ImageReference"] = images;

            g["LayoutManager"] = ApiTable("LayoutManager");
            g["JsonConvert"] = ApiTable("JsonConvert");
        }

        /// <summary>A table whose unknown members are no-op functions, recorded as unsupported.</summary>
        private Table ApiTable(string name)
        {
            var t = new Table(_script);
            var meta = new Table(_script);
            meta["__index"] = DynValue.NewCallback((ctx, args) =>
            {
                string key = args.Count > 1 ? args[1].CastToString() : "?";
                UnsupportedApis.Add($"{name}.{key}");
                return NoOp();
            });
            t.MetaTable = meta;
            return t;
        }

        private DynValue Callback(Func<CallbackArguments, DynValue> f) => DynValue.NewCallback((ctx, args) => f(args));

        private DynValue NoOp(DynValue result = null) => DynValue.NewCallback((ctx, args) => result ?? DynValue.Nil);

        private Table MakeTable(Dictionary<string, object> values)
        {
            var t = new Table(_script);
            foreach (var kv in values) t[kv.Key] = kv.Value;
            return t;
        }

        private static DynValue AddHandler(List<DynValue> list, CallbackArguments args)
        {
            // Archipelago:AddXHandler(name, fn): colon call, so args are (self, name, fn).
            var fn = Enumerable.Range(0, args.Count).Select(i => args[i]).LastOrDefault(a => a.Type == DataType.Function || a.Type == DataType.ClrFunction);
            if (fn != null) list.Add(fn);
            return DynValue.True;
        }

        // ---- Objects ----

        private DynValue FindObjectForCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return DynValue.Nil;
            if (code.StartsWith("@"))
            {
                if (!_locationObjects.TryGetValue(code, out var loc))
                {
                    // Locations and sections: scripts set AvailableChestCount etc. Plain tables are enough.
                    loc = new Table(_script);
                    int count = SectionItemCount(code);
                    loc["ChestCount"] = count;
                    loc["AvailableChestCount"] = count;
                    loc["AccessibilityLevel"] = 6;
                    loc["Owner"] = new Table(_script);
                    _locationObjects[code] = loc;
                }
                return DynValue.NewTable(loc);
            }
            if (Pack.ItemsByCode.TryGetValue(code, out var def)) return DynValue.NewTable(Obj(def).Proxy);
            var lua = FindLuaItem(code);
            if (lua != null) return DynValue.NewTable(lua);
            UnknownCodes.Add(code);
            return DynValue.Nil;
        }

        private int SectionItemCount(string path)
        {
            var parts = path.TrimStart('@').Split('/');
            if (parts.Length < 2) return 1;
            string pinPath = string.Join("/", parts.Take(parts.Length - 1));
            var pin = Pack.Locations.FirstOrDefault(l => string.Equals(l.FullPath, pinPath, StringComparison.OrdinalIgnoreCase));
            var sec = pin?.Sections?.FirstOrDefault(s => string.Equals(s.Name, parts[parts.Length - 1], StringComparison.OrdinalIgnoreCase));
            return Math.Max(1, sec?.ItemCount ?? pin?.ItemCount ?? 1);
        }

        private PackObject Obj(PopTrackerItem def)
        {
            if (_objects.TryGetValue(def, out var o)) return o;
            o = new PackObject { Def = def };
            o.Fields["Name"] = DynValue.NewString(def.Name ?? "");
            o.Fields["Type"] = DynValue.NewString(def.Type ?? "toggle");
            o.Fields["Active"] = DynValue.False;
            o.Fields["CurrentStage"] = DynValue.NewNumber(0);
            o.Fields["AcquiredCount"] = DynValue.NewNumber(0);
            o.Fields["MinCount"] = DynValue.NewNumber(0);
            o.Fields["MaxCount"] = DynValue.NewNumber(def.MaxQuantity > 1 ? def.MaxQuantity : int.MaxValue);
            o.Fields["Increment"] = DynValue.NewNumber(1);
            o.Fields["Decrement"] = DynValue.NewNumber(1);
            o.Fields["IconMods"] = DynValue.NewString("");
            o.Fields["BadgeText"] = DynValue.Nil;

            var proxy = new Table(_script);
            var meta = new Table(_script);
            var obj = o;
            meta["__index"] = DynValue.NewCallback((ctx, args) =>
            {
                string key = args[1].CastToString();
                if (key == "Set") return DynValue.NewCallback((c, a) => { SetField(obj, a[1].CastToString(), a[2]); return DynValue.Nil; });
                if (key == "Get") return DynValue.NewCallback((c, a) => obj.Fields.TryGetValue(a[1].CastToString() ?? "", out var v) ? v : DynValue.Nil);
                return key != null && obj.Fields.TryGetValue(key, out var value) ? value : DynValue.Nil;
            });
            meta["__newindex"] = DynValue.NewCallback((ctx, args) =>
            {
                SetField(obj, args[1].CastToString(), args[2]);
                return DynValue.Nil;
            });
            proxy.MetaTable = meta;
            o.Proxy = proxy;
            _objects[def] = o;
            return o;
        }

        /// <summary>Sets an item property with PopTracker's rules (stage and count limits), then runs code watches.</summary>
        private void SetField(PackObject o, string key, DynValue value)
        {
            if (key == null) return;
            switch (key)
            {
                case "CurrentStage" when value.Type == DataType.Number:
                    int stages = o.Def.Stages?.Count ?? 0;
                    int stage = (int)value.Number;
                    if (stages > 0) stage = Math.Clamp(stage, 0, stages - 1);
                    value = DynValue.NewNumber(Math.Max(0, stage));
                    break;
                case "AcquiredCount" when value.Type == DataType.Number:
                    double min = o.Fields["MinCount"].Type == DataType.Number ? o.Fields["MinCount"].Number : 0;
                    double max = o.Fields["MaxCount"].Type == DataType.Number ? o.Fields["MaxCount"].Number : double.MaxValue;
                    value = DynValue.NewNumber(Math.Clamp(value.Number, min, max));
                    break;
                case "Active":
                    value = DynValue.NewBoolean(value.CastToBool());
                    break;
            }
            o.Fields[key] = value;
            if (key is "Active" or "CurrentStage" or "AcquiredCount")
            {
                o.Touched = true;
                if (_inClear) _setInClear.Add(o.Def);
                // The most recent slot_data read decided this (e.g. "if options.x == 1 then obj.Active = true").
                if (_pendingReads.Count > 0)
                {
                    _optionByObject[o.Def] = _pendingReads[_pendingReads.Count - 1];
                    _pendingReads.Clear();
                }
            }
            FireWatches(o.Def.GetCodes());
        }

        private void FireWatches(IEnumerable<string> codes)
        {
            if (_watches.Count == 0 || _watchDepth > 8) return;
            _watchDepth++;
            try
            {
                foreach (var code in codes.Append("*"))
                {
                    if (!_watches.TryGetValue(code, out var list)) continue;
                    foreach (var fn in list.ToList()) Run($"watch for {code}", () => _script.Call(fn, code));
                }
            }
            finally { _watchDepth--; }
        }

        private int ProviderCount(string code)
        {
            if (string.IsNullOrEmpty(code)) return 0;
            if (Pack.ItemsByCode.TryGetValue(code, out var def))
            {
                var o = Obj(def);
                string type = def.Type ?? "toggle";
                bool active = o.Fields["Active"].CastToBool();
                if (type == "consumable") return (int)o.Fields["AcquiredCount"].Number;
                if (type.StartsWith("progressive") && def.Stages != null && def.Stages.Count > 0)
                {
                    // A stage provides its own codes; earlier stages are included unless the pack says otherwise.
                    int stage = (int)o.Fields["CurrentStage"].Number;
                    for (int i = 0; i < def.Stages.Count; i++)
                    {
                        var codes = (def.Stages[i].Codes ?? "").Split(',').Select(c => c.Trim());
                        if (codes.Contains(code, StringComparer.OrdinalIgnoreCase)) return active && stage >= i ? 1 : 0;
                    }
                    return active ? 1 : 0;
                }
                return active ? 1 : 0;
            }
            var lua = FindLuaItem(code);
            if (lua == null) return 0;
            var r = CallField(lua, "ProvidesCodeFunc", DynValue.NewTable(lua), DynValue.NewString(code));
            return r.Type == DataType.Number ? (int)r.Number : r.CastToBool() ? 1 : 0;
        }

        // ---- Custom Lua items (ScriptHost:CreateLuaItem) ----

        private Table CreateLuaItem()
        {
            var item = new Table(_script);
            var props = new Table(_script);
            var meta = new Table(_script);
            var methods = new Table(_script);
            methods["Set"] = DynValue.NewCallback((ctx, args) =>
            {
                var self = args[0].Table;
                var key = args[1];
                var value = args.Count > 2 ? args[2] : DynValue.Nil;
                props.Set(key, value);
                CallField(self, "PropertyChangedFunc", DynValue.NewTable(self), key, value);
                return DynValue.Nil;
            });
            methods["Get"] = DynValue.NewCallback((ctx, args) => props.Get(args[1]));
            meta["__index"] = DynValue.NewTable(methods);
            item.MetaTable = meta;
            _luaItems.Add(item);
            return item;
        }

        private Table FindLuaItem(string code)
        {
            foreach (var item in _luaItems)
            {
                var r = CallField(item, "CanProvideCodeFunc", DynValue.NewTable(item), DynValue.NewString(code));
                if (r.CastToBool()) return item;
            }
            return null;
        }

        private DynValue CallField(Table t, string field, params DynValue[] args)
        {
            var fn = t.RawGet(field);
            if (fn == null || (fn.Type != DataType.Function && fn.Type != DataType.ClrFunction)) return DynValue.Nil;
            try { return _script.Call(fn, args); }
            catch (InterpreterException ex) { AddError($"{field}: {ex.DecoratedMessage ?? ex.Message}"); return DynValue.Nil; }
        }

        // ---- Slot data as read-tracking tables ----

        private readonly List<string> _pendingReads = new List<string>();
        private readonly Dictionary<PopTrackerItem, string> _optionByObject = new Dictionary<PopTrackerItem, string>();

        /// <summary>
        /// A Lua view of JSON whose reads are recorded with their path ("options.goal_condition"). Supports
        /// indexing, pairs/ipairs (via __pairs/__ipairs) and # (via __len); writes go to an overlay.
        /// </summary>
        private DynValue Tracked(JToken token, string path)
        {
            if (token is not JObject && token is not JArray) return ToLua(token);
            var proxy = new Table(_script);
            var meta = new Table(_script);
            var children = new Dictionary<string, DynValue>();
            var overlay = new Dictionary<string, DynValue>();

            List<(DynValue Key, string Name, JToken Value)> Entries()
            {
                var list = new List<(DynValue, string, JToken)>();
                if (token is JObject o) foreach (var p in o.Properties()) list.Add((DynValue.NewString(p.Name), p.Name, p.Value));
                else { int i = 1; foreach (var v in (JArray)token) { list.Add((DynValue.NewNumber(i), i.ToString(), v)); i++; } }
                return list;
            }

            DynValue Child(string name, JToken value)
            {
                if (overlay.TryGetValue(name, out var o)) return o;
                if (!children.TryGetValue(name, out var c)) children[name] = c = Tracked(value, path.Length == 0 ? name : path + "." + name);
                return c;
            }

            meta["__index"] = DynValue.NewCallback((ctx, args) =>
            {
                var key = args[1];
                string name = key.Type == DataType.Number ? ((long)key.Number).ToString() : key.CastToString();
                if (name == null) return DynValue.Nil;
                JToken value = token is JObject jo ? jo[name] : (key.Type == DataType.Number && token is JArray ja && key.Number >= 1 && key.Number <= ja.Count ? ja[(int)key.Number - 1] : null);
                string full = path.Length == 0 ? name : path + "." + name;
                if (_pendingReads.Count < 200) _pendingReads.Add(full);
                return value == null && !overlay.ContainsKey(name) ? DynValue.Nil : Child(name, value);
            });
            meta["__newindex"] = DynValue.NewCallback((ctx, args) =>
            {
                string name = args[1].Type == DataType.Number ? ((long)args[1].Number).ToString() : args[1].CastToString();
                if (name != null) overlay[name] = args[2];
                return DynValue.Nil;
            });
            meta["__len"] = DynValue.NewCallback((ctx, args) => DynValue.NewNumber(token is JArray arr ? arr.Count : 0));
            // pairs/ipairs walk the data without counting as reads (dumping slot_data shouldn't credit anything).
            DynValue Iterator(bool arrayOnly)
            {
                var entries = Entries().Where(e => !arrayOnly || e.Key.Type == DataType.Number).ToList();
                int index = 0;
                var next = DynValue.NewCallback((ctx, args) =>
                {
                    if (index >= entries.Count) return DynValue.Nil;
                    var e = entries[index++];
                    return DynValue.NewTuple(e.Key, Child(e.Name, e.Value));
                });
                return DynValue.NewTuple(next, DynValue.NewTable(proxy), DynValue.Nil);
            }
            meta["__pairs"] = DynValue.NewCallback((ctx, args) => Iterator(false));
            meta["__ipairs"] = DynValue.NewCallback((ctx, args) => Iterator(true));
            proxy.MetaTable = meta;
            return DynValue.NewTable(proxy);
        }

        private bool _inClear;
        private readonly HashSet<PopTrackerItem> _setInClear = new HashSet<PopTrackerItem>();

        /// <summary>The slot data the scripts were given (null before Clear).</summary>
        public JToken SlotData { get; private set; }

        /// <summary>A seed setting as the pack shows it.</summary>
        public sealed class SettingInfo
        {
            public string Code = "";
            public string Name = "";
            public bool On;
            public int Stage;
            /// <summary>The stage's name for multi-stage settings ("Manus"), else null.</summary>
            public string StageName;
            /// <summary>The slot_data option it reflects ("options.goal_condition"), when known.</summary>
            public string OptionPath;
            /// <summary>That option's value in the slot data, or null if the slot data doesn't have it.</summary>
            public JToken OptionValue;
            public bool OptionMissing => OptionPath != null && OptionValue == null;
        }

        /// <summary>
        /// The pack's seed-setting indicators: the given codes (e.g. a settings grid) plus anything the clear
        /// handler set from a slot_data option. Works for any pack, whatever it names its settings.
        /// </summary>
        public List<SettingInfo> Settings(IEnumerable<string> settingCodes)
        {
            var result = new List<SettingInfo>();
            if (_script == null) return result;
            var seen = new HashSet<PopTrackerItem>();
            var codes = (settingCodes ?? Enumerable.Empty<string>()).ToList();
            foreach (var def in _setInClear.Where(d => _optionByObject.ContainsKey(d)))
                codes.Add(def.GetCodes().FirstOrDefault() ?? "");
            foreach (var code in codes)
            {
                if (string.IsNullOrEmpty(code) || !Pack.ItemsByCode.TryGetValue(code, out var def) || !seen.Add(def)) continue;
                var state = StateOf(code);
                string path = OptionPathFor(code);
                int stage = state?.Stage ?? 0;
                result.Add(new SettingInfo
                {
                    Code = code,
                    Name = def.Name ?? code,
                    On = state?.Active == true || (def.Stages != null && def.Stages.Count > 0 && stage > 0),
                    Stage = stage,
                    StageName = def.Stages != null && def.Stages.Count > 1 && stage < def.Stages.Count ? def.Stages[stage].Name : null,
                    OptionPath = path,
                    OptionValue = path == null || SlotData == null ? null : ReadPath(SlotData, path)
                });
            }
            return result;
        }

        /// <summary>The slot_data option the scripts read just before setting the object for a code (from running them).</summary>
        public string OptionPathFor(string code)
        {
            if (code != null && Pack.ItemsByCode.TryGetValue(code, out var def) && _optionByObject.TryGetValue(def, out var path)) return path;
            return OptionPathsByCode().TryGetValue(code ?? "", out var list) && list.Count > 0 ? list[0] : null;
        }

        // ---- JSON → Lua ----

        private DynValue ToLua(JToken token)
        {
            switch (token?.Type)
            {
                case JTokenType.Object:
                    var t = new Table(_script);
                    foreach (var p in ((JObject)token).Properties()) t[p.Name] = ToLua(p.Value);
                    return DynValue.NewTable(t);
                case JTokenType.Array:
                    var a = new Table(_script);
                    int i = 1;
                    foreach (var v in (JArray)token) a[i++] = ToLua(v);
                    return DynValue.NewTable(a);
                case JTokenType.Integer: return DynValue.NewNumber(token.Value<long>());
                case JTokenType.Float: return DynValue.NewNumber(token.Value<double>());
                case JTokenType.Boolean: return DynValue.NewBoolean(token.Value<bool>());
                case JTokenType.String: return DynValue.NewString(token.Value<string>());
                default: return DynValue.Nil;
            }
        }

        // =====================================================================
        // Which slot_data option a code reflects (for labels and validation)
        // =====================================================================

        /// <summary>
        /// The slot_data paths (e.g. "options.enable_weapon_locations") read near each place the scripts set the
        /// object for a code. A best-effort label: the state itself always comes from running the scripts.
        /// </summary>
        public Dictionary<string, List<string>> OptionPathsByCode()
        {
            if (_optionPathCache != null) return _optionPathCache;
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var luaFiles = _files.Where(f => f.Key.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList();

            // Aliases: "local sd_options = slot_data['options']" (or "= SLOT_DATA.options" after "SLOT_DATA = slot_data").
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal) { ["slot_data"] = "" };
            // Whatever the clear handler calls its parameter is slot data too ("function onClear(slotData)").
            foreach (var kv in luaFiles)
            {
                foreach (Match reg in Regex.Matches(kv.Value, @"AddClearHandler\s*\([^,]*,\s*([A-Za-z_]\w*)\s*\)"))
                {
                    foreach (var file in luaFiles)
                    {
                        var def = Regex.Match(file.Value, @"function\s+" + Regex.Escape(reg.Groups[1].Value) + @"\s*\(\s*([A-Za-z_]\w*)");
                        if (def.Success) aliases[def.Groups[1].Value] = "";
                    }
                }
                foreach (Match inline in Regex.Matches(kv.Value, @"AddClearHandler\s*\([^,]*,\s*function\s*\(\s*([A-Za-z_]\w*)"))
                    aliases[inline.Groups[1].Value] = "";
            }
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (var kv in luaFiles)
                {
                    foreach (Match m in Regex.Matches(kv.Value, @"(?:local\s+)?([A-Za-z_]\w*)\s*=\s*([A-Za-z_]\w*)((?:\s*\.\s*[A-Za-z_]\w*|\s*\[\s*[""'][^""']+[""']\s*\])*)\s*(?:\r?\n|;|--|$)"))
                    {
                        string name = m.Groups[1].Value, source = m.Groups[2].Value;
                        if (name == source || !aliases.TryGetValue(source, out var basePath) || aliases.ContainsKey(name)) continue;
                        string rest = NormalizePath(m.Groups[3].Value);
                        aliases[name] = string.Join(".", new[] { basePath, rest }.Where(s => s.Length > 0));
                    }
                }
            }
            var aliasRegex = new Regex(@"\b(" + string.Join("|", aliases.Keys.Select(Regex.Escape)) + @")((?:\s*\.\s*[A-Za-z_]\w*|\s*\[\s*[""'][^""']+[""']\s*\])+)");

            // Every place a code is looked up, with the slot_data reads around it; nearest across all places wins
            // (so a "reset everything" block far from the real condition doesn't decide the label).
            var candidates = new Dictionary<string, List<(int Distance, string Path)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in luaFiles)
            {
                var lines = StripLuaComments(kv.Value).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in Regex.Matches(lines[i], @"FindObjectForCode\(\s*[""']([^""'@][^""']*)[""']\s*\)"))
                    {
                        string code = m.Groups[1].Value;
                        for (int j = Math.Max(0, i - 6); j <= Math.Min(lines.Length - 1, i + 3); j++)
                        {
                            foreach (Match p in aliasRegex.Matches(lines[j]))
                            {
                                string full = string.Join(".", new[] { aliases[p.Groups[1].Value], NormalizePath(p.Groups[2].Value) }.Where(s => s.Length > 0));
                                if (!candidates.TryGetValue(code, out var list)) candidates[code] = list = new List<(int, string)>();
                                list.Add((Math.Abs(j - i) * 2 + (j < i ? 1 : 0), full));
                            }
                        }
                    }
                }
            }
            foreach (var kv in candidates)
                result[kv.Key] = kv.Value.OrderBy(c => c.Distance).Select(c => c.Path).Distinct().ToList();
            _optionPathCache = result;
            return result;
        }

        /// <summary>Removes Lua comments but keeps line breaks, so line numbers stay put.</summary>
        private static string StripLuaComments(string text)
        {
            text = Regex.Replace(text, @"--\[(=*)\[.*?\]\1\]", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
            return Regex.Replace(text, @"--[^\n]*", "");
        }

        private Dictionary<string, List<string>> _optionPathCache;

        private static string NormalizePath(string raw) =>
            string.Join(".", Regex.Matches(raw, @"[A-Za-z_]\w*|[""']([^""']+)[""']").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value));

        /// <summary>Looks a dotted path up in slot data; null if it isn't there.</summary>
        public static JToken ReadPath(JToken slotData, string path)
        {
            JToken cur = slotData;
            foreach (var part in path.Split('.'))
            {
                if (cur is JObject o && o.TryGetValue(part, out var next)) cur = next;
                else return null;
            }
            return cur;
        }
    }
}
