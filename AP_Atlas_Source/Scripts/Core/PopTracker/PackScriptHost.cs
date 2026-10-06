#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.Interpreter.Loaders;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>
    /// Runs a PopTracker pack's own Lua scripts (init.lua → autotracking → clear/item/location handlers) in a
    /// sandbox, so tile states (items and seed-setting indicators) come out exactly as the pack defines them, for
    /// any game. The sandbox has no file, network or OS access; scripts can only read the pack's own files.
    /// Every PopTracker API the scripts touch is emulated; anything unknown is a harmless no-op that's recorded,
    /// so the Pack Doctor can report it. Each piece of the scripts' work runs under limits (<see cref="ScriptLimits"/>):
    /// work that runs away is stopped, and the scripts with it, so a broken pack can't freeze or crash Atlas.
    /// Not thread-safe: call from one thread at a time.
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

        /// <summary>
        /// Why the scripts were stopped for good (null while they run): a piece of their work went over a limit, or broke
        /// the script engine. Stopped scripts do nothing, and tiles fall back to the pack's item mappings.
        /// </summary>
        public string StopReason => _stopReason;
        public bool Stopped => _stopReason != null;
        private volatile string _stopReason;

        /// <summary>The limits each piece of work runs under (set before <see cref="Initialize"/>).</summary>
        public ScriptLimits Limits { get; set; } = ScriptLimits.Default;

        /// <summary>The most any one piece of work has taken so far (each measure on its own), and the work that took the most steps.</summary>
        public (long Steps, long Memory, TimeSpan Time) Peak { get; private set; }
        public string PeakWork { get; private set; }

        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Script _script;
        private readonly Watchdog _watchdog = new Watchdog();
        // The pack's scripts, compiled before the watchdog is attached (see Initialize), or why they don't compile.
        private readonly Dictionary<string, DynValue> _compiled = new Dictionary<string, DynValue>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _compileErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
            if (Initialized) return Errors.Count == 0 && _stopReason == null;
            Initialized = true;
            // Sandbox: no io/os (except clock/time), no file loading except the pack's own files via require/LoadScript. No
            // json or dynamic modules: PopTracker has neither, and both can overflow Atlas's stack in one call (json.serialize
            // on a table that holds itself, dynamic.eval on deeply nested code).
            _script = new Script((CoreModules.Preset_SoftSandbox | CoreModules.LoadMethods) & ~(CoreModules.Json | CoreModules.Dynamic));
            _script.Options.ScriptLoader = new PackLoader(this);
            _script.Options.DebugPrint = s => { if (Log.Count < 500) Log.Add(s); };
            WrapLibrary();
            InstallApi();
            GuardLibrary();
            // Every script is compiled before the watchdog is attached: with a debugger attached, MoonSharp writes out all
            // the code loaded so far each time a file loads (for the debugger to show), which takes seconds for a big pack.
            CompileScripts();
            _script.AttachDebugger(_watchdog);
            bool ran = false;
            Work("init.lua", () => ran = RunScript("scripts/init.lua"));
            return ran && _stopReason == null;
        }

        /// <summary>Calls the clear handlers with the slot's data, as PopTracker does on connect.</summary>
        public void Clear(int playerNumber, int teamNumber, JToken slotData) => Work("the clear handler", () =>
        {
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
                    Run("clear handler", () => CallLua(h, data));
                }
            }
            finally { _inClear = false; }
            _pendingReads.Clear();
            Cleared = true;
        });

        /// <summary>Feeds received items (index is the item's position in the received list, from 1).</summary>
        public void ApplyItem(int index, long itemId, string itemName, int fromPlayer) =>
            Work($"the item handler (for {(string.IsNullOrEmpty(itemName) ? "item " + itemId : itemName)})", () =>
            {
                foreach (var h in _itemHandlers) Run("item handler", () => CallLua(h, index, itemId, itemName ?? "", fromPlayer));
            });

        public void ApplyLocation(long locationId, string locationName) =>
            Work($"the location handler (for {(string.IsNullOrEmpty(locationName) ? "location " + locationId : locationName)})", () =>
            {
                foreach (var h in _locationHandlers) Run("location handler", () => CallLua(h, locationId, locationName ?? ""));
            });

        /// <summary>Runs Lua, recording a Lua error (the scripts carry on). Anything else (a stop) goes on up to <see cref="Work"/>.</summary>
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
            return false;
        }

        /// <summary>
        /// Calls Lua from Atlas's side (a handler, a watch, a custom item's function). A failure that isn't a Lua error means
        /// the interpreter itself failed with its stacks left mid-call: it's marked, so nothing on the way out takes it for a
        /// failure of Atlas's own code and lets the scripts carry on.
        /// </summary>
        private DynValue CallLua(DynValue function, params object[] args)
        {
            try { return _script.Call(function, args); }
            catch (Exception ex) when (ex is not InterpreterException && ex is not ScriptStopped && ex is not InterpreterBroke)
            {
                throw new InterpreterBroke(ex);
            }
        }

        // =====================================================================
        // Compiling
        // =====================================================================

        private void CompileScripts()
        {
            foreach (var file in _files)
            {
                if (!file.Key.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) continue;
                try { _compiled[file.Key] = _script.LoadString(file.Value, null, file.Key); }
                catch (InterpreterException ex) { _compileErrors[file.Key] = ex.DecoratedMessage ?? ex.Message; }
            }
        }

        /// <summary>
        /// A pack script as PopTracker would find it (the variant's override first, then the base file), compiled: null if
        /// it isn't in the pack, or if it doesn't compile (then <paramref name="compileError"/> says why).
        /// </summary>
        private DynValue Compiled(string path, out string compileError)
        {
            compileError = null;
            path = path.Replace('\\', '/').TrimStart('/');
            foreach (var key in string.IsNullOrEmpty(Variant) ? new[] { path } : new[] { Variant + "/" + path, path })
            {
                if (_compiled.TryGetValue(key, out var chunk)) return chunk;
                if (_compileErrors.TryGetValue(key, out compileError)) return null;
            }
            return null;
        }

        /// <summary>Runs one of the pack's scripts, recording why if it can't (not there, doesn't compile, a Lua error).</summary>
        private bool RunScript(string path)
        {
            var chunk = Compiled(path, out string compileError);
            if (chunk == null)
            {
                AddError(compileError != null ? $"{path}: {compileError}" : $"LoadScript: '{path}' isn't in the pack");
                return false;
            }
            return Run(path, () => CallLua(chunk));
        }

        // =====================================================================
        // Limits
        // =====================================================================

        /// <summary>
        /// How much one piece of the scripts' work (running init.lua, the clear handlers, one item, one check, one read) may
        /// do before the scripts are stopped. Far above what real packs need: across the 40-pack corpus, run as slots run
        /// them (ATLAS_SELFTEST_PACKS reports each pack), the busiest piece of work took 1.5 million steps, the most memory
        /// 91 MB, and the longest 0.2 s.
        /// </summary>
        public sealed class ScriptLimits
        {
            /// <summary>Lua steps (instructions). MoonSharp runs 30-40 million a second on a desktop PC.</summary>
            public long Steps { get; init; } = 50_000_000;
            /// <summary>Memory the work may allocate, in bytes (garbage included: MoonSharp allocates as it runs).</summary>
            public long Memory { get; init; } = 1L << 30;
            /// <summary>How long the work may take: a backstop for steps that each take long (big string work).</summary>
            public TimeSpan Time { get; init; } = TimeSpan.FromSeconds(10);

            public static ScriptLimits Default { get; } = new ScriptLimits();
        }

        private int _workDepth;

        /// <summary>
        /// Runs one piece of the scripts' work under the limits (anything it does in turn is part of it). False once the
        /// scripts are stopped: this work went over a limit or broke the script engine, or earlier work did.
        /// </summary>
        private bool Work(string what, Action action)
        {
            if (_script == null || _stopReason != null) return false;
            if (_workDepth > 0)
            {
                action();
                return true;
            }
            _workDepth++;
            _watchdog.Begin(what, Limits);
            try { action(); }
            catch (Exception ex) { Stop(what, ex); }
            finally
            {
                _workDepth--;
                // Swallowed on the way out (table.sort drops errors that aren't Lua's), but the work still went over.
                if (_watchdog.Stop != null) StopWith(_watchdog.Stop);
                NotePeak(what);
            }
            return _stopReason == null;
        }

        /// <summary>What ended a piece of work early: a limit, a Lua error nothing caught, or the script engine failing.</summary>
        private void Stop(string what, Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is ScriptStopped) { StopWith(e.Message); return; }
            }
            var cause = ex is InterpreterBroke ? ex.InnerException : ex;
            if (cause is InterpreterException lua)
            {
                AddError($"{what}: {lua.DecoratedMessage ?? lua.Message}");
                return;
            }
            StopWith(cause switch
            {
                // MoonSharp's stacks are fixed arrays: recursion in Lua that never ends fills them.
                IndexOutOfRangeException when ThrownBy(cause) == typeof(Script).Assembly => $"{what} called itself too deeply",
                InsufficientExecutionStackException => $"{what} called itself too deeply",
                OutOfMemoryException => $"{what} ran out of memory",
                _ => $"{what} broke the script engine ({cause.GetType().Name}: {cause.Message})"
            });
        }

        /// <summary>The library that threw: the exception's first frame outside .NET's own (where its throw helpers are).</summary>
        private static System.Reflection.Assembly ThrownBy(Exception ex) =>
            new StackTrace(ex).GetFrames().Select(f => f.GetMethod()?.DeclaringType?.Assembly).FirstOrDefault(a => a != typeof(object).Assembly);

        private void StopWith(string reason)
        {
            if (_stopReason == null) _stopReason = reason;
        }

        private void NotePeak(string what)
        {
            var peak = Peak;
            var time = _watchdog.Elapsed;
            if (_watchdog.Steps > peak.Steps) PeakWork = what;
            Peak = (Math.Max(peak.Steps, _watchdog.Steps), Math.Max(peak.Memory, _watchdog.MemoryUsed), time > peak.Time ? time : peak.Time);
        }

        /// <summary>
        /// Sees every Lua step the scripts take, wherever it runs (in pcall, a coroutine, a sort's comparison, or Lua that a
        /// callback of Atlas's calls), through MoonSharp's debugger hook, and stops work that goes over a limit. MoonSharp's
        /// own limit (a coroutine's AutoYieldCounter) can't: Lua run from callbacks isn't counted, and each coroutine costs
        /// 2 MB of stacks. A stop is an exception Lua can't catch, and it's sticky: if something swallows it (table.sort
        /// does), the next step throws it again, and the end of the work checks for it.
        /// </summary>
        private sealed class Watchdog : IDebugger
        {
            private ScriptLimits _limits = ScriptLimits.Default;
            private string _work;
            private long _memoryAtStart, _clockAtStart, _clockLimit;

            public long Steps { get; private set; }
            /// <summary>Why the current work was stopped, once it was.</summary>
            public string Stop { get; private set; }

            public void Begin(string work, ScriptLimits limits)
            {
                _work = work;
                _limits = limits;
                Steps = 0;
                Stop = null;
                _memoryAtStart = GC.GetAllocatedBytesForCurrentThread();
                _clockAtStart = Stopwatch.GetTimestamp();
                _clockLimit = (long)(limits.Time.TotalSeconds * Stopwatch.Frequency);
            }

            public long MemoryUsed => GC.GetAllocatedBytesForCurrentThread() - _memoryAtStart;
            public long MemoryLeft => _limits.Memory - MemoryUsed;
            public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_clockAtStart);

            public bool IsPauseRequested()
            {
                if (Stop != null) throw new ScriptStopped(Stop);
                long steps = ++Steps;
                if (steps > _limits.Steps) Trip($"was still running after {_limits.Steps:N0} steps, so it may be stuck in a loop");
                if ((steps & 3) == 0)
                {
                    // Recursion through callbacks (a script loading itself, a sort comparing with itself) uses this thread's
                    // stack, and running out of it would end Atlas. Each level runs at least one step, so at most 4 new levels
                    // come between two checks: far less than the room the check keeps.
                    if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) Trip("called itself too deeply");
                    if ((steps & 15) == 0 && MemoryUsed > _limits.Memory) Trip($"used over {_limits.Memory >> 20:N0} MB of memory");
                    if ((steps & 1023) == 0 && Stopwatch.GetTimestamp() - _clockAtStart > _clockLimit) Trip($"took over {_limits.Time.TotalSeconds:0.#} seconds");
                }
                return false;
            }

            /// <summary>Stops the work before a single step builds something bigger than its memory has left.</summary>
            public void GuardSize(double bytes)
            {
                if (bytes > MemoryLeft) Trip($"asked for over {_limits.Memory >> 20:N0} MB of memory at once");
            }

            private void Trip(string why)
            {
                Stop = $"{_work} {why}";
                throw new ScriptStopped(Stop);
            }

            // The rest of the debugger interface: nothing to show, never pause.
            public DebuggerCaps GetDebuggerCaps() => 0;
            public void SetDebugService(DebugService debugService) { }
            public void SetSourceCode(SourceCode sourceCode) { }
            public void SetByteCode(string[] byteCode) { }
            public bool SignalRuntimeException(ScriptRuntimeException ex) => false;
            public DebuggerAction GetAction(int ip, SourceRef sourceref) => new DebuggerAction { Action = DebuggerAction.ActionType.Run };
            public void SignalExecutionEnded() { }
            public void Update(WatchType watchType, IEnumerable<WatchItem> items) { }
            public List<DynamicExpression> GetWatchItems() => new List<DynamicExpression>();
            public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
        }

        /// <summary>A piece of work went over a limit. Not a Lua error, so pcall can't catch it.</summary>
        private sealed class ScriptStopped : Exception
        {
            public ScriptStopped(string reason) : base(reason) { }
        }

        /// <summary>The script engine itself failed in Lua that Atlas's code called (its stacks are left mid-call).</summary>
        private sealed class InterpreterBroke : Exception
        {
            public InterpreterBroke(Exception inner) : base(inner.Message, inner) { }
        }

        /// <summary>
        /// Library functions that could hurt Atlas in a single step, which the watchdog (between steps) can't catch:
        /// collectgarbage forces a full collection of all of Atlas's memory, pausing everything (no pack in the corpus uses
        /// it), and string.rep and table.concat build their whole result at once. Each is replaced by a checked version.
        /// </summary>
        private void GuardLibrary()
        {
            _script.Globals["collectgarbage"] = Callback(args => (args.Count > 0 ? args[0].CastToString() : null) switch
            {
                "count" => DynValue.NewNumber(GC.GetTotalMemory(false) / 1024.0),
                "isrunning" => DynValue.True,
                _ => DynValue.NewNumber(0)
            });
            // Both build their result in a buffer, then copy it out: 2 bytes a character, twice.
            var strings = _script.Globals.Get("string").Table;
            strings["rep"] = Checked(strings.Get("rep"), args =>
            {
                if (args.Count < 2 || args[0].Type != DataType.String || args[1].CastToNumber() is not double n || n < 1) return;
                double separator = args.Count > 2 && args[2].Type == DataType.String ? args[2].String.Length : 0;
                _watchdog.GuardSize((args[0].String.Length + separator) * n * 4);
            });
            var tables = _script.Globals.Get("table").Table;
            tables["concat"] = Checked(tables.Get("concat"), args =>
            {
                if (args.Count < 1 || args[0].Type != DataType.Table) return;
                var list = args[0].Table;
                double separator = args.Count > 1 && args[1].Type == DataType.String ? args[1].String.Length : 0;
                int first = args.Count > 2 && args[2].CastToNumber() is double i ? (int)i : 1;
                int last = args.Count > 3 && args[3].CastToNumber() is double j ? (int)j : list.Length;
                double characters = 0;
                for (int k = first; k <= last; k++)
                {
                    var value = list.Get(k);
                    if (value.Type == DataType.String) characters += value.String.Length + separator;
                    else if (value.Type == DataType.Number) characters += 24 + separator;
                    else break; // concat itself reports it
                }
                _watchdog.GuardSize(characters * 4);
            });
        }

        /// <summary>A library function, checked before it runs.</summary>
        private static DynValue Checked(DynValue original, Action<CallbackArguments> check)
        {
            var run = original.Callback.ClrCallback;
            return DynValue.NewCallback((context, arguments) =>
            {
                check(arguments);
                return run(context, arguments);
            });
        }

        /// <summary>
        /// MoonSharp's library throws .NET exceptions for some arguments where Lua raises an error (math.random(1e20),
        /// string.format("%c", -1), os.date of a time out of range), and those get past pcall with the interpreter's stacks
        /// left mid-call. Each library function is wrapped so that its own failures are Lua errors, as in Lua: pcall can
        /// catch them, the interpreter unwinds, and the scripts carry on.
        /// </summary>
        private void WrapLibrary()
        {
            var globals = _script.Globals;
            foreach (var pair in globals.Pairs.ToList())
            {
                if (pair.Value.Type == DataType.ClrFunction) globals.Set(pair.Key, AsLuaErrors(pair.Value));
                else if (pair.Value.Type == DataType.Table && pair.Value.Table != globals)
                {
                    var library = pair.Value.Table;
                    foreach (var member in library.Pairs.ToList())
                        if (member.Value.Type == DataType.ClrFunction) library.Set(member.Key, AsLuaErrors(member.Value));
                }
            }
        }

        /// <summary>
        /// A library function whose own .NET failures are Lua errors. A failure from Lua it ran in turn (a sort's comparison)
        /// passes through untouched: there the interpreter itself failed, or a limit stopped the work.
        /// </summary>
        private static DynValue AsLuaErrors(DynValue function)
        {
            var run = function.Callback.ClrCallback;
            return DynValue.NewCallback((context, arguments) =>
            {
                try { return run(context, arguments); }
                catch (Exception ex) when (ex is not InterpreterException && ex is not ScriptStopped && ex is not InterpreterBroke)
                {
                    if (FromLua(ex)) throw;
                    throw new ScriptRuntimeException($"{ex.GetType().Name}: {ex.Message}");
                }
            }, function.Callback.Name);
        }

        /// <summary>Whether an exception came out of Lua: one of MoonSharp's processing loops ran between where it was thrown and here.</summary>
        private static bool FromLua(Exception ex) =>
            new StackTrace(ex).GetFrames().Any(f => f.GetMethod() is { Name: "Processing_Loop" } method && method.DeclaringType?.Assembly == typeof(Script).Assembly);

        private void AddError(string message)
        {
            if (Errors.Count < 50 && !Errors.Contains(message)) Errors.Add(message);
        }

        // =====================================================================
        // Reading results
        // =====================================================================

        /// <summary>The state of the object a code names, or null if no pack object provides it (or the scripts were stopped).</summary>
        public TileState StateOf(string code)
        {
            if (_script == null || _stopReason != null || string.IsNullOrEmpty(code)) return null;
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
            // A custom item's own functions say: Lua, so it's work like any other.
            TileState state = null;
            Work($"reading '{code}'", () =>
            {
                var lua = FindLuaItem(code);
                if (lua == null) return;
                var provided = CallField(lua, "ProvidesCodeFunc", DynValue.NewTable(lua), DynValue.NewString(code));
                int n = provided.Type == DataType.Number ? (int)provided.Number : provided.Type == DataType.Boolean && provided.Boolean ? 1 : 0;
                state = new TileState { Active = n > 0, Stage = n, Count = n, Touched = true };
            });
            return _stopReason == null ? state : null;
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
            host["LoadScript"] = Callback(args => RunScript(args.Count > 1 ? args[1].CastToString() ?? "" : "") ? DynValue.True : DynValue.False);
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

            // require: the pack's modules, compiled at the start like the rest of its scripts.
            g["__require_clr_impl"] = Callback(args =>
            {
                string name = args.Count > 0 ? args[0].CastToString() : null;
                string file = name == null ? null : _script.Options.ScriptLoader.ResolveModuleName(name, _script.Globals);
                if (file == null) throw new ScriptRuntimeException($"module '{name}' not found");
                return Compiled(file, out string compileError) ?? throw new ScriptRuntimeException(compileError ?? $"module '{name}' isn't a Lua script");
            });
        }

        /// <summary>A table whose unknown members are no-op functions, recorded as unsupported.</summary>
        private Table ApiTable(string name)
        {
            var t = new Table(_script);
            var meta = new Table(_script);
            meta["__index"] = Callback(args =>
            {
                string key = args.Count > 1 ? args[1].CastToString() : "?";
                UnsupportedApis.Add($"{name}.{key}");
                return NoOp();
            });
            t.MetaTable = meta;
            return t;
        }

        /// <summary>
        /// A function of Atlas's that the scripts can call. If it fails, that's a Lua error at the script's call, as a
        /// library function's would be: pcall can catch it, and the interpreter unwinds. A stop, or the interpreter failing
        /// in Lua the function called in turn, passes through untouched.
        /// </summary>
        private static DynValue Callback(Func<CallbackArguments, DynValue> f) => DynValue.NewCallback((context, arguments) =>
        {
            try { return f(arguments); }
            catch (Exception ex) when (ex is not InterpreterException && ex is not ScriptStopped && ex is not InterpreterBroke)
            {
                throw new ScriptRuntimeException($"{ex.GetType().Name}: {ex.Message}");
            }
        });

        private static DynValue NoOp(DynValue result = null) => Callback(_ => result ?? DynValue.Nil);

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
            meta["__index"] = Callback(args =>
            {
                string key = args[1].CastToString();
                if (key == "Set") return Callback(a => { SetField(obj, a[1].CastToString(), a[2]); return DynValue.Nil; });
                if (key == "Get") return Callback(a => obj.Fields.TryGetValue(a[1].CastToString() ?? "", out var v) ? v : DynValue.Nil);
                return key != null && obj.Fields.TryGetValue(key, out var value) ? value : DynValue.Nil;
            });
            meta["__newindex"] = Callback(args =>
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
                    foreach (var fn in list.ToList()) Run($"watch for {code}", () => CallLua(fn, code));
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
            methods["Set"] = Callback(args =>
            {
                var self = args[0].Table;
                var key = args[1];
                var value = args.Count > 2 ? args[2] : DynValue.Nil;
                props.Set(key, value);
                CallField(self, "PropertyChangedFunc", DynValue.NewTable(self), key, value);
                return DynValue.Nil;
            });
            methods["Get"] = Callback(args => props.Get(args[1]));
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
            try { return CallLua(fn, args); }
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

            meta["__index"] = Callback(args =>
            {
                var key = args[1];
                string name = key.Type == DataType.Number ? ((long)key.Number).ToString() : key.CastToString();
                if (name == null) return DynValue.Nil;
                JToken value = token is JObject jo ? jo[name] : (key.Type == DataType.Number && token is JArray ja && key.Number >= 1 && key.Number <= ja.Count ? ja[(int)key.Number - 1] : null);
                string full = path.Length == 0 ? name : path + "." + name;
                // Only single values decide a tile ("options.goal == 1"); reading a table ("slot_data.options") is
                // just navigation and would credit the whole table to whatever the script sets next.
                if (value != null && value is not JObject && value is not JArray && _pendingReads.Count < 200) _pendingReads.Add(full);
                return value == null && !overlay.ContainsKey(name) ? DynValue.Nil : Child(name, value);
            });
            meta["__newindex"] = Callback(args =>
            {
                string name = args[1].Type == DataType.Number ? ((long)args[1].Number).ToString() : args[1].CastToString();
                if (name != null) overlay[name] = args[2];
                return DynValue.Nil;
            });
            meta["__len"] = Callback(args => DynValue.NewNumber(token is JArray arr ? arr.Count : 0));
            // pairs/ipairs walk the data without counting as reads (dumping slot_data shouldn't credit anything).
            DynValue Iterator(bool arrayOnly)
            {
                var entries = Entries().Where(e => !arrayOnly || e.Key.Type == DataType.Number).ToList();
                int index = 0;
                var next = Callback(args =>
                {
                    if (index >= entries.Count) return DynValue.Nil;
                    var e = entries[index++];
                    return DynValue.NewTuple(e.Key, Child(e.Name, e.Value));
                });
                return DynValue.NewTuple(next, DynValue.NewTable(proxy), DynValue.Nil);
            }
            meta["__pairs"] = Callback(args => Iterator(false));
            meta["__ipairs"] = Callback(args => Iterator(true));
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

            /// <summary>The option's value for display: short, and a summary for lists and tables rather than their contents.</summary>
            public string ValueText => FormatValue(OptionValue);

            public static string FormatValue(JToken value)
            {
                switch (value?.Type)
                {
                    case null: return "";
                    case JTokenType.Object: return $"{{{((JObject)value).Count} options}}";
                    case JTokenType.Array:
                        var array = (JArray)value;
                        if (array.Count <= 4 && array.All(v => v is JValue)) return "[" + string.Join(", ", array.Select(v => v.ToString())) + "]";
                        return $"[{array.Count} items]";
                    case JTokenType.String:
                        string text = value.ToString();
                        return text.Length > 60 ? text.Substring(0, 57) + "…" : text;
                    default:
                        return value.ToString(Newtonsoft.Json.Formatting.None);
                }
            }
        }

        /// <summary>
        /// The pack's seed-setting indicators: the given codes (e.g. a settings grid) plus anything the clear
        /// handler set from a slot_data option. Works for any pack, whatever it names its settings.
        /// </summary>
        public List<SettingInfo> Settings(IEnumerable<string> settingCodes)
        {
            var result = new List<SettingInfo>();
            if (_script == null || _stopReason != null) return result;
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
