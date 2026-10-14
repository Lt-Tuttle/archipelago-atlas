#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Map Tracker's Notices, beside its maps list: what the pack gets wrong for this seed (from the Pack Doctor's
    /// report), and what play turns up (a check the map has no pin for: "which pin is it?"), each in plain words with what
    /// to do about it. The tab and the activity bar's Map button carry their count.
    /// </summary>
    public partial class MapTrackerControl
    {
        /// <summary>One notice: what it's about, what it means, and what can be done (each a label and its action).</summary>
        public sealed record Notice(string Key, string Title, string Detail, IReadOnlyList<(string Label, Action Act)> Actions, bool FromPlay, long LocationId = 0);

        /// <summary>The notices changed (their count is on the activity bar).</summary>
        public event Action? NoticesChanged;

        /// <summary>The most notices play keeps (the oldest go first).</summary>
        public const int PlayNoticeCap = 20;

        /// <summary>More fresh checks than this at once is a release or a collect, not play: no notices for them.</summary>
        public const int PlayBurst = 10;

        private readonly List<Notice> _playNotices = new List<Notice>();
        private Button? _mapsTab, _noticesTab;
        private Control? _mapsPane;
        private ScrollContainer? _noticesPane;
        private VBoxContainer? _noticesBox;
        private bool _noticesShown;

        /// <summary>How many notices there are (the tab's and the activity bar's count).</summary>
        public int NoticeCount => _playNotices.Count + _attention.Count(r => r.Key != "more");

        /// <summary>Every notice: play's first (newest first), then the pack's.</summary>
        public List<Notice> Notices()
        {
            var all = new List<Notice>(_playNotices.AsEnumerable().Reverse());
            foreach (var row in _attention)
            {
                var open = row.Open;
                var actions = new List<(string, Action)>();
                if (row.Key.StartsWith("map:nobg:", StringComparison.Ordinal))
                {
                    string mapName = row.Key.Substring("map:nobg:".Length);
                    actions.Add((Translate("Fix it here"), () => FixMapHere(mapName)));
                }
                actions.Add((row.Key == "more" ? Translate("Open the Pack Doctor") : Translate("Fix in the Pack Doctor"), () => open?.Invoke()));
                all.Add(new Notice(row.Key, row.Text, DetailOf(row), actions, FromPlay: false));
            }
            return all;
        }

        /// <summary>Whether the Notices are shown (rather than the maps).</summary>
        public bool NoticesShown => _noticesShown;

        /// <summary>Shows the Notices, or the maps.</summary>
        public void ShowNotices(bool show)
        {
            _noticesShown = show;
            if (_mapsPane != null) _mapsPane.Visible = !show;
            if (_noticesPane != null) _noticesPane.Visible = show;
            if (_mapsTab != null) _mapsTab.SetPressedNoSignal(!show);
            if (_noticesTab != null) _noticesTab.SetPressedNoSignal(show);
            if (show) RenderNotices();
        }

        /// <summary>Does one of a notice's actions (for tests: what pressing its button does).</summary>
        public void PressNotice(string key, int action)
        {
            var notice = Notices().FirstOrDefault(n => n.Key == key);
            if (notice != null && action >= 0 && action < notice.Actions.Count) notice.Actions[action].Act();
        }

        private static string DetailOf(AttentionRow row)
        {
            // The tooltip is the finding's title, then what it means and what to do; the card shows the rest.
            var lines = (row.Tip ?? "").Split('\n').Skip(1).Where(l => !l.StartsWith("Opens the Pack Doctor", StringComparison.Ordinal));
            return string.Join(" ", lines).Trim();
        }

        /// <summary>Shows a map that has no picture and opens its fixes.</summary>
        private void FixMapHere(string mapName)
        {
            var map = _pack?.Maps.Values.FirstOrDefault(m => m.Name == mapName);
            if (map == null) return;
            ShowNotices(false);
            SwitchMap(map.Id);
            PackFixMenus.Show(this, FixProblemItems());
        }

        /// <summary>The Maps | Notices switch above the explorer's list, and the Notices pane (added by the caller after it).</summary>
        private Control BuildNoticeTabs(Control mapsPane)
        {
            _mapsPane = mapsPane;
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_right", 10);
            margin.AddThemeConstantOverride("margin_top", 6);
            var tabs = new HBoxContainer();
            tabs.AddThemeConstantOverride("separation", 4);
            margin.AddChild(tabs);
            var group = new ButtonGroup();
            _mapsTab = Kit.Button(Translate("Maps"), Translate("The pack's maps"), () => ShowNotices(false), small: true);
            _mapsTab.ToggleMode = true;
            _mapsTab.ButtonGroup = group;
            _mapsTab.ButtonPressed = true;
            _noticesTab = Kit.Button(Translate("Notices"), Translate("What the pack gets wrong for your seed, and checks you made that the map has no pin for: each says what it means and what to do."), () => ShowNotices(true), small: true);
            _noticesTab.ToggleMode = true;
            _noticesTab.ButtonGroup = group;
            _noticesTab.SetMeta("notices_tab", true);
            tabs.AddChild(_mapsTab);
            tabs.AddChild(_noticesTab);
            _noticesPane = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, Visible = false };
            var paneMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            paneMargin.AddThemeConstantOverride("margin_left", 10);
            paneMargin.AddThemeConstantOverride("margin_right", 10);
            paneMargin.AddThemeConstantOverride("margin_top", 6);
            paneMargin.AddThemeConstantOverride("margin_bottom", 10);
            _noticesBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _noticesBox.AddThemeConstantOverride("separation", 8);
            paneMargin.AddChild(_noticesBox);
            _noticesPane.AddChild(paneMargin);
            UpdateNoticesTab();
            return margin;
        }

        private void UpdateNoticesTab()
        {
            if (_noticesTab == null) return;
            int n = NoticeCount;
            _noticesTab.Text = n > 0 ? Translate("Notices ({0})").Replace("{0}", n.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Translate("Notices");
            if (n > 0) _noticesTab.AddThemeColorOverride("font_color", ThemeColors.Warning);
            else _noticesTab.RemoveThemeColorOverride("font_color");
        }

        /// <summary>The notices changed: the tab's count, the pane when shown, and the activity bar.</summary>
        private void OnNoticesChanged()
        {
            UpdateNoticesTab();
            if (_noticesShown) RenderNotices();
            NoticesChanged?.Invoke();
        }

        private void RenderNotices()
        {
            if (_noticesBox == null) return;
            foreach (Node child in _noticesBox.GetChildren())
            {
                _noticesBox.RemoveChild(child);
                child.QueueFree();
            }
            _fixAllButton = null;
            var notices = Notices();
            if (notices.Count == 0)
            {
                var none = Kit.Subtle(Translate("Nothing to look at: the pack fits this seed as far as Atlas can tell, and every check you've made has its pin."));
                none.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _noticesBox.AddChild(none);
                return;
            }
            if (_fixAll != null)
            {
                var fixAll = _fixAll;
                _fixAllButton = Kit.Button(Translate("Fix what Atlas can"), Translate("Links every pin and tile whose best match is confident (85% or more, no clash) and sets the rest aside, in one step; a card says what was done, and Undo in the Pack Doctor reverses it."), () => fixAll(), small: true);
                _fixAllButton.SetMeta("attention_fix", true);
                _fixAllButton.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                _noticesBox.AddChild(_fixAllButton);
            }
            foreach (var notice in notices) _noticesBox.AddChild(NoticeCard(notice));
        }

        private Control NoticeCard(Notice notice)
        {
            var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            card.SetMeta(notice.FromPlay ? "play_notice" : "attention_row", notice.Key);
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = ThemeColors.SurfaceSunken,
                BorderColor = notice.FromPlay ? ThemeColors.Accent : ThemeColors.Warning,
                BorderWidthLeft = 3,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 10,
                ContentMarginRight = 8,
                ContentMarginTop = 6,
                ContentMarginBottom = 6
            });
            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 4);
            var title = new Label { Text = notice.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(120, 0) };
            title.AddThemeColorOverride("font_color", ThemeColors.Text);
            column.AddChild(title);
            if (!string.IsNullOrWhiteSpace(notice.Detail))
            {
                var detail = Kit.Subtle(notice.Detail);
                detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                detail.CustomMinimumSize = new Vector2(120, 0);
                column.AddChild(detail);
            }
            var buttons = new HFlowContainer();
            buttons.AddThemeConstantOverride("h_separation", 6);
            buttons.AddThemeConstantOverride("v_separation", 4);
            foreach (var (label, act) in notice.Actions)
            {
                var run = act;
                buttons.AddChild(Kit.Button(label, label, () => run(), small: true));
            }
            if (notice.FromPlay)
            {
                string key = notice.Key;
                buttons.AddChild(Kit.Button(Translate("Dismiss"), Translate("Forget this notice"), () => DismissPlayNotice(key), small: true, flat: true));
            }
            column.AddChild(buttons);
            card.AddChild(column);
            return card;
        }

        // ---- Learn while playing: a check the map has no pin for ----

        /// <summary>
        /// Checks just made (not the first set a slot reports, nor a burst: a release or a collect) that no pin shows: a
        /// notice each, asking which pin it is (or to place it).
        /// </summary>
        private void NoticeUnshownChecks(IReadOnlyList<long> checkedInOrder, HashSet<long> before)
        {
            if (_pack == null || Fixing is not { } fix) return;
            var fresh = checkedInOrder.Where(id => !before.Contains(id)).ToList();
            if (fresh.Count == 0 || fresh.Count > PlayBurst) return;
            bool changed = false;
            foreach (long id in fresh)
            {
                if (_locIdToMap.ContainsKey(id) || Excluded(id)) continue;
                string key = "play:unshown:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (_playNotices.Any(n => n.Key == key)) continue;
                string name = fix.Index.LocationName(id) ?? Translate("Location {0}").Replace("{0}", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                long locationId = id;
                _playNotices.Add(new Notice(key,
                    Translate("You checked {0}: no pin shows it").Replace("{0}", name),
                    Translate("Show Atlas which pin it is and it shows there from now on (a Pack Doctor fix, with Undo), or place a pin for it."),
                    new List<(string, Action)>
                    {
                        (Translate("Pick its pin"), () => StartPicking(locationId, name)),
                        (Translate("Place it on the map"), () => { ShowNotices(false); StartPlacing(locationId, name); }),
                    }, FromPlay: true, LocationId: id));
                changed = true;
            }
            while (_playNotices.Count > PlayNoticeCap) _playNotices.RemoveAt(0);
            if (changed) OnNoticesChanged();
        }

        /// <summary>Play's notices whose check has a pin now (a fix, or another pack) go.</summary>
        private void PrunePlayNotices()
        {
            if (_playNotices.RemoveAll(n => n.LocationId != 0 && _locIdToMap.ContainsKey(n.LocationId)) > 0) OnNoticesChanged();
        }

        private void DismissPlayNotice(string key)
        {
            if (_playNotices.RemoveAll(n => n.Key == key) > 0) OnNoticesChanged();
        }

        /// <summary>A location waiting for its pin: the next pin pressed shows it.</summary>
        private (long Id, string Name)? _pickingFor;

        /// <summary>The location the next pin pressed will show (for tests), or null.</summary>
        public string? PickingFor => _pickingFor?.Name;

        private void StartPicking(long id, string name)
        {
            CancelPlacing();
            ShowNotices(false);
            // The bar says what the next press does; a click on the map itself (no pin) cancels.
            StartPlace(Translate("Click the pin that shows {0}").Replace("{0}", name), _ => { });
            _pickingFor = (id, name);
        }

        /// <summary>
        /// A pin pressed while a location waits for its pin: a pin with one section shows it there at once; with several,
        /// a menu asks which section. True when the press was taken.
        /// </summary>
        private bool TakePinForPicking(PopTrackerLocation pin)
        {
            if (_pickingFor is not { } wanted || Fixing is not { } fix) return false;
            CancelPlacing();
            var sections = pin.Sections != null ? pin.Sections.Where(s => s != null).ToList() : new List<PopTrackerSection>();
            void Link(string? section) => fix.Apply(Translate("{0} shows {1} now.").Replace("{0}", string.IsNullOrEmpty(section) ? pin.Name : section).Replace("{1}", wanted.Name), () =>
                PackFixActions.LinkSection(fix.Original, pin.FullPath, section ?? "", wanted.Id, wanted.Name));
            if (sections.Count <= 1)
            {
                Link(sections.FirstOrDefault()?.Name);
                return true;
            }
            var items = new List<FixItem> { new FixItem(Translate("Which of {0}'s checks is {1}?").Replace("{0}", pin.Name).Replace("{1}", wanted.Name), null) };
            foreach (var sec in sections)
            {
                string? name = sec.Name;
                items.Add(new FixItem($"\"{name}\"", () => Link(name)));
            }
            PackFixMenus.Show(this, items);
            return true;
        }

        /// <summary>A pin pressed while picking (for tests: what a press on that pin does).</summary>
        public bool PressPinForPicking(PopTrackerLocation pin) => TakePinForPicking(pin);
    }
}
