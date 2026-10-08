#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>What the engine's health check reported (the AtlasCheck bridge component).</summary>
    public class EngineCheckResult
    {
        [JsonProperty("python")] public string Python { get; set; }
        [JsonProperty("ap")] public string Archipelago { get; set; }
        [JsonProperty("ut")] public string Tracker { get; set; }
        [JsonProperty("tested_ut")] public List<string> TestedTracker { get; set; } = new();
        [JsonProperty("games")] public List<string> Games { get; set; } = new();
        [JsonProperty("failed")] public List<string> FailedWorlds { get; set; } = new();
        [JsonProperty("tracker")] public bool TrackerLoads { get; set; }
        [JsonProperty("tracker_error")] public string TrackerError { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
        /// <summary>Why each failed world didn't load (portable engine): a missing module and the error.</summary>
        [JsonProperty("failed_details")] public Dictionary<string, FailedWorldInfo> FailedDetails { get; set; } = new();
        /// <summary>Universal Tracker functions the bridge needs that this version lacks (it changed incompatibly).</summary>
        [JsonProperty("api_missing")] public List<string> ApiMissing { get; set; } = new();
        /// <summary>End-to-end test: a small built-in game rebuilt and its logic computed. Null when no test game exists.</summary>
        [JsonProperty("smoke")] public EngineSmokeResult Smoke { get; set; }
        public DateTime Checked { get; set; } = DateTime.Now;
        public string Root { get; set; }

        [JsonIgnore] public bool TrackerTested => !string.IsNullOrEmpty(Tracker) && TestedTracker.Contains(Tracker);

        /// <summary>Everything logic needs works: loads, the tracker's API matches, and the end-to-end test computed logic.</summary>
        [JsonIgnore] public bool Passed => Error == null && TrackerLoads && (ApiMissing == null || ApiMissing.Count == 0) && (Smoke == null || Smoke.Ok);

        /// <summary>Why the check failed, in one line (null when it passed).</summary>
        [JsonIgnore]
        public string Problem =>
            Error != null ? "the engine didn't start: " + LastLine(Error)
            : !TrackerLoads ? "the Universal Tracker didn't load: " + LastLine(TrackerError)
            : ApiMissing?.Count > 0 ? $"this Universal Tracker version is incompatible with Atlas (missing: {string.Join(", ", ApiMissing)})"
            : Smoke != null && !Smoke.Ok ? $"the end-to-end logic test on {Smoke.Game} failed: {LastLine(Smoke.Error)}"
            : null;

        private static string LastLine(string s) => string.IsNullOrWhiteSpace(s) ? "unknown error" : s.Trim().Split('\n')[^1].Trim();
    }

    public class FailedWorldInfo
    {
        [JsonProperty("missing")] public string MissingModule { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("is_zip")] public bool IsZip { get; set; }
    }

    public class EngineSmokeResult
    {
        [JsonProperty("game")] public string Game { get; set; }
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("locations")] public int Locations { get; set; }
        [JsonProperty("in_logic")] public int InLogic { get; set; }
        /// <summary>Whether the goal was reachable with every progression item (proves go-mode detection works).</summary>
        [JsonProperty("goal_with_all_items")] public bool? GoalWithAllItems { get; set; }
        [JsonProperty("goal_at_start")] public bool? GoalAtStart { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
    }

    /// <summary>What's installed in the portable engine (PortableData/engine/engine.json).</summary>
    public class PortableEngineState
    {
        public string Python { get; set; }
        public string Archipelago { get; set; }
        public string PackagesSignature { get; set; }
        /// <summary>World → signature of the requirements already tried, so a package that can't install isn't retried on every setup.</summary>
        public Dictionary<string, string> WorldPackagesTried { get; set; } = new();
        public string Tracker { get; set; }
        public EngineCheckResult LastCheck { get; set; }
    }

    public enum EngineStepId { Runtime, Archipelago, Packages, Tracker, Bridge, Check }

    public enum EngineStepState { Ok, Missing, Warning, Error }

    public class EngineStepStatus
    {
        public EngineStepId Id { get; set; }
        public string Title { get; set; }
        public EngineStepState State { get; set; }
        public string Detail { get; set; }
        /// <summary>Button text for the step's fix, or null when there's nothing to do.</summary>
        public string Action { get; set; }
    }

    /// <summary>
    /// The Atlas Engine: the Archipelago + Universal Tracker setup that logic runs on. By default Atlas downloads its
    /// own portable copy into PortableData/engine (pinned, hash-checked versions; no installer, no admin rights);
    /// an existing Archipelago install can be used instead. Everything here runs off the main thread except the
    /// quick status checks.
    /// </summary>
    public static class AtlasEngine
    {
        // --- Pinned versions (update together, after testing the bridge against them) ---
        public const string PythonVersion = "3.12.10";
        private const string PythonUrl = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip";
        private const string PythonSha256 = "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3";
        private const string PythonPth = "python312._pth";

        // pip, pinned: one wheel from PyPI, checked against its published SHA-256. A wheel is the installed files, so it's
        // unpacked into the runtime and then reinstalls itself from the same file: nothing unpinned is downloaded or run.
        public const string PipVersion = "26.2.1";
        private const string PipWheelUrl = "https://files.pythonhosted.org/packages/f3/6e/1736e5b4ae2b778ef2f81c47d797de9f891d4d8acb047a24ca37a60294dd/pip-26.2.1-py3-none-any.whl";
        private const string PipWheelSha256 = "71138adf1f4ca900cdb7d289c21b7494329f2332b6d85f0e1c42108c0384ed3e";

        public const string ArchipelagoVersion = "0.6.7";
        private const string ArchipelagoUrl = "https://github.com/ArchipelagoMW/Archipelago/archive/refs/tags/0.6.7.zip";
        private const string ArchipelagoSha256 = "440dfc50afd35663ccac1052a14bd7dc733b130894282870becb6903174d674a";

        public const string TrackerVersion = "v0.3.4";
        private const string TrackerUrl = "https://github.com/FarisTheAncient/Archipelago/releases/download/Tracker_v0.3.4/tracker.apworld";
        private const string TrackerSha256 = "2978cabdac919ba19a7fddb0ed0cf79bee5553f29a6b681f2941a79e0ba02f75";

        /// <summary>Archipelago requirements Atlas leaves out: the desktop GUI and build tools, which the engine never uses.</summary>
        private static readonly string[] SkippedRequirements = { "kivy", "kivymd", "cython", "pyshortcuts" };

        /// <summary>Packages some bundled worlds import at load time without listing them in requirements.txt.</summary>
        private static readonly string[] ExtraRequirements = { "requests", "setuptools<81" };

        /// <summary>
        /// The one package Atlas unpacks itself instead of pip: setuptools' wheel carries test folders whose paths run to
        /// 115 characters below site-packages, past Windows' 259-character limit from any ordinary Downloads folder, and
        /// pip fails partway when one file can't be written. Archipelago needs the package (ModuleUpdate.py and some worlds
        /// import pkg_resources), not its tests. pip still fetches the wheel against the lock's hashes; Atlas checks the
        /// hash again and leaves these folders out.
        /// </summary>
        private const string UnpackedPackage = "setuptools";
        private static readonly string[] UnpackedPackageSkips = { "pkg_resources/tests/", "setuptools/tests/" };

        // --- Paths ---
        public static string EngineDir => Path.Combine(DataManager.GetDataDirectory(), "engine");
        public static string PythonDir => Path.Combine(EngineDir, "python");
        public static string PythonExe => Path.Combine(PythonDir, "python.exe");
        public static string ArchipelagoDir => Path.Combine(EngineDir, "archipelago");
        public static string RunnerPath => Path.Combine(EngineDir, "atlas_run.py");
        private static string StatePath => Path.Combine(EngineDir, "engine.json");
        private static string DownloadsDir => Path.Combine(EngineDir, "downloads");
        private static string BackupsDir => Path.Combine(EngineDir, "backups");
        /// <summary>The engine processes' temporary files (see <see cref="KeepFilesInEngineFolder"/>).</summary>
        public static string TempDir => Path.Combine(EngineDir, "temp");
        /// <summary>Stands in for %LocalAppData% and %AppData% in engine processes (Archipelago's cache, pip's cache).</summary>
        public static string UserDir => Path.Combine(EngineDir, "user");

        /// <summary>
        /// For the UI test only: a Python already on this computer that runs the engine's components in place of the
        /// portable engine's own, so the test can run its fake engine (AP_Atlas.Core.Testing.FakeLogicEngine). Null
        /// otherwise. Setup never uses it, so nothing is ever installed into that Python. Only the UI test sets it (a
        /// guard rail checks).
        /// </summary>
        internal static string TestPython { get; set; }

        private static AppSettings _settings;

        /// <summary>Raised (on any thread) when the engine's setup or mode changed.</summary>
        public static event Action Changed;

        public static void Initialize(AppSettings settings)
        {
            _settings = settings;
            ApworldSources.Initialize(settings);
            RecoverInterruptedUpdate();
        }

        public static void NotifyChanged() => Changed?.Invoke();

        // =====================================================================
        // Mode
        // =====================================================================

        public static bool PortableInstalled => EngineInstall.Portable().CanLaunch;

        /// <summary>
        /// The engine in use: the user's choice; until they choose, a working existing install keeps being used
        /// (so downloading the portable engine never silently changes a setup that works), else the portable engine.
        /// </summary>
        public static EngineMode EffectiveMode(AppSettings s)
        {
            if (s != null && Enum.TryParse<EngineMode>(s.EngineMode, out var chosen)) return chosen;
            var existing = EngineInstall.Existing(s?.ArchipelagoInstallationPath);
            if (existing.CanLaunch && existing.HasTracker) return EngineMode.Existing;
            return EngineMode.Portable;
        }

        public static EngineInstall Resolve(AppSettings s) =>
            EffectiveMode(s) == EngineMode.Portable ? EngineInstall.Portable() : EngineInstall.Existing(s?.ArchipelagoInstallationPath);

        public static EngineInstall Current => Resolve(_settings);

        public static void SetMode(EngineMode mode)
        {
            if (_settings == null) return;
            _settings.EngineMode = mode.ToString();
            DataManager.SaveSettings(_settings);
            NotifyChanged();
        }

        /// <summary>Why logic can't run on an engine, or null when it can.</summary>
        public static string ProblemWith(EngineInstall install)
        {
            if (!install.CanLaunch)
                return install.Mode == EngineMode.Portable ? "The Atlas Engine isn't set up yet." : "Archipelago wasn't found at the chosen folder.";
            if (!install.HasTracker) return "The Universal Tracker isn't installed in the engine.";
            if (install.Mode == EngineMode.Existing && !MayWriteTo(install) && install.WorldsDir != null && !BridgeIsCurrent(install))
                return "Atlas needs your OK before it adds its bridge to your Archipelago install (Atlas Engine → Allow…).";
            return null;
        }

        /// <summary>The Archipelago folder the user chose for Atlas (empty when none). Atlas never searches for one by itself.</summary>
        public static string ConfiguredInstallPath => _settings?.ArchipelagoInstallationPath ?? "";

        // =====================================================================
        // Existing Archipelago installs
        // =====================================================================

        /// <summary>
        /// Archipelago installs on this PC: the usual folders plus wherever its installer registered itself. This searches
        /// the PC, so it's only called after the user agreed (<see cref="Permissions.FindArchipelago"/>); never at startup.
        /// </summary>
        public static List<string> FindArchipelagoInstalls()
        {
            var candidates = new List<string>();
            void Add(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                try { p = Path.GetFullPath(p.Trim().Trim('"')); } catch { return; }
                if (!candidates.Contains(p, StringComparer.OrdinalIgnoreCase)) candidates.Add(p);
            }
            Add(_settings?.ArchipelagoInstallationPath);
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Archipelago"));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Archipelago"));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Archipelago"));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Archipelago"));
            foreach (var drive in new[] { "C", "D", "E" }) Add($@"{drive}:\Archipelago");
            foreach (var p in RegisteredInstalls()) Add(p);
            return candidates.Where(p => File.Exists(Path.Combine(p, "ArchipelagoLauncher.exe"))).ToList();
        }

        private static IEnumerable<string> RegisteredInstalls()
        {
            var found = new List<string>();
            if (!OperatingSystem.IsWindows()) return found;
            try
            {
                foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                {
                    foreach (var path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    {
                        using var root = hive.OpenSubKey(path);
                        if (root == null) continue;
                        foreach (var name in root.GetSubKeyNames())
                        {
                            using var key = root.OpenSubKey(name);
                            string display = key?.GetValue("DisplayName") as string;
                            if (display == null || !display.StartsWith("Archipelago", StringComparison.OrdinalIgnoreCase)) continue;
                            if (key.GetValue("InstallLocation") is string location) found.Add(location);
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.LogDebug("Couldn't read Windows' list of installed programs: " + ex.Message); }
            return found;
        }

        public static void UseExistingInstall(string path)
        {
            if (_settings == null) return;
            _settings.ArchipelagoInstallationPath = path;
            _settings.EngineMode = EngineMode.Existing.ToString();
            DataManager.SaveSettings(_settings);
            NotifyChanged();
        }

        // =====================================================================
        // Changes to the user's own Archipelago install: only with their OK, and recorded so they can be undone
        // =====================================================================

        public class InstallChange
        {
            public string Root { get; set; }
            /// <summary>"added": a file Atlas put there. "moved": a file of theirs Atlas moved aside (Path → MovedTo).</summary>
            public string Kind { get; set; }
            public string Path { get; set; }
            public string MovedTo { get; set; }
            public DateTime When { get; set; } = DateTime.Now;
        }

        private static string ChangesPath => Path.Combine(EngineDir, "install_changes.json");
        private static List<InstallChange> _changes;
        private static List<InstallChange> Changes => _changes ??= SafeFile.ReadJson(ChangesPath, () => new List<InstallChange>());

        /// <summary>Whether Atlas may write into an engine's folders: its own portable engine always; the user's install only with their OK.</summary>
        public static bool MayWriteTo(EngineInstall install) =>
            install.Mode == EngineMode.Portable || Permissions.IsAllowed(_settings, Permissions.WriteArchipelago, install.Root);

        private static void RequireWriteConsent(EngineInstall install)
        {
            if (!MayWriteTo(install))
                throw new InvalidOperationException("Atlas needs your OK before it adds files to your Archipelago install (Atlas Engine → My Archipelago install → Allow…).");
        }

        private static void RecordChange(EngineInstall install, string kind, string path, string movedTo = null)
        {
            if (install.Mode != EngineMode.Existing) return;
            lock (Changes)
            {
                if (kind == "added" && Changes.Any(c => c.Kind == "added" && SamePath(c.Path, path))) return;
                Changes.Add(new InstallChange { Root = install.Root, Kind = kind, Path = path, MovedTo = movedTo });
                try { SafeFile.WriteJson(ChangesPath, Changes); }
                catch (Exception ex) { Logger.LogWarning("Couldn't record a change to your Archipelago install: " + ex.Message); }
            }
        }

        /// <summary>What Atlas changed in an install (files it added, files it moved aside), for "Remove Atlas's files".</summary>
        public static List<InstallChange> ChangesIn(string root)
        {
            lock (Changes) return Changes.Where(c => SamePath(c.Root, root)).ToList();
        }

        /// <summary>Atlas's bridge in an install's world folders (also from before changes were recorded).</summary>
        public static List<string> AtlasFilesIn(EngineInstall install) =>
            install.WorldFolders().Select(d => Path.Combine(d, "UltimateBridge.apworld")).Where(File.Exists).ToList();

        /// <summary>
        /// Undoes Atlas's changes to the user's install: removes the files it added (and its bridge), and puts back files
        /// it moved aside. Engines using the install are stopped first. Returns how many changes were undone.
        /// </summary>
        public static Task<int> RemoveAtlasFilesAsync(EngineInstall install, Action<string> log, CancellationToken ct) =>
            ExclusiveAsync(async () =>
            {
                await StopEnginesUsingAsync(install.Root, log, ct);
                int undone = 0;
                foreach (var c in ChangesIn(install.Root).AsEnumerable().Reverse())
                {
                    try
                    {
                        if (c.Kind == "added" && File.Exists(c.Path))
                        {
                            File.Delete(c.Path);
                            undone++;
                            log($"Removed {c.Path}");
                        }
                        else if (c.Kind == "moved" && File.Exists(c.MovedTo) && !File.Exists(c.Path))
                        {
                            File.Move(c.MovedTo, c.Path);
                            undone++;
                            log($"Put back {c.Path}");
                        }
                    }
                    catch (Exception ex) { log($"Couldn't undo the change to {c.Path}: {ex.Message}"); }
                }
                foreach (var bridge in AtlasFilesIn(install))
                {
                    try
                    {
                        File.Delete(bridge);
                        undone++;
                        log($"Removed {bridge}");
                    }
                    catch (Exception ex) { log($"Couldn't remove {bridge}: {ex.Message}"); }
                }
                string loose = install.WorldsDir != null ? Path.Combine(install.WorldsDir, "UltimateBridge") : null;
                if (loose != null && Directory.Exists(loose))
                {
                    try { Directory.Delete(loose, true); undone++; }
                    catch (Exception ex) { log($"Couldn't remove {loose}: {ex.Message}"); }
                }
                lock (Changes)
                {
                    Changes.RemoveAll(c => SamePath(c.Root, install.Root));
                    try { SafeFile.WriteJson(ChangesPath, Changes); }
                    catch (Exception ex) { Logger.LogWarning("Couldn't update the record of install changes: " + ex.Message); }
                }
                log(undone == 0 ? "Atlas had no files in that install." : $"Undid {undone} change(s): your Archipelago install no longer has anything from Atlas.");
                NotifyChanged();
                return undone;
            }, ct);

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a ?? "").TrimEnd('\\', '/'), Path.GetFullPath(b ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // =====================================================================
        // Status
        // =====================================================================

        private static PortableEngineState _state;

        public static PortableEngineState State
        {
            get
            {
                if (_state != null) return _state;
                _state = SafeFile.ReadJson<PortableEngineState>(StatePath, () => null);
                return _state ??= new PortableEngineState();
            }
        }

        private static void SaveState()
        {
            try
            {
                SafeFile.WriteJson(StatePath, State);
            }
            catch (Exception ex) { Logger.LogWarning("Couldn't save the engine state: " + ex.Message); }
        }

        private static readonly Dictionary<string, EngineCheckResult> _existingChecks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last health check of an engine (this session for an existing install; saved for the portable one).</summary>
        public static EngineCheckResult LastCheck(EngineInstall install)
        {
            if (install.Mode == EngineMode.Portable) return State.LastCheck;
            lock (_existingChecks) return _existingChecks.TryGetValue(install.Root, out var r) ? r : null;
        }

        /// <summary>Each setup step's status, cheap enough for the UI thread (no processes started).</summary>
        public static List<EngineStepStatus> Steps(EngineInstall install)
        {
            var steps = new List<EngineStepStatus>();
            var check = LastCheck(install);
            if (install.Mode == EngineMode.Portable)
            {
                bool python = File.Exists(PythonExe) && File.Exists(Path.Combine(PythonDir, "Lib", "site-packages", "pip", "__init__.py"));
                steps.Add(new EngineStepStatus
                {
                    Id = EngineStepId.Runtime,
                    Title = "Python runtime",
                    State = python ? EngineStepState.Ok : EngineStepState.Missing,
                    Detail = python ? $"Python {State.Python ?? PythonVersion} (portable, {Size(PythonDir)})" : $"Python {PythonVersion} for Windows (about 11 MB from python.org) and pip {PipVersion} (from PyPI), both hash-checked",
                    Action = python ? null : "Download"
                });
                bool ap = File.Exists(Path.Combine(ArchipelagoDir, "Utils.py"));
                steps.Add(new EngineStepStatus
                {
                    Id = EngineStepId.Archipelago,
                    Title = "Archipelago",
                    State = ap ? EngineStepState.Ok : EngineStepState.Missing,
                    Detail = ap ? $"Archipelago {State.Archipelago ?? "?"} (source, {Size(ArchipelagoDir)})" : $"Archipelago {ArchipelagoVersion} source, about 22 MB from GitHub",
                    Action = ap ? null : "Download"
                });
                string wanted = ap ? PackagesSignature() : null;
                bool packages = ap && State.PackagesSignature != null && State.PackagesSignature == wanted;
                var worldPackages = packages ? InstallableWorldPackages(install) : new List<(string World, string Requirements)>();
                steps.Add(new EngineStepStatus
                {
                    Id = EngineStepId.Packages,
                    Title = "Python packages",
                    State = packages && worldPackages.Count == 0 ? EngineStepState.Ok : (State.PackagesSignature != null && ap) || worldPackages.Count > 0 ? EngineStepState.Warning : EngineStepState.Missing,
                    Detail = packages && worldPackages.Count > 0 ? $"{worldPackages.Count} world(s) need their own packages: {string.Join(", ", worldPackages.Select(w => w.World))}"
                        : packages ? "Archipelago's packages (no desktop GUI)"
                        : State.PackagesSignature != null && ap ? "Archipelago's requirements changed; update the packages" : "Archipelago's packages from PyPI, about 20 MB, each checked against Atlas's list of hashes",
                    Action = packages && worldPackages.Count == 0 ? null : ap && python ? "Install" : null
                });
            }
            else
            {
                bool ok = install.CanLaunch;
                steps.Add(new EngineStepStatus
                {
                    Id = EngineStepId.Archipelago,
                    Title = "Archipelago install",
                    State = ok ? EngineStepState.Ok : EngineStepState.Error,
                    Detail = ok ? install.Root + (check?.Archipelago != null ? $" (Archipelago {check.Archipelago})" : "") : "No Archipelago install at " + (string.IsNullOrEmpty(install.Root) ? "(no folder chosen)" : install.Root),
                    Action = ok ? null : "Choose folder"
                });
            }

            var trackers = install.CanLaunch ? install.FindAllTrackers() : new List<string>();
            string trackerDetail;
            EngineStepState trackerState;
            if (trackers.Count == 0 && install.FindTracker() == null)
            {
                trackerState = EngineStepState.Missing;
                trackerDetail = $"Universal Tracker {TrackerVersion}, about 0.2 MB from GitHub";
            }
            else if (trackers.Count > 1)
            {
                trackerState = EngineStepState.Warning;
                trackerDetail = $"{trackers.Count} copies installed; they conflict ({string.Join(", ", trackers.Select(Path.GetFileName))})";
            }
            else if (check != null && !string.IsNullOrEmpty(check.Tracker) && !check.TrackerTested)
            {
                trackerState = EngineStepState.Warning;
                trackerDetail = $"Universal Tracker {check.Tracker} hasn't been tested with Atlas (tested: {string.Join(", ", check.TestedTracker)})";
            }
            else
            {
                trackerState = EngineStepState.Ok;
                trackerDetail = "Universal Tracker " + (check?.Tracker ?? "installed");
            }
            steps.Add(new EngineStepStatus
            {
                Id = EngineStepId.Tracker,
                Title = "Universal Tracker",
                State = trackerState,
                Detail = trackerDetail,
                Action = !install.CanLaunch ? null : trackerState == EngineStepState.Ok ? null : trackerState == EngineStepState.Missing ? "Download" : $"Use {TrackerVersion}"
            });

            bool bridge = install.WorldsDir != null && BridgeIsCurrent(install);
            steps.Add(new EngineStepStatus
            {
                Id = EngineStepId.Bridge,
                Title = "Atlas bridge",
                State = bridge ? EngineStepState.Ok : EngineStepState.Missing,
                Detail = bridge ? "Installed" : "Atlas's connector to the tracker (written by Atlas, no download)",
                Action = bridge || !install.CanLaunch ? null : "Install"
            });

            EngineStepState checkState;
            string checkDetail;
            if (check == null) { checkState = EngineStepState.Missing; checkDetail = "Not run yet"; }
            else if (!check.Passed) { checkState = EngineStepState.Error; checkDetail = "Failed: " + check.Problem; }
            else
            {
                checkState = EngineStepState.Ok;
                checkDetail = $"Working: Archipelago {check.Archipelago}, tracker {check.Tracker}, {check.Games.Count} games" +
                              (check.Smoke != null ? $", logic test passed on {check.Smoke.Game}" : "") +
                              (check.FailedWorlds.Count > 0 ? $" ({check.FailedWorlds.Count} worlds couldn't load)" : "") +
                              $", checked {check.Checked:g}";
            }
            steps.Add(new EngineStepStatus
            {
                Id = EngineStepId.Check,
                Title = "Health check",
                State = checkState,
                Detail = checkDetail,
                Action = install.CanLaunch && install.HasTracker ? (check == null ? "Run" : "Run again") : null
            });
            return steps;
        }

        private static string Size(string dir)
        {
            try
            {
                long bytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                return bytes > 1 << 20 ? $"{bytes / (1 << 20)} MB" : $"{bytes / 1024} KB";
            }
            catch { return "?"; }
        }

        // =====================================================================
        // Setup
        // =====================================================================

        // --- Safety: one setup at a time, never under a running engine, never left half-installed ---

        private static readonly SemaphoreSlim SetupLock = new SemaphoreSlim(1, 1);
        private const long RequiredFreeBytes = 700L * 1024 * 1024;

        /// <summary>Asks the slots using an engine (by root folder) to pause their logic so its files can change. Any thread.</summary>
        public static event Action<string> PauseRequested;

        public static bool SetupRunning => SetupLock.CurrentCount == 0;

        internal static async Task<T> ExclusiveAsync<T>(Func<Task<T>> body, CancellationToken ct)
        {
            await SetupLock.WaitAsync(ct);
            try { return await body(); }
            finally
            {
                SetupLock.Release();
                // After the release, so slots waiting on the engine see it free when they react. The parts that changed
                // first: pools retire the processes that loaded the old ones, running slots restart on the new.
                FlushChanges();
                NotifyChanged();
            }
        }

        /// <summary>Stops everything running from this engine (slots pause and resume afterwards) so files can be replaced.</summary>
        internal static async Task StopEnginesUsingAsync(string root, Action<string> log, CancellationToken ct)
        {
            if (ProcessJob.RunningUnder(root) == 0) return;
            log("Pausing logic for the slots using this engine while it changes…");
            PauseRequested?.Invoke(root);
            for (int i = 0; i < 100 && ProcessJob.RunningUnder(root) > 0; i++) await Task.Delay(100, ct);
            int killed = ProcessJob.KillAllUnder(root);
            if (killed > 0) log($"Stopped {killed} engine process(es) that didn't exit on their own.");
            await Task.Delay(300, ct); // let Windows release the file handles
        }

        private static void EnsureFreeSpace(string dir)
        {
            DriveInfo drive;
            try { drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))); }
            catch { return; } // network paths etc.: can't tell, don't block
            long free;
            try { free = drive.AvailableFreeSpace; } catch { return; }
            if (free < RequiredFreeBytes)
                throw new IOException($"Only {free / (1 << 20)} MB is free on {drive.Name}; the engine needs about {RequiredFreeBytes / (1 << 20)} MB. Free some space and try again.");
        }

        private static readonly string[] SwappedDirs = { "python", "archipelago" };

        /// <summary>
        /// Puts a fully prepared folder in place. The version it replaces is kept as "*.previous" until a health check
        /// passes, so a bad update can always be rolled back.
        /// </summary>
        private static void SwapIn(string staged, string target)
        {
            string previous = target + ".previous";
            if (Directory.Exists(target))
            {
                // A version from an earlier, unconfirmed update is older than the current one: keep the current.
                if (Directory.Exists(previous)) DeleteDir(previous);
                MoveDir(target, previous);
            }
            try { MoveDir(staged, target); }
            catch
            {
                if (!Directory.Exists(target) && Directory.Exists(previous)) MoveDir(previous, target);
                throw;
            }
        }

        /// <summary>After a passed health check: drop the kept previous versions.</summary>
        private static void CommitUpdates()
        {
            foreach (var name in SwappedDirs) DeleteDir(Path.Combine(EngineDir, name + ".previous"));
            _stateBeforeUpdate = null;
        }

        private static PortableEngineState _stateBeforeUpdate;

        private static void RememberStateBeforeUpdate()
        {
            _stateBeforeUpdate ??= JsonConvert.DeserializeObject<PortableEngineState>(JsonConvert.SerializeObject(State));
        }

        /// <summary>A health check failed after an update: put the previous working versions back.</summary>
        private static bool RollBack(Action<string> log)
        {
            bool any = false;
            foreach (var name in SwappedDirs)
            {
                string dir = Path.Combine(EngineDir, name), previous = dir + ".previous";
                if (!Directory.Exists(previous)) continue;
                if (Directory.Exists(dir)) DeleteDir(dir);
                MoveDir(previous, dir);
                any = true;
                log($"Rolled {name} back to the previous working version.");
            }
            if (any && _stateBeforeUpdate != null)
            {
                _state = _stateBeforeUpdate;
                _stateBeforeUpdate = null;
                SaveState();
            }
            return any;
        }

        /// <summary>
        /// Repairs an engine a crash or power loss left mid-update (called at startup and before setup): restores a
        /// folder that was moved aside but not replaced, and clears half-finished staging folders and downloads.
        /// </summary>
        public static void RecoverInterruptedUpdate()
        {
            try
            {
                if (!Directory.Exists(EngineDir)) return;
                foreach (var name in SwappedDirs)
                {
                    string dir = Path.Combine(EngineDir, name), previous = dir + ".previous";
                    if (!Directory.Exists(dir) && Directory.Exists(previous))
                    {
                        MoveDir(previous, dir);
                        Logger.LogWarning($"[Atlas Engine] Restored {name} after an interrupted update.");
                    }
                    DeleteDir(dir + ".new");
                }
                foreach (var trash in Directory.GetDirectories(EngineDir, "*.trash-*")) DeleteDir(trash);
                if (Directory.Exists(DownloadsDir))
                    foreach (var part in Directory.GetFiles(DownloadsDir, "*.part")) EngineDownloader.TryDelete(part);
            }
            catch (Exception ex) { Logger.LogWarning("[Atlas Engine] Couldn't check for an interrupted update: " + ex.Message); }
        }

        private static void MoveDir(string from, string to)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Move(from, to); return; }
                catch (IOException) when (attempt < 10) { Thread.Sleep(200); } // antivirus scanning a fresh folder
                catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(200); }
            }
        }

        private static void DeleteDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Directory.Delete(dir, true); return; }
                catch { Thread.Sleep(200); }
            }
            // Still locked: move it out of the way and clean it up next time.
            try { Directory.Move(dir, dir + ".trash-" + DateTime.Now.ToString("yyyyMMddHHmmss")); }
            catch (Exception ex) { Logger.LogWarning($"Couldn't delete or set aside {dir} (another program may be using it): {ex.Message}"); }
        }

        // --- Setup ---

        /// <summary>
        /// What setup is doing now, in plain words ("Downloading Python…"), for the setup panel; the log keeps the detail.
        /// Raised on the setup's thread.
        /// </summary>
        public static event Action<string> SetupStep;

        /// <summary>Why the last setup stopped, in plain words, or null when it finished (or hasn't run).</summary>
        public static string LastSetupProblem { get; private set; }

        /// <summary>Test hook: stands in for the whole setup (the UI test's setup panel scenario; nothing is downloaded).</summary>
        internal static Func<Action<string>, Action<float>, CancellationToken, Task<bool>> TestSetUp;

        /// <summary>
        /// Where the setup is: its step (1-based, 0 when unknown) of how many, what it's doing (null when only the
        /// fraction moved), and how far along the whole setup is (0 to 1; negative when unknown). One number for the
        /// whole setup, never going down (SetupProgressPlan).
        /// </summary>
        public sealed record SetupProgress(int Step, int Steps, string Text, float Fraction);

        /// <summary>Raised (on the setup's thread) as the setup moves: the step it's on and the overall fraction.</summary>
        public static event Action<SetupProgress> SetupProgressed;

        /// <summary>A part of the engine that changed: which, under which engine root, and for an apworld, its game.</summary>
        public sealed record EngineChange(string Kind, string Root, string Game = null);

        /// <summary>
        /// Raised (on any thread) once an engine change is done with: every part that changed since the last time.
        /// Running slots restart their logic on the new parts, and their engine pools retire the processes that loaded
        /// the old ones. Raised before <see cref="Changed"/>.
        /// </summary>
        public static event Action<IReadOnlyList<EngineChange>> PartsChanged;

        private static readonly List<EngineChange> _pendingChanges = new List<EngineChange>();
        private static SetupProgressPlan _plan;
        private static Action<float> _setupProgress;

        /// <summary>Notes a changed part; told to the slots when the change that made it is done (or at once when none is running).</summary>
        private static void NoteChange(string kind, string root, string game = null)
        {
            lock (_pendingChanges) _pendingChanges.Add(new EngineChange(kind, root, game));
            if (SetupLock.CurrentCount > 0) FlushChanges();
        }

        private static void FlushChanges()
        {
            List<EngineChange> changes;
            lock (_pendingChanges)
            {
                if (_pendingChanges.Count == 0) return;
                changes = _pendingChanges.ToList();
                _pendingChanges.Clear();
            }
            PartsChanged?.Invoke(changes);
        }

        /// <summary>Test hook: tells the slots the engine's parts changed, as an install would (the UI test's logic scenario).</summary>
        internal static void RaisePartsChangedForTests(IReadOnlyList<EngineChange> changes) => PartsChanged?.Invoke(changes);

        /// <summary>Logs a step and tells the panel about it.</summary>
        private static void Stage(Action<string> log, string text)
        {
            log?.Invoke(text);
            ReportStep(text);
        }

        /// <summary>A phase of the setup begins (the plan moves to its start), and it's logged and shown.</summary>
        private static void Phase(Action<string> log, string phaseId, string text)
        {
            _plan?.Enter(phaseId);
            Stage(log, text);
            if (_plan != null) _setupProgress?.Invoke(_plan.Fraction);
        }

        /// <summary>A download within the running phase moved (its own fraction, or negative when its size is unknown).</summary>
        private static void Progressed(float part)
        {
            if (_plan == null) return;
            float fraction = _plan.Within(part);
            SetupProgressed?.Invoke(new SetupProgress(_plan.Step, _plan.Steps, null, fraction));
        }

        /// <summary>Tells the panel what setup is doing (the UI test's pretend setup uses it too).</summary>
        internal static void ReportStep(string text)
        {
            SetupStep?.Invoke(text);
            SetupProgressed?.Invoke(new SetupProgress(_plan?.Step ?? 0, _plan?.Steps ?? 0, text, _plan?.Fraction ?? -1f));
        }

        /// <summary>
        /// Runs every step the engine still needs, then a health check. Updated parts are verified before they're
        /// swapped in; if the check fails afterwards, the previous working engine is restored.
        /// </summary>
        public static Task<bool> SetUpAsync(EngineInstall install, Action<string> log, Action<float> progress, CancellationToken ct) =>
            ExclusiveAsync(async () =>
            {
                LastSetupProblem = null;
                if (TestSetUp != null)
                {
                    bool pretend = await TestSetUp(log, f => { progress?.Invoke(f); SetupProgressed?.Invoke(new SetupProgress(0, 0, null, f)); }, ct);
                    if (!pretend) LastSetupProblem = "The pretend setup failed.";
                    NotifyChanged();
                    return pretend;
                }
                try
                {
                    RecoverInterruptedUpdate();
                    var steps = Steps(install);
                    bool Needs(EngineStepId id) => steps.Any(s => s.Id == id && s.State != EngineStepState.Ok);
                    // The phases this setup will run, so the panel shows one number for the whole of it.
                    var phases = new List<string>();
                    bool portable = install.Mode == EngineMode.Portable;
                    if (portable)
                    {
                        if (Needs(EngineStepId.Runtime)) { phases.Add("runtime"); phases.Add("pip"); }
                        if (Needs(EngineStepId.Archipelago)) phases.Add("archipelago");
                        if (Needs(EngineStepId.Runtime) || Needs(EngineStepId.Archipelago) || Needs(EngineStepId.Packages)) phases.Add("packages");
                    }
                    if (steps.First(s => s.Id == EngineStepId.Tracker).State == EngineStepState.Missing) phases.Add("tracker");
                    phases.Add("bridge");
                    phases.Add("check");
                    if (portable && (phases.Contains("packages") || WorldPackagesWanted(install))) { phases.Add("world-packages"); phases.Add("check-2"); }
                    _plan = SetupProgressPlan.For(phases);
                    _setupProgress = progress;
                    void overall(float part)
                    {
                        Progressed(part);
                        progress?.Invoke(_plan?.Fraction ?? part);
                    }
                    if (portable)
                    {
                        if (Needs(EngineStepId.Runtime)) await InstallRuntimeCoreAsync(log, overall, ct);
                        if (Needs(EngineStepId.Archipelago)) await InstallArchipelagoCoreAsync(log, overall, ct);
                        steps = Steps(install);
                        if (Needs(EngineStepId.Packages)) await InstallPackagesCoreAsync(log, ct);
                    }
                    else if (!install.CanLaunch)
                    {
                        LastSetupProblem = "Choose your Archipelago folder first.";
                        log("Choose your Archipelago folder first.");
                        return false;
                    }
                    steps = Steps(install);
                    if (steps.First(s => s.Id == EngineStepId.Tracker).State == EngineStepState.Missing) await InstallTrackerCoreAsync(install, log, overall, ct);
                    Phase(log, "bridge", "Installing Atlas's bridge…");
                    InstallBridge(install, log);
                    bool ok = await CheckCommitOrRollBackAsync(install, log, ct);
                    // Worlds that couldn't load for want of their own packages: install those, then verify again.
                    if (ok && portable && await InstallWorldPackagesCoreAsync(install, log, ct, force: false))
                        ok = await CheckCommitOrRollBackAsync(install, log, ct);
                    if (!ok) LastSetupProblem = ProblemWith(Current) ?? "The health check didn't pass (the log says what it found).";
                    else
                    {
                        _plan.Complete();
                        SetupProgressed?.Invoke(new SetupProgress(_plan.Steps, _plan.Steps, "Ready.", 1f));
                        progress?.Invoke(1f);
                    }
                    return ok;
                }
                catch (OperationCanceledException)
                {
                    LastSetupProblem = "Cancelled.";
                    log("Cancelled. Nothing half-installed was kept.");
                    RecoverInterruptedUpdate();
                    return false;
                }
                catch (Exception ex)
                {
                    LastSetupProblem = ex.Message;
                    log("Setup stopped: " + ex.Message);
                    Logger.LogWarning("[Atlas Engine] " + ex);
                    RecoverInterruptedUpdate();
                    if (install.Mode == EngineMode.Portable && RollBack(log)) log("The previous working engine is back in place.");
                    return false;
                }
                finally
                {
                    _plan = null;
                    _setupProgress = null;
                    NotifyChanged();
                }
            }, ct);

        /// <summary>Whether any installed world still wants packages of its own (false while Archipelago isn't there to ask).</summary>
        private static bool WorldPackagesWanted(EngineInstall install)
        {
            try { return InstallableWorldPackages(install).Count > 0; }
            catch (Exception) { return false; } // no Archipelago yet: the setup installs it first
        }

        /// <summary>Runs one step (from the setup window), then verifies the engine the same way setup does.</summary>
        public static Task<bool> RunStepAsync(EngineInstall install, EngineStepId step, Action<string> log, Action<float> progress, CancellationToken ct) =>
            ExclusiveAsync(async () =>
            {
                try
                {
                    RecoverInterruptedUpdate();
                    switch (step)
                    {
                        case EngineStepId.Runtime: await InstallRuntimeCoreAsync(log, progress, ct); break;
                        case EngineStepId.Archipelago: await InstallArchipelagoCoreAsync(log, progress, ct); break;
                        case EngineStepId.Packages:
                            if (State.PackagesSignature != PackagesSignature()) await InstallPackagesCoreAsync(log, ct);
                            if (install.Mode == EngineMode.Portable && LastCheck(install) == null) await RunCheckAsync(install, log, ct);
                            await InstallWorldPackagesCoreAsync(install, log, ct, force: true);
                            break;
                        case EngineStepId.Tracker: await InstallTrackerCoreAsync(install, log, progress, ct); break;
                        case EngineStepId.Bridge: InstallBridge(install, log); break;
                    }
                    if (step != EngineStepId.Check && !install.CanLaunch) return true; // more steps to go before a check makes sense
                    InstallBridge(install, log);
                    return await CheckCommitOrRollBackAsync(install, log, ct);
                }
                catch (OperationCanceledException)
                {
                    log("Cancelled. Nothing half-installed was kept.");
                    RecoverInterruptedUpdate();
                    return false;
                }
                catch (Exception ex)
                {
                    log("Stopped: " + ex.Message);
                    Logger.LogWarning("[Atlas Engine] " + ex);
                    RecoverInterruptedUpdate();
                    if (install.Mode == EngineMode.Portable && RollBack(log)) log("The previous working engine is back in place.");
                    return false;
                }
                finally
                {
                    NotifyChanged();
                }
            }, ct);

        private static async Task<bool> CheckCommitOrRollBackAsync(EngineInstall install, Action<string> log, CancellationToken ct)
        {
            if (!install.CanLaunch || !install.HasTracker) return false;
            var check = await RunCheckAsync(install, log, ct);
            if (check.Passed)
            {
                if (install.Mode == EngineMode.Portable) CommitUpdates();
                return true;
            }
            if (install.Mode == EngineMode.Portable && RollBack(log))
            {
                log("The updated engine failed its health check, so the previous working version was restored.");
                await RunCheckAsync(install, log, ct);
            }
            return false;
        }

        /// <summary>Before anything downloads: a folder too deep for Windows' path limit is said now, not by pip partway through.</summary>
        private static void EnsureDepth()
        {
            string problem = EnginePaths.DepthProblem(DataManager.GetDataDirectory());
            if (problem != null) throw new Exception(problem);
        }

        private static async Task InstallRuntimeCoreAsync(Action<string> log, Action<float> progress, CancellationToken ct)
        {
            EnsureDepth();
            EnsureFreeSpace(EngineDir);
            Phase(log, "runtime", $"Downloading Python {PythonVersion} from python.org…");
            string zip = Path.Combine(DownloadsDir, Path.GetFileName(PythonUrl));
            await EngineDownloader.DownloadAsync(PythonUrl, zip, PythonSha256, Report(progress), ct);
            Stage(log, "Verified. Unpacking Python…");
            string staging = PythonDir + ".new";
            DeleteDir(staging);
            await ZipFile.ExtractToDirectoryAsync(zip, staging, ct);
            // site-packages for pip; Archipelago's own folder is added by atlas_run.py at start. Paths are relative,
            // so the folder still works after it's moved into place.
            await File.WriteAllTextAsync(Path.Combine(staging, PythonPth), "python312.zip\n.\nLib\\site-packages\nimport site\n", ct);
            EngineDownloader.TryDelete(zip);

            log($"Downloading pip {PipVersion} from PyPI…");
            string pipWheel = Path.Combine(DownloadsDir, Path.GetFileName(new Uri(PipWheelUrl).AbsolutePath));
            await EngineDownloader.DownloadAsync(PipWheelUrl, pipWheel, PipWheelSha256, null, ct);
            Phase(log, "pip", "Verified. Installing pip…");
            string stagedExe = Path.Combine(staging, "python.exe");
            await ZipFile.ExtractToDirectoryAsync(pipWheel, Path.Combine(staging, "Lib", "site-packages"), overwriteFiles: true, ct);
            int code = await RunAsync(stagedExe, new[] { "-m", "pip", "install", "--no-index", "--no-deps", "--force-reinstall", "--no-warn-script-location", "--disable-pip-version-check", pipWheel },
                staging, log, ct, TimeSpan.FromMinutes(5));
            EngineDownloader.TryDelete(pipWheel);
            if (code != 0) throw new Exception("pip couldn't be installed (see the log above).");
            if (await RunAsync(stagedExe, new[] { "-c", "import pip, ssl, sqlite3; print('ok')" }, staging, log, ct, TimeSpan.FromMinutes(1)) != 0)
                throw new Exception("The new Python runtime doesn't work on this PC (see the log above).");

            await StopEnginesUsingAsync(ArchipelagoDir, log, ct);
            RememberStateBeforeUpdate();
            SwapIn(staging, PythonDir);
            State.Python = PythonVersion;
            State.PackagesSignature = null; // packages live inside the runtime folder
            SaveState();
            log($"Python {PythonVersion} ready.");
            NoteChange("runtime", ArchipelagoDir);
        }

        private static async Task InstallArchipelagoCoreAsync(Action<string> log, Action<float> progress, CancellationToken ct)
        {
            EnsureFreeSpace(EngineDir);
            Phase(log, "archipelago", $"Downloading Archipelago {ArchipelagoVersion} from GitHub…");
            string zip = Path.Combine(DownloadsDir, $"Archipelago-{ArchipelagoVersion}.zip");
            await EngineDownloader.DownloadAsync(ArchipelagoUrl, zip, ArchipelagoSha256, Report(progress), ct);
            Stage(log, "Verified. Unpacking Archipelago…");
            string staging = ArchipelagoDir + ".new";
            DeleteDir(staging);
            await ZipFile.ExtractToDirectoryAsync(zip, staging, ct);
            // The archive holds one top folder (Archipelago-0.6.7/).
            var tops = Directory.GetDirectories(staging);
            if (tops.Length != 1 || !File.Exists(Path.Combine(tops[0], "Utils.py")))
                throw new InvalidDataException("The Archipelago download doesn't look like Archipelago's source.");
            string inner = tops[0];
            // Keep what the user added to an older engine: apworlds, YAMLs, settings.
            if (Directory.Exists(ArchipelagoDir))
            {
                foreach (var keep in new[] { "custom_worlds", "Players" })
                {
                    string from = Path.Combine(ArchipelagoDir, keep), to = Path.Combine(inner, keep);
                    if (!Directory.Exists(from)) continue;
                    Directory.CreateDirectory(to);
                    foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
                }
                string hostYaml = Path.Combine(ArchipelagoDir, "host.yaml");
                if (File.Exists(hostYaml)) File.Copy(hostYaml, Path.Combine(inner, "host.yaml"), true);
            }
            Directory.CreateDirectory(Path.Combine(inner, "custom_worlds"));
            Directory.CreateDirectory(Path.Combine(inner, "Players"));
            EngineDownloader.TryDelete(zip);

            await StopEnginesUsingAsync(ArchipelagoDir, log, ct);
            RememberStateBeforeUpdate();
            SwapIn(inner, ArchipelagoDir);
            DeleteDir(staging);
            State.Archipelago = ArchipelagoVersion;
            State.PackagesSignature = null;
            State.LastCheck = null;
            SaveState();
            WriteRunner();
            log($"Archipelago {ArchipelagoVersion} ready.");
            NoteChange("archipelago", ArchipelagoDir);
        }

        /// <summary>Archipelago's requirements minus the GUI and build tools, plus packages worlds need but don't list.</summary>
        private static string FilteredRequirements()
        {
            string file = Path.Combine(ArchipelagoDir, "requirements.txt");
            var lines = new List<string>();
            if (File.Exists(file))
            {
                foreach (var raw in File.ReadAllLines(file))
                {
                    string line = raw.Split('#')[0].Trim();
                    if (line.Length == 0 || line.StartsWith("-")) continue;
                    string name = Regex.Match(line, @"^[A-Za-z0-9_.\-]+").Value.ToLowerInvariant();
                    if (SkippedRequirements.Contains(name) || line.Contains("git+")) continue;
                    lines.Add(line);
                }
            }
            lines.AddRange(ExtraRequirements);
            return string.Join("\n", lines) + "\n";
        }

        private static string PackagesSignature() =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(FilteredRequirements())))[..16];

        private static async Task InstallPackagesCoreAsync(Action<string> log, CancellationToken ct)
        {
            if (!File.Exists(PythonExe)) throw new Exception("Install the Python runtime first.");
            EnsureFreeSpace(EngineDir);
            await StopEnginesUsingAsync(ArchipelagoDir, log, ct);
            // pip changes the runtime in place and can't undo a half-finished install: keep a copy to roll back to.
            string previous = PythonDir + ".previous";
            if (!Directory.Exists(previous))
            {
                log("Keeping a copy of the current runtime in case the update has to be undone…");
                RememberStateBeforeUpdate();
                CopyDir(PythonDir, previous);
            }
            // Exactly the packages (and files) in Atlas's lock, built for this Archipelago version: pip refuses anything else.
            string lockText = ReadResource("AtlasEngine.engine_packages.lock");
            string locked = Regex.Match(lockText, @"requirements-signature:\s*([0-9A-Fa-f]+)").Groups[1].Value;
            if (!string.Equals(locked, PackagesSignature(), StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Atlas's package list was made for different requirements than this Archipelago's, so nothing was installed. Update Atlas, or report this (expected {locked}, found {PackagesSignature()}).");
            EnsureDepth();
            Phase(log, "packages", "Installing Archipelago's Python packages…");
            var (rest, unpacked) = EngineLock.Take(lockText, UnpackedPackage);
            if (unpacked == null || unpacked.Hashes.Count == 0) throw new Exception($"Atlas's package list doesn't pin {UnpackedPackage}, so nothing was installed. Update Atlas, or report this.");
            await UnpackPackageAsync(unpacked, log, ct);
            Stage(log, "Installing Archipelago's Python packages from PyPI (each file checked against Atlas's list of SHA-256 hashes)…");
            string req = Path.Combine(EngineDir, "requirements-atlas.lock.txt");
            await File.WriteAllTextAsync(req, rest, ct);
            int code = await RunAsync(PythonExe, new[] { "-m", "pip", "install", "--require-hashes", "--no-deps", "--only-binary=:all:", "-r", req, "--disable-pip-version-check", "--no-warn-script-location", "--retries", "5", "--timeout", "60" },
                PythonDir, log, ct, TimeSpan.FromMinutes(15));
            if (code != 0) throw new Exception("Some packages couldn't be installed (see the log above).");
            State.PackagesSignature = PackagesSignature();
            SaveState();
            log("Packages ready.");
            NoteChange("packages", ArchipelagoDir);
        }

        /// <summary>
        /// Installs <see cref="UnpackedPackage"/> without its test folders: pip downloads the wheel (only a file matching
        /// the lock's hashes), Atlas checks the hash again and unpacks it into site-packages through SafeZip, leaving
        /// <see cref="UnpackedPackageSkips"/> out. What an earlier install left of the package is removed first.
        /// </summary>
        private static async Task UnpackPackageAsync(EngineLock.Block package, Action<string> log, CancellationToken ct)
        {
            Stage(log, $"Downloading {package.Name} {package.Version} from PyPI (checked against Atlas's list of SHA-256 hashes)…");
            string req = Path.Combine(EngineDir, $"requirements-{package.Name}.lock.txt");
            await File.WriteAllTextAsync(req, package.Text, ct);
            Directory.CreateDirectory(DownloadsDir);
            foreach (var old in Directory.GetFiles(DownloadsDir, $"{package.Name}-*.whl")) EngineDownloader.TryDelete(old);
            int code = await RunAsync(PythonExe, new[] { "-m", "pip", "download", "--require-hashes", "--no-deps", "--only-binary=:all:", "-r", req, "-d", DownloadsDir, "--disable-pip-version-check", "--retries", "5", "--timeout", "60" },
                PythonDir, log, ct, TimeSpan.FromMinutes(10));
            string wheel = code == 0 ? Directory.GetFiles(DownloadsDir, $"{package.Name}-*.whl").FirstOrDefault() : null;
            if (wheel == null) throw new Exception($"{package.Name} couldn't be downloaded (see the log above).");
            string hash = EngineDownloader.Sha256Of(wheel);
            if (!package.Hashes.Contains(hash)) throw new InvalidDataException($"{Path.GetFileName(wheel)} isn't a file Atlas's package list allows (SHA-256 {hash}).");
            log($"Verified. Unpacking {package.Name} without its test folders (their paths are too long for Windows)…");
            string sitePackages = Path.Combine(PythonDir, "Lib", "site-packages");
            // The package's top-level folders and files from an earlier install go first: the unpack never overwrites.
            using (var zip = SafeZip.Open(wheel))
            {
                foreach (string top in zip.Entries.Select(e => e.FullName.Split('/')[0]).Distinct())
                {
                    string path = Path.Combine(sitePackages, top);
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    else if (File.Exists(path)) File.Delete(path);
                }
            }
            SafeZip.UnpackTo(wheel, sitePackages, SafeZip.TextLimit, SafeZip.TextTotal,
                include: name => !UnpackedPackageSkips.Any(skip => name.StartsWith(skip, StringComparison.OrdinalIgnoreCase)));
            EngineDownloader.TryDelete(wheel);
            log($"{package.Name} {package.Version} ready.");
        }

        /// <summary>
        /// Worlds that failed to load and declare their own packages (a requirements.txt bundled with Archipelago or
        /// inside the apworld). Only declared requirements are installed: a package name is never guessed from an
        /// error, which could pull in a look-alike package.
        /// </summary>
        public static List<(string World, string Requirements)> InstallableWorldPackages(EngineInstall install, bool includeTried = false)
        {
            var result = new List<(string, string)>();
            var check = LastCheck(install);
            if (install.Mode != EngineMode.Portable || check?.FailedWorlds == null) return result;
            foreach (var world in check.FailedWorlds)
            {
                string text = WorldRequirements(world, check);
                if (text == null) continue;
                if (!includeTried && State.WorldPackagesTried != null && State.WorldPackagesTried.TryGetValue(world, out var tried) && tried == Signature(text)) continue;
                result.Add((world, text));
            }
            return result;
        }

        private static string WorldRequirements(string world, EngineCheckResult check)
        {
            string bundled = Path.Combine(ArchipelagoDir, "worlds", world, "requirements.txt");
            if (File.Exists(bundled)) return File.ReadAllText(bundled);
            string apworld = check.FailedDetails != null && check.FailedDetails.TryGetValue(world, out var info) && info.IsZip ? info.Path : null;
            apworld ??= Path.Combine(ArchipelagoDir, "custom_worlds", world + ".apworld");
            if (!File.Exists(apworld)) return null;
            try
            {
                using var zip = SafeZip.Open(apworld);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Count(c => c == '/') == 1 && e.Name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase));
                return entry == null ? null : zip.ReadText(entry);
            }
            catch { return null; }
        }

        private static string Signature(string text) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")))[..16];

        /// <summary>
        /// Makes a world's own requirements safe to hand to pip: only package requirements stay. pip options (an extra
        /// package index, "-r"/"-e", install options) and local files are dropped and reported, so a world can't point pip
        /// anywhere else. "name @ git+https://github.com/owner/repo@ref" becomes GitHub's source archive for that exact
        /// ref; other git sources are dropped (reported) because git isn't present.
        /// </summary>
        internal static string SafeWorldRequirements(string requirements, Action<string> log)
        {
            var output = new List<string>();
            foreach (var raw in (requirements ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("-"))
                {
                    log("  Skipped (a pip option, not a package): " + line);
                    continue;
                }
                // Options after a requirement ("pkg==1.0 --hash=…", "--config-settings=…") are dropped; the requirement stays.
                var options = Regex.Match(line, @"\s+--?[A-Za-z]");
                if (options.Success)
                {
                    log("  Dropped pip options from: " + line);
                    line = line.Substring(0, options.Index).Trim();
                }
                var m = Regex.Match(line, @"^([A-Za-z0-9_.\-]+)\s*@\s*git\+https://github\.com/([^@\s#]+?)(?:\.git)?@([^\s#]+)");
                if (m.Success)
                {
                    output.Add($"{m.Groups[1].Value} @ https://github.com/{m.Groups[2].Value}/archive/{m.Groups[3].Value}.zip");
                    continue;
                }
                if (line.Contains("git+"))
                {
                    log("  Skipped (needs git): " + line);
                    continue;
                }
                // Only a package name (with extras, a version and markers), or a direct https link to a package.
                bool direct = Regex.IsMatch(line, @"^[A-Za-z0-9_.\-]+(\[[^\]]*\])?\s*@\s*https://", RegexOptions.IgnoreCase);
                bool named = Regex.IsMatch(line, @"^[A-Za-z0-9_.\-]+(\[[^\]]*\])?\s*([<>=!~;].*)?$");
                if (!direct && (!named || line.Contains("://") || line.Contains(" --")))
                {
                    log("  Skipped (not a plain package requirement): " + line);
                    continue;
                }
                output.Add(line);
            }
            return string.Join("\n", output);
        }

        /// <summary>
        /// Installs the declared packages of worlds that failed to load, one world at a time (one failure can't block
        /// the rest), with the runtime copied first so a bad install rolls back. Returns true if anything was installed.
        /// </summary>
        private static async Task<bool> InstallWorldPackagesCoreAsync(EngineInstall install, Action<string> log, CancellationToken ct, bool force)
        {
            var targets = InstallableWorldPackages(install, includeTried: force);
            if (targets.Count == 0) return false;
            EnsureFreeSpace(EngineDir);
            await StopEnginesUsingAsync(ArchipelagoDir, log, ct);
            string previous = PythonDir + ".previous";
            if (!Directory.Exists(previous))
            {
                log("Keeping a copy of the current runtime in case the update has to be undone…");
                RememberStateBeforeUpdate();
                CopyDir(PythonDir, previous);
            }
            bool any = false;
            State.WorldPackagesTried ??= new Dictionary<string, string>();
            Phase(log, "world-packages", "Installing the packages some games need…");
            foreach (var (world, requirements) in targets)
            {
                log($"Installing the packages {world} declares…");
                string file = Path.Combine(EngineDir, $"requirements-{world}.txt");
                await File.WriteAllTextAsync(file, SafeWorldRequirements(requirements, log), ct);
                int code = await RunAsync(PythonExe, new[] { "-m", "pip", "install", "-r", file, "--prefer-binary", "--disable-pip-version-check", "--no-warn-script-location", "--retries", "5", "--timeout", "60" },
                    PythonDir, log, ct, TimeSpan.FromMinutes(10));
                State.WorldPackagesTried[world] = Signature(requirements);
                if (code == 0) { any = true; log($"Packages for {world} installed."); NoteChange("world-packages", ArchipelagoDir, world); }
                else log($"The packages for {world} couldn't be installed; {world} won't be available (everything else is unaffected).");
            }
            SaveState();
            return any;
        }

        private static void CopyDir(string from, string to)
        {
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
        }

        /// <summary>Installs the tested Universal Tracker. Other copies are moved to the engine's backups folder (they'd conflict), and put back if anything fails.</summary>
        private static async Task InstallTrackerCoreAsync(EngineInstall install, Action<string> log, Action<float> progress, CancellationToken ct)
        {
            string worlds = install.WorldsDir ?? throw new Exception("The engine has no worlds folder.");
            RequireWriteConsent(install);
            Phase(log, "tracker", $"Downloading Universal Tracker {TrackerVersion} from GitHub…");
            string temp = Path.Combine(DownloadsDir, "tracker.apworld");
            await EngineDownloader.DownloadAsync(TrackerUrl, temp, TrackerSha256, Report(progress), ct);
            await StopEnginesUsingAsync(install.Root, log, ct);
            var moved = new List<(string From, string To)>();
            try
            {
                foreach (var other in install.FindAllTrackers())
                {
                    Directory.CreateDirectory(BackupsDir);
                    string backup = Path.Combine(BackupsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(other)}");
                    File.Move(other, backup);
                    moved.Add((other, backup));
                    log($"Moved the other tracker copy {Path.GetFileName(other)} to {backup}.");
                }
                Directory.CreateDirectory(worlds);
                File.Move(temp, Path.Combine(worlds, "tracker.apworld"), true);
                foreach (var (from, to) in moved) RecordChange(install, "moved", from, to);
                RecordChange(install, "added", Path.Combine(worlds, "tracker.apworld"));
            }
            catch
            {
                // Put back what was moved in the user's own install; say so if something can't be.
                foreach (var (from, to) in moved)
                {
                    try { if (!File.Exists(from)) File.Move(to, from); }
                    catch (Exception ex) { Logger.LogWarning($"Couldn't move {Path.GetFileName(from)} back from {to}: {ex.Message}"); }
                }
                throw;
            }
            if (install.Mode == EngineMode.Portable) { State.Tracker = TrackerVersion; SaveState(); }
            log($"Universal Tracker {TrackerVersion} installed in {worlds}.");
            NoteChange("tracker", install.Root);
        }

        // =====================================================================
        // Bridge
        // =====================================================================

        public static string BridgeScript() => ReadResource("AtlasEngine.atlas_bridge.py");

        private static string RunnerScript() => ReadResource("AtlasEngine.atlas_run.py");

        private static string ReadResource(string name)
        {
            using var stream = typeof(AtlasEngine).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Missing embedded resource " + name);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static bool BridgeIsCurrent(EngineInstall install)
        {
            string apworld = Path.Combine(install.WorldsDir, "UltimateBridge.apworld");
            if (!File.Exists(apworld)) return false;
            if (install.Mode == EngineMode.Portable && (!File.Exists(RunnerPath) || File.ReadAllText(RunnerPath) != RunnerScript())) return false;
            return ReadInstalledBridge(apworld) == BridgeScript();
        }

        /// <summary>Writes the bridge apworld (and the portable runner) when they differ from this build's.</summary>
        public static void InstallBridge(EngineInstall install, Action<string> log = null)
        {
            string worlds = install.WorldsDir;
            if (worlds == null) return;
            if (!MayWriteTo(install))
            {
                if (!BridgeIsCurrent(install)) log?.Invoke("Atlas needs your OK before it adds its bridge to your Archipelago install (Atlas Engine → Allow…).");
                return;
            }
            try
            {
                Directory.CreateDirectory(worlds);
                string looseDir = Path.Combine(worlds, "UltimateBridge");
                if (Directory.Exists(looseDir)) Directory.Delete(looseDir, true);
                if (install.Mode == EngineMode.Portable) WriteRunner();
                string apworld = Path.Combine(worlds, "UltimateBridge.apworld");
                string script = BridgeScript();
                // Rewrite only when changed: another slot's bridge may be loading this file right now.
                if (File.Exists(apworld) && ReadInstalledBridge(apworld) == script) return;
                string temp = apworld + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    using var writer = new StreamWriter(archive.CreateEntry("UltimateBridge/__init__.py").Open(), new UTF8Encoding(false));
                    writer.Write(script);
                }
                File.Move(temp, apworld, true);
                RecordChange(install, "added", apworld);
                log?.Invoke("Atlas bridge installed.");
            }
            catch (Exception ex)
            {
                log?.Invoke("Couldn't install the Atlas bridge: " + ex.Message);
                Logger.LogWarning("[Atlas Engine] Couldn't install the bridge: " + ex.Message);
            }
        }

        private static void WriteRunner()
        {
            Directory.CreateDirectory(EngineDir);
            string script = RunnerScript();
            if (!File.Exists(RunnerPath) || File.ReadAllText(RunnerPath) != script) File.WriteAllText(RunnerPath, script);
        }

        private static string ReadInstalledBridge(string apworld)
        {
            try
            {
                using var zip = SafeZip.Open(apworld);
                var entry = zip.GetEntry("UltimateBridge/__init__.py");
                return entry == null ? null : zip.ReadText(entry);
            }
            catch { return null; }
        }

        // =====================================================================
        // Health check
        // =====================================================================

        public static async Task<EngineCheckResult> RunCheckAsync(EngineInstall install, Action<string> log, CancellationToken ct)
        {
            Phase(log, "check", "Running the health check (loading every game takes a few seconds)…");
            InstallBridge(install);
            EngineCheckResult result = null;
            var errors = new StringBuilder();
            try
            {
                int code = await RunAsync(install.StartInfo("AtlasCheck"), install.Root, line =>
                {
                    string t = line.Trim();
                    if (t.StartsWith("{") && t.Contains("\"python\""))
                    {
                        try { result = JsonConvert.DeserializeObject<EngineCheckResult>(t); } catch { } // not every output line is a reply
                    }
                }, err => { if (errors.Length < 4000) errors.AppendLine(err); }, ct, TimeSpan.FromMinutes(3));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.AppendLine(ex.Message); }

            result ??= new EngineCheckResult { Error = errors.Length > 0 ? errors.ToString() : "The engine exited without reporting." };
            result.Checked = DateTime.Now;
            result.Root = install.Root;
            if (install.Mode == EngineMode.Portable) { State.LastCheck = result; SaveState(); }
            else lock (_existingChecks) _existingChecks[install.Root] = result;

            if (!result.Passed) log?.Invoke("Health check failed: " + result.Problem);
            else
            {
                log?.Invoke($"Health check passed: Archipelago {result.Archipelago}, Universal Tracker {result.Tracker}, Python {result.Python}, {result.Games.Count} games" +
                            (result.Smoke != null ? $"; logic test on {result.Smoke.Game}: {result.Smoke.Locations} locations, {result.Smoke.InLogic} in starting logic" +
                             (result.Smoke.GoalWithAllItems == true ? ", goal reachable with every item (go mode detection works)." : result.Smoke.GoalWithAllItems == false ? ", but its goal wasn't reachable even with every item (go mode suggestions won't work)." : ".") : "."));
                if (!result.TrackerTested) log?.Invoke($"Note: Universal Tracker {result.Tracker} hasn't been tested with Atlas (tested: {string.Join(", ", result.TestedTracker)}).");
                if (result.FailedWorlds.Count > 0) log?.Invoke($"{result.FailedWorlds.Count} worlds couldn't load (they need extra packages or a newer Archipelago): {string.Join(", ", result.FailedWorlds.Take(12))}");
            }
            NotifyChanged();
            return result;
        }

        // =====================================================================
        // Game apworlds
        // =====================================================================

        /// <summary>The game an apworld provides, read from its manifest or its world class. Null if unknown.</summary>
        public static string GameOfApworld(string file)
        {
            try
            {
                using var zip = SafeZip.Open(file);
                var manifest = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("archipelago.json", StringComparison.OrdinalIgnoreCase));
                if (manifest != null)
                {
                    var game = JObject.Parse(zip.ReadText(manifest))["game"]?.ToString();
                    if (!string.IsNullOrEmpty(game)) return game;
                }
                var init = zip.Entries.Where(e => e.FullName.Count(c => c == '/') == 1 && e.FullName.EndsWith("/__init__.py")).FirstOrDefault();
                if (init == null) return null;
                string src = zip.ReadText(init);
                // class XWorld(World): ... game = "Name"  /  game: str = "Name"  /  game: ClassVar[str] = "Name"
                // NonBacktracking: linear time in the source's length (an apworld's source comes from outside Atlas).
                var m = Regex.Match(src, @"class\s+\w+\s*\([^)]*World[^)]*\)\s*:[\s\S]*?^\s+game\s*(?::\s*[\w\.\[\]]+\s*)?=\s*[""']([^""']+)[""']", RegexOptions.Multiline | RegexOptions.NonBacktracking);
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>Apworlds for a game in an existing Archipelago install (to copy into the portable engine).</summary>
        public static List<string> FindApworldsFor(string game, IEnumerable<string> installRoots)
        {
            var found = new List<string>();
            foreach (var root in installRoots)
            {
                foreach (var dir in new[] { Path.Combine(root, "custom_worlds"), Path.Combine(root, "lib", "worlds") })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in EngineInstall.SafeFiles(dir, "*.apworld"))
                        if (string.Equals(GameOfApworld(file), game, StringComparison.OrdinalIgnoreCase)) found.Add(file);
                }
            }
            return found;
        }

        /// <summary>Adds an apworld to the engine (exclusively, pausing engines if a file they use is replaced), then health-checks it.</summary>
        public static Task<bool> InstallApworldAsync(EngineInstall install, string sourceFile, Action<string> log, CancellationToken ct) =>
            ExclusiveAsync(async () =>
            {
                try
                {
                    string target = Path.Combine(install.WorldsDir ?? throw new Exception("The engine has no worlds folder."), Path.GetFileName(sourceFile));
                    if (File.Exists(target)) await StopEnginesUsingAsync(install.Root, log, ct);
                    string installed = InstallApworld(install, sourceFile, log);
                    var check = await RunCheckAsync(install, log, ct);
                    string game = GameOfApworld(installed);
                    if (game != null && check.FailedWorlds.Contains(game))
                    {
                        string why = check.FailedDetails.TryGetValue(game, out var info) ? (info.Error ?? info.MissingModule) : null;
                        log($"{game} was added but doesn't load" + (why != null ? ": " + why : ".") + " The engine runs without it.");
                        return false;
                    }
                    return check.Passed;
                }
                catch (OperationCanceledException) { log("Cancelled."); return false; }
                catch (Exception ex) { log("Couldn't add the apworld: " + ex.Message); return false; }
                finally { NotifyChanged(); }
            }, ct);

        /// <summary>Copies an apworld into the engine. A file of the same name is moved to backups first.</summary>
        private static string InstallApworld(EngineInstall install, string sourceFile, Action<string> log)
        {
            string worlds = install.WorldsDir ?? throw new Exception("The engine has no worlds folder.");
            RequireWriteConsent(install);
            Directory.CreateDirectory(worlds);
            string target = Path.Combine(worlds, Path.GetFileName(sourceFile));
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(sourceFile), StringComparison.OrdinalIgnoreCase)) return target;
            // Another file for the same game would conflict (Archipelago loads only one): move it to backups.
            string game = GameOfApworld(sourceFile);
            if (game != null)
            {
                foreach (var other in EngineInstall.SafeFiles(worlds, "*.apworld"))
                {
                    if (string.Equals(Path.GetFileName(other), Path.GetFileName(target), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(GameOfApworld(other), game, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(BackupsDir);
                    string aside = Path.Combine(BackupsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(other)}");
                    File.Move(other, aside);
                    RecordChange(install, "moved", other, aside);
                    log?.Invoke($"Moved the other {game} apworld ({Path.GetFileName(other)}) to the engine's backups folder.");
                }
            }
            if (File.Exists(target))
            {
                Directory.CreateDirectory(BackupsDir);
                string aside = Path.Combine(BackupsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(target)}");
                File.Move(target, aside);
                RecordChange(install, "moved", target, aside);
            }
            File.Copy(sourceFile, target);
            RecordChange(install, "added", target);
            log?.Invoke($"Installed {Path.GetFileName(sourceFile)} ({GameOfApworld(target) ?? "unknown game"}).");
            NoteChange("apworld", install.Root, GameOfApworld(target));
            NotifyChanged();
            return target;
        }

        // =====================================================================
        // Processes
        // =====================================================================

        private static Action<long, long> Report(Action<float> progress) =>
            progress == null ? null : (done, total) => progress(total > 0 ? (float)done / total : -1f);

        private static Task<int> RunAsync(string exe, IEnumerable<string> args, string workDir, Action<string> log, CancellationToken ct, TimeSpan timeout) =>
            RunAsync(SetupStartInfo(exe, args, workDir), null, line => log?.Invoke("  " + line), line => log?.Invoke("  " + line), ct, timeout);

        /// <summary>How a setup step (Python, pip) is started.</summary>
        internal static ProcessStartInfo SetupStartInfo(string exe, IEnumerable<string> args, string workDir)
        {
            var info = new ProcessStartInfo { FileName = exe, WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            KeepFilesInEngineFolder(info);
            return info;
        }

        /// <summary>
        /// Keeps an engine process's own files in the engine folder. Python's temporary files, Archipelago's cache (data
        /// packages, a client id) and pip's downloads would otherwise go to %TEMP%, %LocalAppData% and %AppData%, outside
        /// Atlas's folder. pip also stops reading the user's own pip settings, so they can't change what Atlas installs.
        /// Every engine process starts through this (EngineInstall.StartInfo, SetupStartInfo; a guard rail checks).
        /// </summary>
        internal static void KeepFilesInEngineFolder(ProcessStartInfo info)
        {
            string local = Path.Combine(UserDir, "Local"), roaming = Path.Combine(UserDir, "Roaming");
            foreach (var dir in new[] { TempDir, local, roaming }) Directory.CreateDirectory(dir);
            RemoveOldTemp();
            var env = info.Environment;
            foreach (var name in env.Keys.Where(k => k.StartsWith("PIP_", StringComparison.OrdinalIgnoreCase)).ToList()) env.Remove(name);
            env["TEMP"] = TempDir;
            env["TMP"] = TempDir;
            // platformdirs (used by Archipelago and pip) reads the WIN_PD_OVERRIDE_* names; other code reads the usual ones.
            env["LOCALAPPDATA"] = local;
            env["WIN_PD_OVERRIDE_LOCAL_APPDATA"] = local;
            env["APPDATA"] = roaming;
            env["WIN_PD_OVERRIDE_APPDATA"] = roaming;
            env["PIP_CACHE_DIR"] = Path.Combine(EngineDir, "pip_cache");
            env["PIP_CONFIG_FILE"] = "nul"; // Python's os.devnull: pip reads no pip.ini at all
            env["PYTHONNOUSERSITE"] = "1"; // nothing from the user's own Python packages folder
        }

        /// <summary>Removes what engine runs that crashed left in the temp folder (anything over a day old).</summary>
        private static void RemoveOldTemp()
        {
            try
            {
                foreach (var entry in new DirectoryInfo(TempDir).EnumerateFileSystemInfos())
                {
                    if (DateTime.UtcNow - entry.LastWriteTimeUtc < TimeSpan.FromDays(1)) continue; // wall clock: a file's time
                    try
                    {
                        if (entry is DirectoryInfo dir) dir.Delete(recursive: true);
                        else entry.Delete();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // still in use: next time
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogDebug("[Atlas Engine] Couldn't tidy the engine's temp folder: " + ex.Message);
            }
        }

        /// <summary>Runs one bridge component with an optional request line on stdin.</summary>
        internal static Task<int> RunComponentAsync(EngineInstall install, string component, string request, Action<string> onOut, Action<string> onErr, CancellationToken ct, TimeSpan timeout)
        {
            InstallBridge(install);
            return RunAsync(install.StartInfo(component), install.Root, onOut, onErr, ct, timeout, request);
        }

        private static async Task<int> RunAsync(ProcessStartInfo info, string engineRoot, Action<string> onOut, Action<string> onErr, CancellationToken ct, TimeSpan timeout, string stdin = null)
        {
            using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult(true);
            process.Start();
            ProcessJob.Track(process, engineRoot);
            // Its output a line at a time, without trusting a line's length: an answer is cut at AnswerLimit, a line of
            // its log at LogLimit (each says how much was cut).
            var output = BoundedLineReader.ForEachAsync(process.StandardOutput, BoundedLineReader.AnswerLimit, onOut);
            var errors = BoundedLineReader.ForEachAsync(process.StandardError, BoundedLineReader.LogLimit, onErr);
            try
            {
                if (stdin != null) await process.StandardInput.WriteLineAsync(stdin);
                process.StandardInput.Close(); // a component waiting on stdin sees the request, then EOF
            }
            catch { } // it already exited (its exit code says how)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await exited.Task.WaitAsync(timeoutCts.Token);
                await Task.WhenAll(output, errors).WaitAsync(timeoutCts.Token); // its last lines (bounded, like the run)
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { } // it exited meanwhile
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"{Path.GetFileName(info.FileName)} took longer than {timeout.TotalMinutes:0} minutes and was stopped.");
            }
        }
    }
}
