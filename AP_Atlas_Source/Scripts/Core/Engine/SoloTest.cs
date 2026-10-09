#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.Games;
using AP_Atlas.Core.PopTracker;
using AP_Atlas.Core.Reports;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>A pack the dialog chose for the test: a GitHub repository, or none (null) to go on without a pack.</summary>
    public sealed record PackChoice(string? Repo);

    /// <summary>What the solo test needs from the window: the pieces that live on its pages.</summary>
    public sealed class SoloTestHooks
    {
        public required AppSettings Settings { get; init; }
        /// <summary>A line for the user (a card), when the window has one.</summary>
        public Action<string>? Notice { get; init; }
        /// <summary>Installs the newest release of the game's apworld (the permission and trust questions asked as always); false when it didn't happen.</summary>
        public required Func<string, Task<bool>> InstallApworldAsync { get; init; }
        /// <summary>Lets the user pick a pack from GitHub's results (or none).</summary>
        public required Func<string, Task<PackChoice?>> ChoosePackAsync { get; init; }
        public required Func<string, string, Task<bool>> InstallPackAsync { get; init; }
        public required Action<MultiworldProfile> AddProfile { get; init; }
        public required Action<MultiworldProfile> RemoveProfile { get; init; }
        /// <summary>Connects a slot of a multiworld through the window and waits for its view (null when it didn't connect).</summary>
        public required Func<MultiworldProfile, string, Task<SlotTrackerControl?>> ConnectSlotAsync { get; init; }
        /// <summary>The report's scrubber, taken before the test's multiworld exists (its name and address stay readable).</summary>
        public required Func<Scrubber> Scrubber { get; init; }
        public required Func<string> AtlasVersionLine { get; init; }
    }

    public sealed record SoloTestProgress(SoloTestStep Step, SoloStepOutcome Outcome, string Detail, string? LogLine);

    /// <summary>
    /// "Test this game…": the chain that gets a game from nothing to a connected, scored slot with the user watching: the
    /// apworld in the engine, a map pack, a YAML with the game's default options, a one-player seed generated in the
    /// engine, Archipelago's server on this PC only, a slot connected with that YAML, Atlas's logic scored against the
    /// seed's own spheres and the pack's coverage counted, and a report written for the owner to annotate. One test at a
    /// time; the server lives until it's stopped or Atlas closes.
    /// </summary>
    public sealed class SoloTestRunner
    {
        public const string SlotName = SoloYaml.PlayerName;
        public static readonly TimeSpan TemplateLimit = TimeSpan.FromMinutes(3), GenerateLimit = TimeSpan.FromMinutes(25), HostStart = TimeSpan.FromSeconds(90), ConnectWait = TimeSpan.FromSeconds(60), LogicSettle = TimeSpan.FromSeconds(180);

        private static readonly List<SoloTestRunner> _all = new();

        /// <summary>Raised (on the main thread) when a test starts or ends, or its server stops.</summary>
        public static event Action? StateChanged;

        /// <summary>The test running or hosting now, if any (one at a time).</summary>
        public static SoloTestRunner? Running => _all.LastOrDefault(r => r.Busy || r.ServerAddress != null);

        public static string SoloFolder(string game) => Path.Combine(AtlasEngine.EngineDir, "solo", GameFiles.SafeName(game));

        public static string ReportsFolder => Path.Combine(DataManager.GetDataDirectory(), CrashReportService.ReportsFolder, SoloTestReport.Folder);

        private readonly List<SoloStepRecord> _steps = new();
        private EngineProcess? _host;
        private string? _yamlPath, _multidataPath, _reportMd, _reportJson;
        private SoloTestResult? _result;
        private Scrubber? _scrubber;
        private SoloOwnerNotes? _notes;

        public string Game { get; }
        public bool Busy { get; private set; }
        public string? ServerAddress { get; private set; }
        public MultiworldProfile? Profile { get; private set; }
        public SoloTestResult? Result => _result;
        public string? ReportPath => _reportMd;

        public SoloTestRunner(string game)
        {
            Game = game;
            _all.Add(this);
        }

        /// <summary>Runs the whole chain; a failed step ends it, the report is written either way. Runs on the main thread (the hooks touch the window).</summary>
        public async Task<SoloTestResult> RunAsync(SoloTestHooks hooks, IProgress<SoloTestProgress>? progress, CancellationToken ct)
        {
            if (Busy) throw new InvalidOperationException("This test is already running.");
            Busy = true;
            StateChanged?.Invoke();
            var started = DateTime.Now; // wall clock: shown in the report, never compared
            _scrubber = hooks.Scrubber();
            _steps.Clear();
            var install = AtlasEngine.Current;
            string? apworldVersion = null, apworldSource = null, apworldChecksum = null;
            LoadedPack? pack = null;
            SoloGeneration? generation = null;
            SoloLogic? logic = null;
            SoloPins? pins = null;
            SoloKeyItems? keyItems = null;
            SoloScripts? scripts = null;
            SlotTrackerControl? slot = null;
            int yamlOptions = 0;
            string? yamlFrom = null;
            try
            {
                bool failed = !await StepAsync(SoloTestStep.Apworld, progress, ct, async () =>
                {
                    if (install == null || !install.CanLaunch) return Failed("the Atlas Engine isn't set up");
                    if (install.Mode != EngineMode.Portable) return Failed("the solo test runs on Atlas's own engine only (an Archipelago install of your own would read its host.yaml)");
                    if (AtlasEngine.ProblemWith(install) is { } problem) return Failed(problem);
                    var check = AtlasEngine.LastCheck(install);
                    if (check == null)
                    {
                        progress?.Report(new SoloTestProgress(SoloTestStep.Apworld, SoloStepOutcome.Running, "checking the engine", null));
                        check = await AtlasEngine.RunCheckAsync(install, line => progress?.Report(new SoloTestProgress(SoloTestStep.Apworld, SoloStepOutcome.Running, "checking the engine", line)), ct);
                    }
                    bool has = check?.Games.Contains(Game, StringComparer.OrdinalIgnoreCase) == true;
                    if (!has)
                    {
                        progress?.Report(new SoloTestProgress(SoloTestStep.Apworld, SoloStepOutcome.Running, "installing the apworld", null));
                        if (!await hooks.InstallApworldAsync(Game)) return Failed("the apworld wasn't installed (the Games page's log says why, or the project wasn't trusted)");
                    }
                    var copies = ApworldSources.InstalledCopies(install, Game);
                    foreach (var copy in copies)
                    {
                        var (repo, tag) = ApworldSources.KnownSourceOf(copy.Sha256);
                        apworldVersion ??= tag;
                        apworldSource ??= repo != null ? "github.com/" + repo : null;
                        apworldChecksum ??= copy.Sha256;
                    }
                    return has ? Skipped("already in the engine" + (apworldVersion != null ? " (" + apworldVersion + ")" : "")) : Done("installed" + (apworldVersion != null ? " " + apworldVersion : ""));
                });
                if (!failed) failed = !await StepAsync(SoloTestStep.MapPack, progress, ct, async () =>
                {
                    pack = InstalledPackFor(Game);
                    if (pack != null) return Skipped("using " + pack.Manifest.Name + " " + pack.Manifest.GetActualVersion());
                    progress?.Report(new SoloTestProgress(SoloTestStep.MapPack, SoloStepOutcome.Running, "looking for a pack on GitHub", null));
                    var choice = await hooks.ChoosePackAsync(Game);
                    if (choice?.Repo == null) return Done("no pack: Pins, Key Items and Scripts aren't scored");
                    if (!await hooks.InstallPackAsync(Game, choice.Repo)) return Failed("the pack from github.com/" + choice.Repo + " wasn't installed");
                    pack = InstalledPackFor(Game);
                    return pack != null ? Done("installed " + pack.Manifest.Name) : Failed("the installed pack isn't for " + Game);
                });
                if (!failed) failed = !await StepAsync(SoloTestStep.Yaml, progress, ct, async () =>
                {
                    string folder = SoloFolder(Game);
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    string players = Path.Combine(folder, "players"), output = Path.Combine(folder, "output");
                    Directory.CreateDirectory(players);
                    Directory.CreateDirectory(output);
                    var answer = await ComponentAsync(install!, "AtlasYamlTemplate", new JObject { ["game"] = Game, ["player_name"] = SlotName, ["output_dir"] = players }, TemplateLimit, SoloTestStep.Yaml, progress, ct);
                    if (answer == null || (bool?)answer["ok"] != true) return Failed(answer?["error"]?.ToString() ?? "the engine gave no answer");
                    _yamlPath = answer["path"]?.ToString();
                    var check = SoloYaml.Inspect(_yamlPath != null && File.Exists(_yamlPath) ? await File.ReadAllTextAsync(_yamlPath, ct) : null);
                    if (!check.Ok) return Failed("the YAML isn't usable: " + string.Join("; ", check.Problems));
                    yamlOptions = check.Options;
                    yamlFrom = answer["from"]?.ToString();
                    apworldChecksum ??= answer["data_checksum"]?.ToString();
                    // The world's own version, when the install didn't record a release (an install from the Games page doesn't).
                    apworldVersion ??= answer["world_version"]?.ToString() is { Length: > 0 } worldVersion ? worldVersion + " (the world's own number)" : null;
                    return Done(check.Options + " options at their defaults, in " + Path.GetRelativePath(DataManager.GetDataDirectory(), _yamlPath!));
                });
                if (!failed) failed = !await StepAsync(SoloTestStep.Generate, progress, ct, async () =>
                {
                    string folder = SoloFolder(Game);
                    var request = new JObject { ["players_dir"] = Path.Combine(folder, "players"), ["output_dir"] = Path.Combine(folder, "output"), ["spoiler"] = 3, ["skip_patch_games"] = new JArray() };
                    var answer = await ComponentAsync(install!, "AtlasGenerate", request, GenerateLimit, SoloTestStep.Generate, progress, ct);
                    bool patchSkipped = false;
                    if (answer != null && (bool?)answer["ok"] != true && NeedsPatchSkip(answer))
                    {
                        // The world's output wants a file this PC doesn't have (a base ROM): generated again without its patch output.
                        progress?.Report(new SoloTestProgress(SoloTestStep.Generate, SoloStepOutcome.Running, "generating again without the game's patch output", null));
                        request["skip_patch_games"] = new JArray(Game);
                        answer = await ComponentAsync(install!, "AtlasGenerate", request, GenerateLimit, SoloTestStep.Generate, progress, ct);
                        patchSkipped = true;
                    }
                    if (answer == null || (bool?)answer["ok"] != true)
                    {
                        string error = answer?["error"]?.ToString() ?? "the engine gave no answer";
                        generation = new SoloGeneration(null, null, 0, 0, 0, 0, (double?)answer?["seconds"] ?? 0, patchSkipped, error, yamlOptions, yamlFrom);
                        return Failed(error);
                    }
                    _multidataPath = answer["multidata"]?.ToString();
                    var player = (answer["players"] as JArray)?.OfType<JObject>().FirstOrDefault();
                    generation = new SoloGeneration(answer["seed_name"]?.ToString(), answer["seed"]?.ToString(), (int?)player?["locations"] ?? 0, (int?)player?["items"] ?? 0,
                        (int?)answer["spheres"] ?? 0, (int?)answer["sphere0"] ?? 0, (double?)answer["seconds"] ?? 0, patchSkipped, null, yamlOptions, yamlFrom);
                    return Done("seed " + generation.SeedName + ": " + generation.Locations + " locations, " + generation.Spheres + " spheres" + (patchSkipped ? "; the patch output was skipped" : ""));
                });
                if (!failed) failed = !await StepAsync(SoloTestStep.Host, progress, ct, async () =>
                {
                    AtlasEngine.InstallBridge(install!);
                    _host = EngineProcess.Start(install!.StartInfo("AtlasHost"), install.Root, line => Logger.LogDebug("[solo host] " + line));
                    _host.Exited += code => progress?.Report(new SoloTestProgress(SoloTestStep.Host, SoloStepOutcome.Failed, "the test server stopped (exit code " + code + ")", null));
                    var answer = await _host.AskAsync(new JObject { ["multidata"] = _multidataPath, ["host"] = "127.0.0.1", ["port"] = 0 }, HostStart, ct);
                    if (answer.Reply == null) return Failed(answer.Why ?? "the server didn't start");
                    if (answer.Reply["event"]?.ToString() != "hosting") return Failed(answer.Reply["error"]?.ToString() ?? "the server didn't start");
                    // Plain ws: a server on this PC has no certificate (a bare address would be tried as wss first).
                    ServerAddress = "ws://127.0.0.1:" + answer.Reply["port"];
                    return Done("serving on " + ServerAddress);
                });
                if (!failed) failed = !await StepAsync(SoloTestStep.Connect, progress, ct, async () =>
                {
                    var profile = new MultiworldProfile { Name = "Solo test: " + Game, ServerUrl = ServerAddress ?? "" };
                    profile.Slots.Clear();
                    profile.Slots.Add(SlotName);
                    // Linked before the connect, so the slot's first logic start already rebuilds the world from this YAML.
                    hooks.Settings.SlotYamlPaths[Annotations.SlotKey(profile.Id, SlotName)] = _yamlPath ?? "";
                    DataManager.SaveSettings(hooks.Settings);
                    hooks.AddProfile(profile);
                    Profile = profile;
                    slot = await hooks.ConnectSlotAsync(profile, SlotName);
                    if (slot == null) return Failed("the slot didn't connect to the test server");
                    var until = Deadline.In(LogicSettle);
                    while (!slot.LogicSettled && slot.EngineProblem == null && !until.Passed)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(200, ct);
                    }
                    string problem = slot.EngineProblem?.Message ?? "";
                    return slot.EngineProblem != null ? Failed("logic didn't start: " + problem) : slot.LogicSettled ? Done(slot.ActiveLogicCount + " of " + slot.TotalLocationsCount + " locations in logic at connect") : Failed("logic didn't settle in time");
                });
                if (!failed) await StepAsync(SoloTestStep.Score, progress, ct, async () =>
                {
                    var parts = new List<string>();
                    progress?.Report(new SoloTestProgress(SoloTestStep.Score, SoloStepOutcome.Running, "replaying the seed's spheres", null));
                    var report = await SeedVerifier.VerifyAsync(install!, _multidataPath!, line => progress?.Report(new SoloTestProgress(SoloTestStep.Score, SoloStepOutcome.Running, "replaying the seed's spheres", line)), ct);
                    var player = report.Players.FirstOrDefault(p => p.Name == SlotName) ?? report.Players.FirstOrDefault();
                    logic = new SoloLogic(player?.Error == null ? player?.Exact : null, player?.Steps.Count ?? 0, player?.Late ?? 0, player?.Early ?? 0, player?.Verdict ?? (report.Error ?? "the seed test didn't run"),
                        player?.ChecksumMatch ?? slot!.ApworldMatchesSeed, slot!.ActiveLogicCount, slot.TotalLocationsCount, slot.EngineYamlInfo?["source"]?.ToString(), slot.EngineProblem?.Message);
                    parts.Add(player?.Exact == true ? "logic exact" : "logic differs");
                    if (slot.Pack != null)
                    {
                        progress?.Report(new SoloTestProgress(SoloTestStep.Score, SoloStepOutcome.Running, "checking the pack", null));
                        var original = PopTrackerPackLoader.InspectZipPack(slot.Pack.SourcePath);
                        var doctor = original != null ? await PackDoctorService.CheckAsync(original, prompt: false) : null;
                        if (doctor != null)
                        {
                            pins = new SoloPins(doctor.ApLocationsTotal, doctor.ApLocationsPlaced, doctor.SectionsTotal, doctor.SectionsLinked, Math.Max(0, doctor.SectionsTotal - doctor.SectionsLinked),
                                doctor.Index?.DanglingPaths.Count ?? 0, doctor.NeedsReview.Count());
                            keyItems = new SoloKeyItems(doctor.TilesTotal, doctor.TilesLinked, doctor.TilesByScript);
                            parts.Add("pins " + SoloTestReport.Percent(pins.LocationsPlaced, pins.LocationsTotal));
                        }
                        var host = slot.PackScripts;
                        scripts = new SoloScripts(host != null && doctor?.ScriptsRan == true, host?.UnsupportedApis.OrderBy(a => a).ToList() ?? new List<string>(), host?.IgnoredWrites.OrderBy(a => a).ToList() ?? new List<string>(),
                            host?.Errors.ToList() ?? new List<string>(), host?.FollowsMaps ?? false, host?.ReadsGameMemory ?? false, host?.Stopped ?? false, host?.StopReason,
                            doctor?.Findings.Count(f => f.Category == "Scripts") ?? 0);
                    }
                    return Done(string.Join(", ", parts));
                });
            }
            catch (OperationCanceledException)
            {
                // Recorded by the step that was running; the report still says what happened.
            }
            finally
            {
                await StepAsync(SoloTestStep.Report, progress, CancellationToken.None, () =>
                {
                    var engine = install == null ? "none" : install.Describe();
                    var versions = new SoloVersions(Game, apworldVersion, apworldSource, apworldChecksum, engine, pack?.Manifest.Name, pack?.Manifest.GetActualVersion(), string.IsNullOrWhiteSpace(pack?.Manifest.VersionsUrl) ? null : pack!.Manifest.VersionsUrl, hooks.AtlasVersionLine());
                    _result = new SoloTestResult(Game, started, versions, _steps.ToList(), generation, logic, pins, keyItems, scripts, ServerAddress, _notes);
                    (_reportMd, _reportJson) = SoloTestReport.Write(ReportsFolder, _result, _scrubber!, DateTime.Now);
                    return Task.FromResult(Done("written to " + Path.GetFileName(_reportMd)));
                });
                _result = _result! with { Steps = _steps.ToList() };
                Busy = false;
                StateChanged?.Invoke();
            }
            return _result!;
        }

        /// <summary>Writes the owner's answers into both files (an earlier set replaced).</summary>
        public (string Md, string Json)? SaveNotes(SoloOwnerNotes notes)
        {
            if (_result == null || _reportMd == null || _reportJson == null || _scrubber == null) return null;
            _notes = notes;
            _result = _result with { Notes = notes };
            SafeFile.WriteAllText(_reportMd, SoloTestReport.WithNotes(SoloTestReport.Markdown(_result with { Notes = null }, _scrubber), notes));
            SafeFile.WriteAllText(_reportJson, SoloTestReport.Json(_result, _scrubber));
            return (_reportMd, _reportJson);
        }

        /// <summary>Ends the test server, disconnects the test slot and removes its multiworld.</summary>
        public void StopServer(SoloTestHooks? hooks)
        {
            _host?.Stop();
            _host = null;
            ServerAddress = null;
            if (Profile != null && hooks != null)
            {
                hooks.Settings.SlotYamlPaths.Remove(Annotations.SlotKey(Profile.Id, SlotName));
                hooks.RemoveProfile(Profile);
            }
            Profile = null;
            StateChanged?.Invoke();
        }

        /// <summary>Every test server ended (Atlas is closing; the job object is the backstop).</summary>
        public static void StopAll()
        {
            foreach (var runner in _all.ToList()) runner.StopServer(null);
            _all.Clear();
        }

        // ---- The pieces ----

        private static bool NeedsPatchSkip(JObject answer)
        {
            string error = (answer["error"]?.ToString() ?? "") + " " + (answer["error_type"]?.ToString() ?? "");
            return error.Contains("FileNotFoundError", StringComparison.Ordinal) || error.Contains(".sfc", StringComparison.OrdinalIgnoreCase) || error.Contains(".z64", StringComparison.OrdinalIgnoreCase)
                || error.Contains(".gba", StringComparison.OrdinalIgnoreCase) || error.Contains(".nes", StringComparison.OrdinalIgnoreCase) || error.Contains(".smc", StringComparison.OrdinalIgnoreCase)
                || error.Contains("rom", StringComparison.OrdinalIgnoreCase);
        }

        private static LoadedPack? InstalledPackFor(string game)
        {
            string packs = PopTrackerPackLoader.GetPacksDirectory();
            if (!Directory.Exists(packs)) return null;
            foreach (string file in Directory.GetFiles(packs, "*.zip").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                LoadedPack? pack;
                try { pack = PopTrackerPackLoader.InspectZipPack(file); }
                catch (Exception ex)
                {
                    Logger.LogDebug("Solo test: couldn't read " + Path.GetFileName(file) + ": " + ex.Message);
                    continue;
                }
                if (pack?.Manifest != null && (PopTrackerPackLoader.IsGameNameMatch(pack.Manifest.GameName, game) || PopTrackerPackLoader.IsGameNameMatch(pack.Manifest.Name, game))) return pack;
            }
            return null;
        }

        /// <summary>Runs a one-shot component and returns its answer line (the first JSON object with "ok"), the error lines going to the progress log.</summary>
        private static async Task<JObject?> ComponentAsync(EngineInstall install, string component, JObject request, TimeSpan limit, SoloTestStep step, IProgress<SoloTestProgress>? progress, CancellationToken ct)
        {
            JObject? answer = null;
            await AtlasEngine.RunComponentAsync(install, component, request.ToString(Newtonsoft.Json.Formatting.None), line =>
            {
                string t = line.Trim();
                if (!t.StartsWith("{", StringComparison.Ordinal)) return;
                try
                {
                    var parsed = JObject.Parse(t);
                    if (parsed["ok"] != null) answer = parsed;
                }
                catch (Newtonsoft.Json.JsonException) { } // a line printed on the channel by mistake isn't an answer
            }, err =>
            {
                string t = err.Trim();
                if (t.Length > 0) progress?.Report(new SoloTestProgress(step, SoloStepOutcome.Running, "", t.Length > 200 ? t.Substring(0, 200) : t));
            }, ct, limit);
            return answer;
        }

        private readonly record struct StepResult(SoloStepOutcome Outcome, string Detail);

        private static StepResult Done(string detail) => new(SoloStepOutcome.Done, detail);
        private static StepResult Skipped(string detail) => new(SoloStepOutcome.Skipped, detail);
        private static StepResult Failed(string detail) => new(SoloStepOutcome.Failed, detail);

        /// <summary>Runs one step, records it and reports it; true unless it failed or was cancelled.</summary>
        private async Task<bool> StepAsync(SoloTestStep step, IProgress<SoloTestProgress>? progress, CancellationToken ct, Func<Task<StepResult>> work)
        {
            var watch = Stopwatch.StartNew();
            progress?.Report(new SoloTestProgress(step, SoloStepOutcome.Running, "", null));
            StepResult result;
            try
            {
                ct.ThrowIfCancellationRequested();
                result = await work();
            }
            catch (OperationCanceledException)
            {
                result = new StepResult(SoloStepOutcome.Cancelled, "cancelled");
                Record(step, result, watch.Elapsed.TotalSeconds, progress);
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Solo test, " + step + ": " + ex);
                result = Failed(ex.Message);
            }
            Record(step, result, watch.Elapsed.TotalSeconds, progress);
            return result.Outcome is SoloStepOutcome.Done or SoloStepOutcome.Skipped;
        }

        private void Record(SoloTestStep step, StepResult result, double seconds, IProgress<SoloTestProgress>? progress)
        {
            _steps.Add(new SoloStepRecord(step, result.Outcome, result.Detail, seconds));
            progress?.Report(new SoloTestProgress(step, result.Outcome, result.Detail, null));
        }
    }
}
