#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The end of Add games when the Pack Doctor found things in the packs it installed (the owner's decision 9, 2026-10-15):
    /// "Atlas found things to look at in N map packs: fix them now?" with each pack's line of what changes. Fix them now…
    /// previews every change at once (FixPreviewDialog.ShowAll), then links the confident matches and sets the rest aside, one
    /// undoable step per pack, each pack marked reviewed; then per pack what was done (Undo) and what's left (Review N…: the
    /// Doctor's guide). Later changes nothing; GameAdder postponed these packs' own prompts to Atlas's next start. Main thread.
    /// </summary>
    public sealed partial class PackFixHandoff : VBoxContainer
    {
        /// <summary>The tip that ends Add games' summary and shows on the map until Got it (MapTrackerControl.FixTip).</summary>
        public const string FixTip = "If a pin or tile is wrong while you play, right-click it and choose a fix; Undo is one click. The Map Tracker's Notices tab lists what Atlas noticed.";

        private static readonly TimeSpan RecheckWait = TimeSpan.FromSeconds(30);

        public enum Stage { Asking, Fixing, Done, Later }

        private readonly IReadOnlyList<PackFixOffer> _offers;
        private readonly Action<string, string?> _openDoctor;
        private readonly VBoxContainer _body = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        private readonly Dictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);
        private List<HandoffResult> _results = new();

        /// <summary>Where the question is (for tests).</summary>
        public Stage State
        {
            get => _state;
            private set
            {
                _state = value;
                StateChanged?.Invoke();
            }
        }

        private Stage _state = Stage.Asking;
        private HBoxContainer? _buttons;
        private bool _hostButtons;

        /// <summary>The question moved on (asked, fixing, done, later).</summary>
        public event Action? StateChanged;

        /// <summary>Whether it asks and has something to fix: what its Fix them now… and Later answer.</summary>
        public bool CanFix => State == Stage.Asking && _offers.Any(o => !o.Plan.Nothing);

        /// <summary>Its Fix them now… and Later are its host's (Add games' dialog buttons, always in sight).</summary>
        public void HideOwnButtons()
        {
            _hostButtons = true;
            if (_buttons != null) _buttons.Visible = false;
        }

        /// <param name="offers">The batch's packs to look at (GameAdderRun.Fixes), at least one.</param>
        /// <param name="openDoctor">Opens the Pack Doctor on a pack's zip at a tab (MainTrackerWindow.OpenPackDoctor).</param>
        public PackFixHandoff(IReadOnlyList<PackFixOffer> offers, Action<string, string?> openDoctor)
        {
            _offers = offers;
            _openDoctor = openDoctor;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SetMeta("pack_fix_handoff", true);
            _body.AddThemeConstantOverride("separation", 6);
            AddChild(_body);
            ShowQuestion();
        }

        private static string T(string text) => Kit.Translate(text);
        private static string N(int n) => n.ToString(CultureInfo.CurrentCulture);
        private static string Things(int n) => n == 1 ? T("1 thing") : T("{0} things").Replace("{0}", N(n));

        /// <summary>The lines shown now, in order (for tests).</summary>
        public List<string> Lines() => _body.FindChildren("*", nameof(Label), true, false).OfType<Label>().Select(l => l.Text).ToList();

        /// <summary>A pack's Review N… button, or null (for tests).</summary>
        public Button? ReviewButton(string key) => Find("ReviewButton", key);

        private void ShowQuestion()
        {
            State = Stage.Asking;
            Clear();
            int all = _offers.Count;
            bool any = _offers.Any(o => !o.Plan.Nothing);
            _body.AddChild(Heading(any
                ? (all == 1 ? T("Atlas found things to look at in 1 map pack: fix them now?") : T("Atlas found things to look at in {0} map packs: fix them now?").Replace("{0}", N(all)))
                : (all == 1 ? T("Atlas found things only you can decide in 1 map pack.") : T("Atlas found things only you can decide in {0} map packs.").Replace("{0}", N(all)))));
            foreach (var o in _offers)
                _body.AddChild(o.Plan.Nothing
                    ? PackRow(T("{0} ({1}): {2} only you can decide.").Replace("{0}", o.Look.PackName).Replace("{1}", o.Look.Game).Replace("{2}", Things(o.Look.NeedsReview)), o.Look.PackKey, o.Look.File, o.Look.NeedsReview)
                    : PackRow(T("{0} ({1}): {2}").Replace("{0}", o.Look.PackName).Replace("{1}", o.Look.Game).Replace("{2}", o.Look.Preview), o.Look.PackKey, o.Look.File, 0));
            if (!any) return;
            _body.AddChild(Kit.Muted(T("Fix them now links each pin and tile whose best match is confident (85% or more, and no other row wants it) and sets the rest aside: one step per pack, which Undo reverses. You see every change before it's made.")));
            var buttons = new HBoxContainer { Visible = !_hostButtons };
            _buttons = buttons;
            buttons.AddThemeConstantOverride("separation", 8);
            var fix = Kit.Button(T("Fix them now…"), T("Shows every change for these packs at once; Apply makes them."), PressFixThemNow);
            fix.Name = "FixThemNow";
            var later = Kit.Button(T("Later"), T("Changes nothing. Each pack's row on its game's page keeps Fix and Review, and the Map Tracker's Notices list what Atlas noticed."), PressLater, flat: true);
            later.Name = "Later";
            buttons.AddChild(fix);
            buttons.AddChild(later);
            _body.AddChild(buttons);
        }

        /// <summary>Fix them now…: one preview of every change; Apply carries out those very plans.</summary>
        public void PressFixThemNow()
        {
            if (State != Stage.Asking || FixPreviewDialog.Showing != null) return;
            var packs = PackHandoff.Current(_offers);
            string title = packs.Count == 1 ? T("Fix them now: {0}").Replace("{0}", packs[0].Name) : T("Fix them now: {0} map packs").Replace("{0}", N(packs.Count));
            FixPreviewDialog.ShowAll(this, title, packs.Select(p => (p.Name, p.Plan, p.Report)).ToList(),
                () => Async.Fire(ApplyAsync(packs), "fixing the map packs"),
                _ => Async.Fire(ApplyAsync(packs), "fixing the map packs")); // nothing to change now: still answered, what's left is listed
        }

        /// <summary>Later: nothing changes (the packs' cards wait for Atlas's next start).</summary>
        public void PressLater()
        {
            if (State != Stage.Asking) return;
            State = Stage.Later;
            Clear();
            _body.AddChild(Kit.Text(T("Left as they are. Each pack's row on its game's page keeps Fix and Review, the Map Tracker's Notices list what Atlas noticed, and Atlas asks again the next time it starts and a slot opens the pack.")));
        }

        /// <summary>Presses a pack's Review N… (for tests).</summary>
        public void PressReview(string key) => ReviewButton(key)?.EmitSignal(BaseButton.SignalName.Pressed);

        /// <summary>Presses a pack's Undo (for tests).</summary>
        public void PressUndo(string key) => Find("UndoButton", key)?.EmitSignal(BaseButton.SignalName.Pressed);

        private async Task ApplyAsync(IReadOnlyList<HandoffPack> packs)
        {
            if (State != Stage.Asking) return;
            State = Stage.Fixing;
            Clear();
            _body.AddChild(Kit.Muted(T("Fixing, then checking the packs again…")));
            _results = PackHandoff.Apply(packs);
            await ShowResultsAsync();
        }

        private async Task ShowResultsAsync()
        {
            var counts = await Task.WhenAll(_results.Select(r => PackHandoff.LeftAsync(r.Pack.Key, RecheckWait)));
            if (!IsInstanceValid(this) || IsQueuedForDeletion()) return;
            State = Stage.Done;
            Clear();
            _body.AddChild(Heading(T("Atlas fixed what it could.")));
            foreach (var r in _results) _body.AddChild(ResultRow(r));
            var left = _results.Select((r, i) => (r, n: counts[i])).Where(x => x.n > 0).ToList();
            if (left.Count == 0) { _body.AddChild(Kit.Muted(T("Nothing is left for you to decide in these packs."))); return; }
            _body.AddChild(Heading(T("Left for you")));
            _body.AddChild(Kit.Muted(T("What Atlas can't decide by itself: two rows that want the same check, a picture the pack lacks, a pack made for another version. The Pack Doctor's guide takes you through each, one click at a time.")));
            foreach (var (r, n) in left)
                _body.AddChild(PackRow(T("{0}: {1} to decide.").Replace("{0}", r.Pack.Name).Replace("{1}", Things(n)), r.Pack.Key, r.Pack.File, n));
        }

        private Control ResultRow(HandoffResult r)
        {
            var row = Row();
            string text = _notes.TryGetValue(r.Pack.Key, out var note) ? note
                : r.Unreadable ? T("{0}: its file couldn't be read, so nothing was changed.").Replace("{0}", r.Pack.Name)
                : r.Fixed == null || r.Fixed.Nothing ? T("{0}: nothing Atlas could fix by itself.").Replace("{0}", r.Pack.Name)
                : r.Fixed.Describe(r.Pack.Name);
            row.AddChild(Kit.Text(text, r.Unreadable ? ThemeColors.Warning : null));
            if (r.UndoStep != null && !_notes.ContainsKey(r.Pack.Key) && PackFixes.UndoDescription(r.Pack.Key) == r.UndoStep)
                row.AddChild(PackButton("UndoButton", T("Undo"), T("Takes this pack's fix back in one step."), r.Pack.Key, () => UndoPack(r)));
            return row;
        }

        private void UndoPack(HandoffResult r)
        {
            // Only the step this card made: a later edit of the pack's fixes is the Doctor's to undo.
            bool undone = PackFixes.UndoDescription(r.Pack.Key) == r.UndoStep && PackFixes.Undo(r.Pack.Key);
            _notes[r.Pack.Key] = undone
                ? T("{0}: undone. Its rows are back to decide; its row on its game's page has Fix.").Replace("{0}", r.Pack.Name)
                : T("{0}: changed since; undo it in the Pack Doctor.").Replace("{0}", r.Pack.Name);
            Async.Fire(ShowResultsAsync(), "checking the map pack again");
        }

        private Control PackRow(string text, string key, string file, int toDecide)
        {
            var row = Row();
            row.AddChild(Kit.Text(text));
            if (toDecide > 0)
                row.AddChild(PackButton("ReviewButton", T("Review {0}…").Replace("{0}", N(toDecide)),
                    T("Opens the Pack Doctor's guide on this pack: one step at a time, each a click."), key, () => _openDoctor(file, PackDoctorWindow.GuideTab)));
            return row;
        }

        private static HBoxContainer Row()
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 8);
            return row;
        }

        private static Button PackButton(string name, string text, string tooltip, string key, Action pressed)
        {
            var button = Kit.Button(text, tooltip, pressed, small: true);
            button.Name = name;
            button.SetMeta("pack_key", key);
            return button;
        }

        private Button? Find(string name, string key) =>
            _body.FindChildren(name, nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.GetMeta("pack_key").AsString() == key);

        private static Label Heading(string text)
        {
            var label = Kit.Heading(text, 1.0f);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return label;
        }

        private void Clear()
        {
            foreach (var child in _body.GetChildren())
            {
                _body.RemoveChild(child);
                child.QueueFree();
            }
        }
    }
}
