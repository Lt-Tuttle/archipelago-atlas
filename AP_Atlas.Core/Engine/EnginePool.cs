using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>How a slot lost its engine.</summary>
    /// <param name="Reason">Why, in a few words, for the log.</param>
    /// <param name="Mine">
    /// The slot's own request was the one being answered when the engine crashed or stopped answering (or the engine
    /// ended answering no one's): the slot counts it as a failure of its own. Otherwise the engine was lost answering
    /// another slot that shares it, and the slot just starts again.
    /// </param>
    /// <param name="Culprit">The key of the slot whose request was being answered, if any.</param>
    public sealed record EngineLoss(string Reason, bool Mine, string? Culprit);

    /// <summary>
    /// One multiworld's logic engines. The user chose (2026-10-05) a small pool per multiworld over one process per slot
    /// (about 180 MB each) or one for the whole multiworld:
    /// <list type="bullet">
    /// <item>Up to <see cref="MaxEngines"/> processes. While there are no more slots than that, each slot gets its own;
    /// then a new slot joins the engine with the fewest. Slots of one multiworld can share an engine because they share
    /// the seed's apworld versions.</item>
    /// <item>An engine that crashes, or doesn't answer in time, is stopped, and only its slots lose it. Each is told
    /// whether its own request was the one being answered (<see cref="EngineLoss"/>).</item>
    /// <item>A slot that leaves has its world dropped from its engine; an engine whose last slot leaves is stopped.</item>
    /// </list>
    /// Thread-safe.
    /// </summary>
    public sealed class EnginePool
    {
        private readonly Func<EngineProcess> _start;
        private readonly Action<string> _log;
        private readonly object _lock = new();
        private readonly List<PooledEngine> _engines = new();
        private int _started;

        /// <param name="start">Starts one engine process.</param>
        /// <param name="maxEngines">The most processes at once (see <see cref="EnginesFor"/>).</param>
        /// <param name="log">The pool's own messages: engines starting, stopping and lost.</param>
        public EnginePool(Func<EngineProcess> start, int maxEngines, Action<string> log)
        {
            _start = start;
            MaxEngines = Math.Max(1, maxEngines);
            _log = log;
        }

        /// <summary>The most engine processes the pool runs at once.</summary>
        public int MaxEngines { get; }

        /// <summary>How many engines a multiworld may run on a PC with this many processors: half of them, from 1 to 4.</summary>
        public static int EnginesFor(int processors) => Math.Clamp(processors / 2, 1, 4);

        /// <summary>How many slots each running engine has, oldest engine first.</summary>
        public IReadOnlyList<int> SlotsPerEngine
        {
            get
            {
                lock (_lock) return _engines.Select(engine => engine.Seats.Count).ToList();
            }
        }

        /// <summary>
        /// A seat for a slot on one of the pool's engines, starting an engine if the pool may. The slot then starts its
        /// world there with an "init" request.
        /// </summary>
        /// <exception cref="System.ComponentModel.Win32Exception">An engine had to start and couldn't.</exception>
        public EngineSeat Join(string key)
        {
            lock (_lock)
            {
                PooledEngine engine;
                if (_engines.Count < MaxEngines)
                {
                    engine = new PooledEngine(_start(), ++_started);
                    _engines.Add(engine);
                    var started = engine;
                    engine.Process.Exited += code => Lose(started, $"its process exited (code {code})");
                    _log($"Logic engine {engine.Number} started (for {key}).");
                }
                else engine = _engines.OrderBy(e => e.Seats.Count).First(); // the oldest of those with the fewest slots
                var seat = new EngineSeat(this, engine, key);
                engine.Seats.Add(seat);
                return seat;
            }
        }

        internal async Task<EngineAnswer> AskAsync(EngineSeat seat, JObject request, TimeSpan timeout, bool stuckIfLate, CancellationToken ct)
        {
            var engine = seat.Engine;
            request["key"] = seat.Key;
            var answer = await engine.Process.AskAsync(request, timeout, ct, seat).ConfigureAwait(false);
            switch (answer.Failure)
            {
                case EngineFailure.TimedOut when stuckIfLate:
                    Lose(engine, "it stopped answering: " + answer.Why);
                    break;
                case EngineFailure.Ended or EngineFailure.CouldNotLoad:
                    Lose(engine, answer.Why ?? "it stopped");
                    break;
            }
            return answer;
        }

        /// <summary>An engine crashed or got stuck: it's stopped, and its slots are told (once).</summary>
        private void Lose(PooledEngine engine, string reason)
        {
            List<EngineSeat> seats;
            lock (_lock)
            {
                if (engine.Lost) return;
                engine.Lost = true;
                _engines.Remove(engine);
                seats = engine.Seats.ToList();
                engine.Seats.Clear();
            }
            // Whose request it was working on: the slot that brought it down (or none, if it was idle).
            var culprit = engine.Process.Answering as EngineSeat;
            _log($"Logic engine {engine.Number} was lost: {reason}" + (culprit != null ? $", answering {culprit.Key}" : "") +
                 (seats.Count > 0 ? $". Its slots ({string.Join(", ", seats.Select(s => s.Key))}) start again." : "."));
            foreach (var seat in seats) seat.SetLoss(new EngineLoss(reason, culprit == null || culprit == seat, culprit?.Key));
            engine.Process.Stop();
            foreach (var seat in seats) seat.RaiseLost();
        }

        internal void Leave(EngineSeat seat)
        {
            var engine = seat.Engine;
            bool last;
            lock (_lock)
            {
                if (!engine.Seats.Remove(seat)) return; // its engine was lost already
                last = engine.Seats.Count == 0;
                if (last)
                {
                    engine.Lost = true; // stopped on purpose: its ending isn't a loss
                    _engines.Remove(engine);
                }
            }
            if (last)
            {
                _log($"Logic engine {engine.Number} stopped: its last slot ({seat.Key}) left.");
                engine.Process.Stop();
            }
            else Async.Fire(DropAsync(engine, seat.Key), "freeing a slot's world in its logic engine", tellUser: false);
        }

        // The slot's world goes; a drop that's slow is no reason to stop the engine (its other slots ask next).
        private static Task DropAsync(PooledEngine engine, string key) =>
            engine.Process.AskAsync(new JObject { ["action"] = "drop", ["key"] = key }, TimeSpan.FromMinutes(1));
    }

    /// <summary>A slot's place in an engine pool: its requests go to its engine, tagged with its key.</summary>
    public sealed class EngineSeat
    {
        private readonly EnginePool _pool;
        private EngineLoss? _loss;
        private volatile bool _left;

        internal EngineSeat(EnginePool pool, PooledEngine engine, string key)
        {
            _pool = pool;
            Engine = engine;
            Key = key;
        }

        internal PooledEngine Engine { get; }

        /// <summary>The slot's key in its engine (requests name their slot with it).</summary>
        public string Key { get; }

        /// <summary>The engine's number in its pool (1, 2…), for the log.</summary>
        public int EngineNumber => Engine.Number;

        /// <summary>Raised (on any thread) when the slot's engine is lost: it crashed, or stopped answering in time. Not after <see cref="Leave"/>.</summary>
        public event Action<EngineLoss>? Lost;

        /// <summary>How the slot's engine was lost, once it was (null while it runs).</summary>
        public EngineLoss? Loss => Volatile.Read(ref _loss);

        /// <summary>The slot can ask: it hasn't left, and its engine runs.</summary>
        public bool Running => !_left && Loss == null && Engine.Process.Running;

        /// <summary>
        /// Asks the slot's world in its engine. When <paramref name="stuckIfLate"/>, no answer in time means the engine is
        /// stuck: it's stopped, and its slots lose it (a "why" that takes long is no reason: it's answered late or not).
        /// </summary>
        public Task<EngineAnswer> AskAsync(JObject request, TimeSpan timeout, bool stuckIfLate = true, CancellationToken ct = default) =>
            _left ? Task.FromResult(EngineAnswer.Failed(EngineFailure.Stopped, "the slot left its engine")) : _pool.AskAsync(this, request, timeout, stuckIfLate, ct);

        /// <summary>The slot is done with its engine: its world is dropped, and an engine left with no slots stops. Safe to call more than once.</summary>
        public void Leave()
        {
            if (_left) return;
            _left = true;
            _pool.Leave(this);
        }

        internal void SetLoss(EngineLoss loss) => Volatile.Write(ref _loss, loss);

        internal void RaiseLost()
        {
            var loss = Loss;
            if (loss == null || _left) return;
            try { Lost?.Invoke(loss); }
            catch (Exception ex) { Async.Report(ex, "handling a lost logic engine", tellUser: false); }
        }
    }

    /// <summary>One engine process of a pool, and the slots it hosts.</summary>
    internal sealed class PooledEngine
    {
        public PooledEngine(EngineProcess process, int number)
        {
            Process = process;
            Number = number;
        }

        public EngineProcess Process { get; }

        public int Number { get; }

        // Under the pool's lock.
        public List<EngineSeat> Seats { get; } = new();

        public bool Lost;
    }
}
