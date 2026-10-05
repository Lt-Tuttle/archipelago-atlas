using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Lightweight main-thread profiler for explaining UI hitches.
    /// Wrap potentially slow UI work in <c>using (PerfMonitor.Measure("label")) { ... }</c>; a
    /// <see cref="HitchMonitor"/> node then reports long frames together with the scopes that ran in them.
    /// A scope's time leaves out .NET garbage collection pauses that happened during it (whichever thread's allocation
    /// set them off): the report lists those on their own line, so they're neither blamed on the step that happened to
    /// be running nor counted twice.
    /// </summary>
    public static class PerfMonitor
    {
        public readonly struct Scope : IDisposable
        {
            private readonly Total _total;
            private readonly long _start, _allocated;
            private readonly TimeSpan _paused;

            internal Scope(Total total)
            {
                _total = total;
                _allocated = GC.GetAllocatedBytesForCurrentThread();
                _paused = GC.GetTotalPauseDuration();
                _start = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
                ms -= (GC.GetTotalPauseDuration() - _paused).TotalMilliseconds;
                Record(_total, Math.Max(0, ms), GC.GetAllocatedBytesForCurrentThread() - _allocated);
            }
        }

        /// <summary>One measured step in a frame: how long its runs took together, how many there were, and what they allocated.</summary>
        internal readonly record struct FrameScope(string Label, int Depth, double Ms, int Count, long Bytes);

        /// <summary>A step's running total for the current frame (main thread only).</summary>
        internal sealed class Total
        {
            public string Label = "";
            public int Depth;
            public double Ms;
            public int Count;
            public long Bytes;
        }

        // This frame's steps, in the order they first started (so a step is listed before the ones inside it).
        private static readonly Dictionary<string, Total> _totals = new();
        private static readonly List<Total> _order = new();
        private static readonly Total Ignored = new();
        [ThreadStatic] private static int _depth;

        /// <summary>What the user just did (e.g. "Switch to Map Tracker"), used to give hitch reports context.</summary>
        public static string CurrentAction { get; private set; } = "";
        private static ulong _actionFrame;

        public static Scope Measure(string label)
        {
            // Only main-thread work can stall a frame; anything measured on worker threads is ignored.
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != MainThreadId) return new Scope(Ignored);
            if (!_totals.TryGetValue(label, out var total))
            {
                total = new Total { Label = label, Depth = _depth };
                _totals[label] = total;
                _order.Add(total);
            }
            _depth++;
            return new Scope(total);
        }

        public static void SetAction(string action)
        {
            CurrentAction = action;
            _actionFrame = Engine.GetProcessFrames();
        }

        private static void Record(Total total, double ms, long bytes)
        {
            if (ReferenceEquals(total, Ignored)) return;
            _depth = Math.Max(0, _depth - 1);
            total.Ms += ms;
            total.Count++;
            total.Bytes += bytes;
        }

        internal static int MainThreadId = -1;

        /// <summary>Returns and clears the steps measured since the last call.</summary>
        internal static List<FrameScope> DrainFrameScopes()
        {
            var copy = _order.Select(t => new FrameScope(t.Label, t.Depth, t.Ms, t.Count, t.Bytes)).ToList();
            _order.Clear();
            _totals.Clear();
            return copy;
        }

        /// <summary>A step's kind: its label without the slot it was for ("[Player 1] Hints refresh" is "Hints refresh").</summary>
        internal static string KindOf(string label) => Regex.Replace(label, @"^\[[^\]]*\] ", "");

        /// <summary>The action is only relevant to the frames right after it happened.</summary>
        internal static string RecentAction(ulong currentFrame) =>
            currentFrame - _actionFrame <= 3 ? CurrentAction : "";
    }

    /// <summary>
    /// Watches frame times. When a frame takes longer than the threshold, logs what ran during it
    /// (instrumented scopes, garbage collections) to the System Log and reports a summary to the status bar.
    /// </summary>
    public partial class HitchMonitor : Node
    {
        private const double HitchThresholdMs = 100.0;
        private const double StatusCooldownSec = 2.0;
        /// <summary>Steps that took less than this in all aren't worth listing in a hitch report.</summary>
        private const double MinReportedScopeMs = 5.0;

        private readonly Action<string> _setStatus;
        private readonly int[] _gcCounts = new int[3];
        private TimeSpan _gcPaused;
        private long _allocatedEverywhere;
        private double _lastStatusTime = -999;
        // Work that ran in the previous frame: Godot's delta measures the frame that just finished, and deferred
        // calls run after _Process, so a slow frame's scopes can land in either of the last two drains.
        private List<PerfMonitor.FrameScope> _previousScopes = new();

        public HitchMonitor(Action<string> setStatus)
        {
            _setStatus = setStatus;
            Name = "HitchMonitor";
            ProcessMode = ProcessModeEnum.Always;
        }

        public override void _Ready()
        {
            PerfMonitor.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            for (int g = 0; g < 3; g++) _gcCounts[g] = GC.CollectionCount(g);
            _gcPaused = GC.GetTotalPauseDuration();
            _allocatedEverywhere = GC.GetTotalAllocatedBytes();
        }

        private ulong _lastProcessUsec;

        /// <summary>The longest frame since <see cref="ResetWorst"/> (or since Atlas started), and what ran in it.</summary>
        public static double WorstFrameMs { get; private set; }
        public static string WorstFrameReport { get; private set; } = "";

        /// <summary>Starts measuring the worst frame and the steps again (the UI test's scale scenario measures a burst this way).</summary>
        public static void ResetWorst()
        {
            WorstFrameMs = 0;
            WorstFrameReport = "";
            _steps.Clear();
        }

        // Since ResetWorst: each kind of step (a label without its slot), how many times it ran, the most of one frame it
        // took, and the most times it ran in one frame.
        private static readonly Dictionary<string, (int Runs, double WorstFrameMs, int MostRunsInFrame)> _steps = new();

        /// <summary>
        /// How many times a kind of step ran since <see cref="ResetWorst"/>, the most of one frame it took (garbage collection
        /// pauses left out), and the most times it ran in one frame.
        /// </summary>
        public static (int Runs, double WorstFrameMs, int MostRunsInFrame) Step(string kind) => _steps.TryGetValue(kind, out var step) ? step : (0, 0, 0);

        public override void _Process(double delta)
        {
            // Wall-clock frame time: Godot's delta is smoothed and would under-report a single long frame.
            ulong nowUsec = Time.GetTicksUsec();
            double frameMs = _lastProcessUsec == 0 ? 0 : (nowUsec - _lastProcessUsec) / 1000.0;
            _lastProcessUsec = nowUsec;
            var drained = PerfMonitor.DrainFrameScopes();
            foreach (var kind in drained.GroupBy(s => PerfMonitor.KindOf(s.Label)))
            {
                var (runs, worstMs, mostRuns) = Step(kind.Key);
                _steps[kind.Key] = (runs + kind.Sum(s => s.Count), Math.Max(worstMs, kind.Sum(s => s.Ms)), Math.Max(mostRuns, kind.Sum(s => s.Count)));
            }
            // The same step in both frames counts as one, with its times and runs added.
            var scopes = _previousScopes.Concat(drained).GroupBy(s => s.Label)
                .Select(g => new PerfMonitor.FrameScope(g.Key, g.First().Depth, g.Sum(s => s.Ms), g.Sum(s => s.Count), g.Sum(s => s.Bytes))).ToList();
            _previousScopes = drained;
            var gcs = new List<string>();
            for (int g = 0; g < 3; g++)
            {
                int count = GC.CollectionCount(g);
                if (count != _gcCounts[g]) gcs.Add($"gen{g} x{count - _gcCounts[g]}");
                _gcCounts[g] = count;
            }
            // How long collections stopped the program (every thread, this one included) since the last frame.
            var paused = GC.GetTotalPauseDuration();
            double gcPausedMs = (paused - _gcPaused).TotalMilliseconds;
            _gcPaused = paused;
            long allocatedEverywhere = GC.GetTotalAllocatedBytes();
            double allocatedMb = (allocatedEverywhere - _allocatedEverywhere) / 1048576.0;
            _allocatedEverywhere = allocatedEverywhere;

            // The first frames after startup are always long; nothing useful to report.
            if (Engine.GetProcessFrames() < 10) return;
            bool worst = frameMs > WorstFrameMs;
            if (worst) WorstFrameMs = frameMs;
            if (frameMs < HitchThresholdMs) return;

            string action = PerfMonitor.RecentAction(Engine.GetProcessFrames());
            double measured = scopes.Where(s => s.Depth == 0).Sum(s => s.Ms);

            var report = new System.Text.StringBuilder();
            report.Append($"[color=orange]UI hitch: frame took {frameMs:0} ms");
            if (!string.IsNullOrEmpty(action)) report.Append($" after \"{action}\"");
            report.Append("[/color]");
            foreach (var s in scopes.Where(s => s.Ms >= MinReportedScopeMs || s.Bytes >= 1048576))
                report.Append($"\n    {new string(' ', s.Depth * 2)}{s.Label}{(s.Count > 1 ? $" (x{s.Count})" : "")}: {s.Ms:0} ms" +
                              (s.Bytes >= 1048576 ? $", {s.Bytes / 1048576.0:0.0} MB allocated" : ""));
            // The same step for many slots: each run may be quick, but together they can fill a frame.
            foreach (var kind in scopes.Where(s => s.Depth == 0).GroupBy(s => PerfMonitor.KindOf(s.Label))
                         .Where(g => g.Count() > 1 && g.Sum(s => s.Ms) >= MinReportedScopeMs).OrderByDescending(g => g.Sum(s => s.Ms)))
                report.Append($"\n    All slots: {kind.Key} (x{kind.Sum(s => s.Count)}): {kind.Sum(s => s.Ms):0} ms");
            if (gcs.Count > 0) report.Append($"\n    .NET garbage collection ran ({string.Join(", ", gcs)}), pausing everything for {gcPausedMs:0} ms; {allocatedMb:0.0} MB was allocated since the last frame, on all threads");
            double unexplained = frameMs - measured - gcPausedMs;
            if (unexplained > HitchThresholdMs / 2)
            {
                report.Append($"\n    ~{unexplained:0} ms in Godot layout/rendering or other uninstrumented work (e.g. first-time texture uploads, text re-layout)");
            }
            _previousScopes = new List<PerfMonitor.FrameScope>(); // already reported
            if (worst) WorstFrameReport = report.ToString();
            Logger.LogWarning(report.ToString());

            double now = Time.GetTicksMsec() / 1000.0;
            if (now - _lastStatusTime < StatusCooldownSec) return;
            _lastStatusTime = now;
            var top = scopes.Where(s => s.Depth == 0).OrderByDescending(s => s.Ms).FirstOrDefault();
            string cause = top.Label != null ? $"{top.Label} ({top.Ms:0} ms)" : (gcs.Count > 0 ? "garbage collection / layout" : "layout/rendering");
            _setStatus?.Invoke($"Hitch {frameMs:0} ms: {cause}. Details in System Log.");
        }
    }
}
