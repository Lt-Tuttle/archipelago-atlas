#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The steps to a working Atlas, as Home's setup line and the setup window count them: each read from Atlas's own
    /// state where it can be (the engine, a multiworld with a server, a slot the user named, a pack, a tracker link), else
    /// from the setup window's pages the user passed with Next. A skipped step counts as done.
    /// </summary>
    public sealed class SetupSteps
    {
        /// <summary>One step: its id (kept in the settings) and the setup window's page title.</summary>
        public sealed record Step(string Id, string Title);

        public static readonly Step Look = new("look", "Look");
        public static readonly Step Engine = new("engine", "The Atlas Engine");
        public static readonly Step Multiworld = new("multiworld", "Your multiworld");
        public static readonly Step Slots = new("slots", "Your slots");
        public static readonly Step Games = new("games", "Games and map packs");
        public static readonly Step Trackers = new("trackers", "Trackers");
        public static readonly Step Reports = new("reports", "Reports and updates");

        /// <summary>The steps in the setup window's order.</summary>
        public static readonly IReadOnlyList<Step> All = new[] { Look, Engine, Multiworld, Slots, Games, Trackers, Reports };

        /// <summary>What the steps read from the window, and where their skips and passed pages are kept.</summary>
        public sealed class Hooks
        {
            public Func<bool> EngineReady { get; init; } = () => false;
            public Func<IReadOnlyList<MultiworldProfile>> Profiles { get; init; } = () => Array.Empty<MultiworldProfile>();
            public Func<bool> AnyPackInstalled { get; init; } = () => false;
            /// <summary>The setup window's pages passed with Next (AppSettings.SetupPagesDone).</summary>
            public Func<IReadOnlyList<string>> PagesDone { get; init; } = () => Array.Empty<string>();
            public Action<string> MarkPageDone { get; init; } = _ => { };
            /// <summary>The steps the user skipped (AppSettings.SkippedHomeSteps).</summary>
            public Func<string, bool> IsSkipped { get; init; } = _ => false;
            public Action<string, bool> SetSkipped { get; init; } = (_, _) => { };
        }

        private readonly Hooks _hooks;

        public SetupSteps(Hooks hooks) => _hooks = hooks;

        /// <summary>Atlas's own placeholder slot names: a multiworld with only these has no slot the user named.</summary>
        public static bool IsPlaceholderSlot(string? name) => name is "Player1" or "New Slot";

        /// <summary>Whether a step is done, from Atlas's state (or, for the look and the reports page, from the page passed).</summary>
        public bool Done(Step step)
        {
            var profiles = _hooks.Profiles();
            var pages = _hooks.PagesDone();
            if (step == Look) return pages.Contains(Look.Id);
            if (step == Engine) return _hooks.EngineReady();
            if (step == Multiworld) return profiles.Any(p => !string.IsNullOrWhiteSpace(p.ServerUrl));
            if (step == Slots) return profiles.Any(p => p.Slots.Any(s => !IsPlaceholderSlot(s)));
            if (step == Games) return pages.Contains(Games.Id) || _hooks.AnyPackInstalled();
            if (step == Trackers) return pages.Contains(Trackers.Id) || profiles.Any(p => !string.IsNullOrWhiteSpace(p.CheeseTrackerUrl) || !string.IsNullOrWhiteSpace(p.SphereTrackerUrl));
            if (step == Reports) return pages.Contains(Reports.Id);
            return false;
        }

        public bool Skipped(Step step) => _hooks.IsSkipped(step.Id);

        public void SetSkipped(Step step, bool skipped) => _hooks.SetSkipped(step.Id, skipped);

        public void MarkPageDone(Step step) => _hooks.MarkPageDone(step.Id);

        /// <summary>Done or skipped: what counts as settled.</summary>
        public bool Settled(Step step) => Done(step) || Skipped(step);

        public int SettledCount => All.Count(Settled);

        public int Total => All.Count;

        /// <summary>The first step neither done nor skipped (null once every step is settled).</summary>
        public Step? Next => All.FirstOrDefault(step => !Settled(step));

        public bool AllSettled => Next == null;

        /// <summary>Home's one line about the setup: how far it is and what's next, or that it's done.</summary>
        public string Line(Func<string, string> tr)
        {
            var next = Next;
            if (next == null) return tr("You're set up.");
            return tr("Setup {0} of {1} done. Next: {2}.").Replace("{0}", SettledCount.ToString()).Replace("{1}", Total.ToString()).Replace("{2}", tr(next.Title));
        }
    }
}
