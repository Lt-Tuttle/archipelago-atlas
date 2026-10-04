using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Lightweight main-thread profiler for explaining UI hitches.
    /// Wrap potentially slow UI work in <c>using (PerfMonitor.Measure("label")) { ... }</c>; a
    /// <see cref="HitchMonitor"/> node then reports long frames together with the scopes that ran in them.
    /// </summary>
    public static class PerfMonitor
    {
        public readonly struct Scope : IDisposable
        {
            private readonly string _label;
            private readonly long _start;
            private readonly int _slot; // position in the frame list at entry, so parents are listed before children

            internal Scope(string label, int slot)
            {
                _label = label;
                _slot = slot;
                _start = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
                Record(_label, ms, _slot);
            }
        }

        /// <summary>Scopes shorter than this aren't worth mentioning in a hitch report.</summary>
        private const double MinReportedScopeMs = 5.0;

        private static readonly List<(string Label, double Ms)> _frameScopes = new List<(string, double)>();
        [ThreadStatic] private static int _depth;

        /// <summary>What the user just did (e.g. "Switch to Map Tracker"), used to give hitch reports context.</summary>
        public static string CurrentAction { get; private set; } = "";
        private static ulong _actionFrame;

        public static Scope Measure(string label)
        {
            _depth++;
            return new Scope(label, _frameScopes.Count);
        }

        public static void SetAction(string action)
        {
            CurrentAction = action;
            _actionFrame = Engine.GetProcessFrames();
        }

        private static void Record(string label, double ms, int slot)
        {
            _depth = Math.Max(0, _depth - 1);
            // Only main-thread work can stall a frame; ignore anything measured on worker threads.
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != MainThreadId) return;
            if (ms < MinReportedScopeMs) return;
            // Indent nested scopes so the report reads as a breakdown.
            var entry = (new string(' ', _depth * 2) + label, ms);
            if (slot <= _frameScopes.Count) _frameScopes.Insert(slot, entry);
            else _frameScopes.Add(entry);
        }

        internal static int MainThreadId = -1;

        /// <summary>Returns and clears the scopes recorded since the last call.</summary>
        internal static List<(string Label, double Ms)> DrainFrameScopes()
        {
            var copy = _frameScopes.ToList();
            _frameScopes.Clear();
            return copy;
        }

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

        private readonly Action<string> _setStatus;
        private readonly int[] _gcCounts = new int[3];
        private double _lastStatusTime = -999;
        // Work that ran in the previous frame: Godot's delta measures the frame that just finished, and deferred
        // calls run after _Process, so a slow frame's scopes can land in either of the last two drains.
        private List<(string Label, double Ms)> _previousScopes = new List<(string, double)>();

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
        }

        private ulong _lastProcessUsec;

        public override void _Process(double delta)
        {
            // Wall-clock frame time: Godot's delta is smoothed and would under-report a single long frame.
            ulong nowUsec = Time.GetTicksUsec();
            double frameMs = _lastProcessUsec == 0 ? 0 : (nowUsec - _lastProcessUsec) / 1000.0;
            _lastProcessUsec = nowUsec;
            var drained = PerfMonitor.DrainFrameScopes();
            var scopes = _previousScopes.Concat(drained).ToList();
            _previousScopes = drained;
            var gcs = new List<string>();
            for (int g = 0; g < 3; g++)
            {
                int count = GC.CollectionCount(g);
                if (count != _gcCounts[g]) gcs.Add($"gen{g} x{count - _gcCounts[g]}");
                _gcCounts[g] = count;
            }

            if (frameMs < HitchThresholdMs) return;
            // The first frames after startup are always long; nothing useful to report.
            if (Engine.GetProcessFrames() < 10) return;

            string action = PerfMonitor.RecentAction(Engine.GetProcessFrames());
            double measured = scopes.Where(s => !s.Label.StartsWith(" ")).Sum(s => s.Ms);

            var report = new System.Text.StringBuilder();
            report.Append($"[color=orange]UI hitch: frame took {frameMs:0} ms");
            if (!string.IsNullOrEmpty(action)) report.Append($" after \"{action}\"");
            report.Append("[/color]");
            foreach (var s in scopes) report.Append($"\n    {s.Label}: {s.Ms:0} ms");
            if (gcs.Count > 0) report.Append($"\n    .NET garbage collection ran ({string.Join(", ", gcs)})");
            double unexplained = frameMs - measured;
            if (unexplained > HitchThresholdMs / 2)
            {
                report.Append($"\n    ~{unexplained:0} ms in Godot layout/rendering or other uninstrumented work (e.g. first-time texture uploads, text re-layout)");
            }
            _previousScopes = new List<(string, double)>(); // already reported
            Logger.LogWarning(report.ToString());

            double now = Time.GetTicksMsec() / 1000.0;
            if (now - _lastStatusTime < StatusCooldownSec) return;
            _lastStatusTime = now;
            var top = scopes.Where(s => !s.Label.StartsWith(" ")).OrderByDescending(s => s.Ms).FirstOrDefault();
            string cause = top.Label != null ? $"{top.Label} ({top.Ms:0} ms)" : (gcs.Count > 0 ? "garbage collection / layout" : "layout/rendering");
            _setStatus?.Invoke($"Hitch {frameMs:0} ms: {cause}. Details in System Log.");
        }
    }
}
