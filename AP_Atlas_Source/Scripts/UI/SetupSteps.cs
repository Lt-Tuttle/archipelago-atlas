#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The steps to a working Atlas, as Home's setup line and the setup window count them, each read from Atlas's own state:
    /// the engine ready, a multiworld with a server and a slot the user named, and every game the user's slots play ready
    /// (its logic in the engine; its map pack installed, or none known). A skipped step counts as done.
    /// </summary>
    public sealed class SetupSteps
    {
        /// <summary>One step: its id (kept in the settings) and the setup window's page title.</summary>
        public sealed record Step(string Id, string Title);

        public static readonly Step Engine = new("engine", "The Atlas Engine");
        /// <summary>The multiworld and the user's slots in it: one page, whose Next connects the slots.</summary>
        public static readonly Step Multiworld = new("multiworld", "Your multiworld and slots");
        public static readonly Step Games = new("games", "Add games");

        /// <summary>The steps in the setup window's order.</summary>
        public static readonly IReadOnlyList<Step> All = new[] { Engine, Multiworld, Games };

        /// <summary>What the steps read from the window, and where their skips are kept.</summary>
        public sealed class Hooks
        {
            public Func<bool> EngineReady { get; init; } = () => false;
            public Func<IReadOnlyList<MultiworldProfile>> Profiles { get; init; } = () => Array.Empty<MultiworldProfile>();
            /// <summary>Whether every game the user's slots play is ready; false while no slot's game is known (Atlas learns it at the slot's first connect).</summary>
            public Func<bool> SlotGamesReady { get; init; } = () => false;
            /// <summary>Pages an earlier setup window marked passed (AppSettings.SetupPagesDone): a setup begun then isn't untouched.</summary>
            public Func<IReadOnlyList<string>> PagesDone { get; init; } = () => Array.Empty<string>();
            /// <summary>The steps the user skipped (AppSettings.SkippedHomeSteps).</summary>
            public Func<string, bool> IsSkipped { get; init; } = _ => false;
            public Action<string, bool> SetSkipped { get; init; } = (_, _) => { };
        }

        private readonly Hooks _hooks;

        public SetupSteps(Hooks hooks) => _hooks = hooks;

        /// <summary>Atlas's own placeholder slot names: a multiworld with only these has no slot the user named.</summary>
        public static bool IsPlaceholderSlot(string? name) => name is "Player1" or "New Slot";

        /// <summary>Whether a step is done, from Atlas's state.</summary>
        public bool Done(Step step)
        {
            if (step == Engine) return _hooks.EngineReady();
            if (step == Multiworld) return _hooks.Profiles().Any(p => !string.IsNullOrWhiteSpace(p.ServerUrl) && p.Slots.Any(s => !IsPlaceholderSlot(s)));
            if (step == Games) return _hooks.SlotGamesReady();
            return false;
        }

        public bool Skipped(Step step) => _hooks.IsSkipped(step.Id);

        public void SetSkipped(Step step, bool skipped) => _hooks.SetSkipped(step.Id, skipped);

        /// <summary>Done or skipped: what counts as settled.</summary>
        public bool Settled(Step step) => Done(step) || Skipped(step);

        public int SettledCount => All.Count(Settled);

        public int Total => All.Count;

        /// <summary>The first step neither done nor skipped (null once every step is settled).</summary>
        public Step? Next => All.FirstOrDefault(step => !Settled(step));

        public bool AllSettled => Next == null;

        /// <summary>
        /// Nothing set up by the user yet (the engine aside, which may ship ready): no step skipped, none after the engine
        /// done, no page an earlier window kept as passed. The setup window then begins on Welcome.
        /// </summary>
        public bool Untouched => _hooks.PagesDone().Count == 0 && All.All(step => !Skipped(step)) && All.Where(step => step != Engine).All(step => !Done(step));

        /// <summary>Home's one line about the setup: how far it is and what's next, or that it's done.</summary>
        public string Line(Func<string, string> tr)
        {
            var next = Next;
            if (next == null) return tr("You're set up.");
            return tr("Setup {0} of {1} done. Next: {2}.").Replace("{0}", SettledCount.ToString()).Replace("{1}", Total.ToString()).Replace("{2}", tr(next.Title));
        }
    }
}
