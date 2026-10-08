using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>
    /// A slot's pack scripts, run one piece of work at a time on a background queue (scripts aren't thread-safe): started
    /// once with the slot's options and what it has so far, then fed each new item and check as it arrives.
    /// </summary>
    /// <remarks>
    /// The queue keeps the scripts it started, and later work runs on those. If the scripts are stopped (a piece of their
    /// work went over its limits), later work does nothing, and the owner hears why once. Items that arrive while the scripts start are
    /// queued behind the start and reach the scripts once they're running, even before the main thread hears they are
    /// (it used to look them up there, so those items were lost).
    /// </remarks>
    public sealed class PackScriptRunner
    {
        private readonly Action<Action> _toMainThread;
        private readonly Action<string> _warn;
        private readonly Action<string>? _stopped;
        private Task _queue = Task.CompletedTask;
        // The scripts as the queue sees them, and whether their stop was reported: set and read only by queue work.
        private PackScriptHost? _queued;
        private bool _stopReported;

        /// <param name="toMainThread">Hands a callback to the main thread (skipped if the slot ended meanwhile).</param>
        /// <param name="warn">Says that a piece of script work failed.</param>
        /// <param name="stopped">Says why the scripts were stopped, once, on the main thread.</param>
        public PackScriptRunner(Action<Action> toMainThread, Action<string> warn, Action<string>? stopped = null)
        {
            _toMainThread = toMainThread;
            _warn = warn;
            _stopped = stopped;
        }

        /// <summary>The running scripts, once the main thread has heard they started (null before, or for a pack without scripts).</summary>
        public PackScriptHost? Scripts { get; private set; }

        /// <summary>No script work is queued or running, so the scripts' objects can be read (main thread).</summary>
        public bool Idle => _queue.IsCompleted;

        /// <summary>
        /// Starts the pack's scripts: init, the clear handler with the slot's options, then every item and check so far.
        /// <paramref name="started"/> gets the scripts (null: the pack has none) and how long starting took, on the main thread.
        /// </summary>
        public void Start(LoadedPack pack, int player, int team, JToken slotData, IReadOnlyList<(long ItemId, string ItemName, int Player)> items,
            IReadOnlyList<(long Id, string Name)> locations, Action<PackScriptHost?, long> started)
        {
            PackScriptHost? host = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Queue(() =>
            {
                host = PackScriptHost.Load(pack);
                _queued = host;
                _stopReported = false;
                if (host == null) return;
                host.Initialize();
                host.Clear(player, team, slotData);
                // Which options the seed settings reflect is read here, not on the main thread when Key Items first shows
                // them: it scans all the pack's scripts (0.7 s for the biggest pack in the corpus).
                host.OptionPathsByCode();
                for (int i = 0; i < items.Count; i++) host.ApplyItem(i, items[i].ItemId, items[i].ItemName, items[i].Player);
                foreach (var (id, name) in locations) host.ApplyLocation(id, name);
            }, () =>
            {
                Scripts = host;
                started(host, clock.ElapsedMilliseconds);
            });
        }

        /// <summary>Lets go of the scripts (their pack was removed): queued work finishes, then nothing runs them again.</summary>
        public void Stop() => Queue(() => _queued = null, () => Scripts = null);

        /// <summary>Feeds items received since (index: the item's place in everything the slot received).</summary>
        public void FeedItems(IReadOnlyList<(int Index, long ItemId, string ItemName, int Player)> fresh, Action? onMainThread = null) =>
            Queue(() =>
            {
                foreach (var item in fresh) _queued?.ApplyItem(item.Index, item.ItemId, item.ItemName, item.Player);
            }, onMainThread);

        /// <summary>Feeds locations checked since.</summary>
        public void FeedLocations(IReadOnlyList<(long Id, string Name)> fresh) =>
            Queue(() =>
            {
                foreach (var (id, name) in fresh) _queued?.ApplyLocation(id, name);
            });

        private void Queue(Action work, Action? onMainThread = null)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) { _warn("Pack script: " + ex.Message); }
                if (_queued?.StopReason is string reason && !_stopReported)
                {
                    _stopReported = true;
                    _toMainThread(() => _stopped?.Invoke(reason));
                }
                if (onMainThread != null) _toMainThread(onMainThread);
            }, TaskScheduler.Default);
        }
    }
}
