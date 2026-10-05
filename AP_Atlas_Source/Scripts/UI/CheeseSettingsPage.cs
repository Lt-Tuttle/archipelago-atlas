using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.CheeseTracker;
using Godot;
using Color = Godot.Color;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Cheese Tracker tab's Settings page: the user's API key, which multiworld is linked to which tracker, the slots
    /// Atlas updates automatically, the site, and what Atlas does with all of it.
    /// </summary>
    public partial class CheeseSettingsPage : ScrollContainer
    {
        private readonly AppSettings _settings;
        private readonly CheeseTrackerService _cheese;
        private readonly Func<IReadOnlyList<MultiworldProfile>> _profiles;
        private readonly Action<string, Color> _toast;
        private readonly Action<string> _showView;

        private Label _account, _keyStatus, _siteStatus;
        private LineEdit _keyInput, _siteInput;
        private Button _saveKey, _removeKey;
        private VBoxContainer _linksBox, _autoBox;
        private string _linksSignature, _autoSignature;

        public CheeseSettingsPage(AppSettings settings, CheeseTrackerService cheese, Func<IReadOnlyList<MultiworldProfile>> profiles,
            Action<string, Color> toast, Action<string> showView)
        {
            _settings = settings;
            _cheese = cheese;
            _profiles = profiles;
            _toast = toast;
            _showView = showView;
            Name = "CheeseSettings";
            HorizontalScrollMode = ScrollMode.Disabled;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
        }

        public override void _Ready()
        {
            var page = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            page.AddThemeConstantOverride("separation", 8);
            AddChild(page);

            page.AddChild(Heading("Cheese Tracker settings", 1.3f));
            page.AddChild(Text("Cheese Tracker is the community tracker for async multiworlds. Atlas shows your multiworlds' trackers in this tab, " +
                               "suggests statuses from its logic, and can make the changes for you.", Colors.LightGray));

            page.AddChild(Heading("Your account"));
            _account = Text("", Colors.White);
            page.AddChild(_account);
            page.AddChild(Text("Reading trackers needs nothing. To change statuses, claim slots and edit notes as you, Atlas needs your API key:\n" +
                               "  1. Open Cheese Tracker's settings and sign in with Discord.\n" +
                               "  2. Under API key, press the ↻ button to generate a key, then copy it.\n" +
                               "  3. Paste it here and press Save key.", Colors.LightGray));
            var open = new Button { Text = "Open Cheese Tracker's settings ↗", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            open.Pressed += () => OS.ShellOpen(_cheese.Site + "/settings");
            page.AddChild(open);
            var keyRow = new HBoxContainer();
            keyRow.AddThemeConstantOverride("separation", 6);
            _keyInput = new LineEdit { Secret = true, PlaceholderText = "API key", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _keyInput.TextSubmitted += _ => SaveKey();
            keyRow.AddChild(_keyInput);
            _saveKey = new Button { Text = "Save key" };
            _saveKey.Pressed += SaveKey;
            keyRow.AddChild(_saveKey);
            _removeKey = new Button { Text = "Remove key" };
            _removeKey.Pressed += () =>
            {
                _cheese.RemoveKey();
                SetStatus(_keyStatus, "Removed. Atlas now only reads trackers.", Colors.Gray);
            };
            keyRow.AddChild(_removeKey);
            page.AddChild(keyRow);
            _keyStatus = Text("", Colors.Gray);
            _keyStatus.Visible = false;
            page.AddChild(_keyStatus);
            page.AddChild(Text("The key is stored encrypted for your Windows account and is only ever sent to the site below. " +
                               "Anyone with it can act as you on Cheese Tracker; if it leaks, generate a new one there (the old one stops working).", Colors.Gray));

            page.AddChild(Heading("Your multiworlds"));
            page.AddChild(Text("Link each multiworld to its Cheese Tracker page (an archipelago.gg room link works too).", Colors.Gray));
            _linksBox = new VBoxContainer();
            _linksBox.AddThemeConstantOverride("separation", 4);
            page.AddChild(_linksBox);

            page.AddChild(Heading("Automatic updates"));
            _autoBox = new VBoxContainer();
            _autoBox.AddThemeConstantOverride("separation", 4);
            page.AddChild(_autoBox);

            page.AddChild(Heading("What Atlas does"));
            page.AddChild(Text("• Reads a linked tracker every 10 minutes while one of its slots is connected or this tab shows it, and right before any change, " +
                               "so a change never overwrites someone else's newer edit. If the site has trouble, Atlas waits (1 minute, growing to 30) before asking again.\n" +
                               "• Suggests BK, Unblocked or Go mode from its logic (BK after 5 minutes with nothing in logic). It only suggests when " +
                               "its logic is verified for the slot (apworld matches the seed, rebuild matches the server).\n" +
                               "• Changes a slot automatically only if you turn that on for a slot claimed by your account. It never touches " +
                               "Soft BK, Goal, Done or Forfeit, makes at most one change every 5 minutes, and pauses if anyone else changes the status.\n" +
                               "• Never changes a slot claimed by someone else.", Colors.LightGray));

            page.AddChild(Heading("Site"));
            page.AddChild(Text("Only change this if you use another Cheese Tracker instance. Changing it removes the saved key (keys belong to one site).", Colors.Gray));
            var siteRow = new HBoxContainer();
            siteRow.AddThemeConstantOverride("separation", 6);
            _siteInput = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            siteRow.AddChild(_siteInput);
            var useSite = new Button { Text = "Use this site" };
            useSite.Pressed += () =>
            {
                string error = _cheese.SetSite(_siteInput.Text);
                SetStatus(_siteStatus, error ?? "Saved.", error == null ? Colors.Gray : Colors.Salmon);
            };
            siteRow.AddChild(useSite);
            var reset = new Button { Text = "Default" };
            reset.Pressed += () => _siteInput.Text = CheeseClient.DefaultInstance;
            siteRow.AddChild(reset);
            page.AddChild(siteRow);
            _siteStatus = Text("", Colors.Gray);
            _siteStatus.Visible = false;
            page.AddChild(_siteStatus);

            MainTrackerWindow.SetFontSizeRecursive(page, _settings.ContentFontSize);
            Refresh();
        }

        /// <summary>Updates everything shown (cheap; called whenever Cheese Tracker data changes while visible).</summary>
        public void Refresh()
        {
            if (_account == null) return;
            if (_cheese.KeyRejected)
                SetStatus(_account, "Cheese Tracker no longer accepts the saved key (was it regenerated?). Paste the current one below.", Colors.Orange);
            else if (_cheese.HasKey)
                SetStatus(_account, $"Signed in as {_cheese.AccountName ?? "your account"}. Atlas can change your slots' status.", Colors.LimeGreen);
            else
                SetStatus(_account, "No API key: Atlas can read trackers but not change them.", Colors.Gray);
            _removeKey.Disabled = !_cheese.HasKey && !_cheese.KeyRejected;
            if (!_siteInput.HasFocus()) _siteInput.Text = _cheese.Site;
            RefreshLinks();
            RefreshAuto();
        }

        private void RefreshLinks()
        {
            var profiles = _profiles();
            string signature = string.Join("|", profiles.Select(p => p.Id + "=" + p.Name + "=" + p.CheeseTrackerUrl + "=" + _cheese.RoomView(p.Id)?.Tracker?.Title)) + _cheese.HasKey;
            if (signature == _linksSignature) return;
            _linksSignature = signature;
            foreach (Node child in _linksBox.GetChildren()) child.QueueFree();
            if (profiles.Count == 0)
            {
                _linksBox.AddChild(Text("No multiworlds yet: add one on the Connections tab.", Colors.Gray));
                return;
            }
            foreach (var profile in profiles)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                var name = new Label { Text = profile.Name, CustomMinimumSize = new Vector2(160, 0), ClipText = true };
                row.AddChild(name);
                var room = _cheese.RoomView(profile.Id);
                var state = Text(room == null ? "Not linked" : "Linked: " + (string.IsNullOrWhiteSpace(room.Tracker?.Title) ? room.Link : room.Tracker.Title),
                    room == null ? Colors.Gray : Colors.LimeGreen);
                state.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(state);
                var p = profile;
                if (room != null)
                {
                    row.AddChild(SmallButton("Show", "Show this tracker", () => _showView(p.Id)));
                    row.AddChild(SmallButton("Change link…", "Link this multiworld to a different tracker", () => CheeseDialogs.Link(this, _cheese, p, _toast, Refresh)));
                    row.AddChild(SmallButton("Unlink…", "Stop using Cheese Tracker for this multiworld", () =>
                        CheeseDialogs.Confirm(this, $"Unlink {p.Name} from Cheese Tracker? Nothing changes on Cheese Tracker; Atlas just stops reading it (and stops automatic updates).", "Unlink",
                            () => _cheese.Unlink(p.Id))));
                }
                else
                {
                    row.AddChild(SmallButton("Link…", "Paste the tracker's link, or the archipelago.gg room link", () => CheeseDialogs.Link(this, _cheese, p, _toast, Refresh)));
                    if (_cheese.HasKey)
                        row.AddChild(SmallButton("Find on my dashboard", "Look for this room among the trackers on your Cheese Tracker dashboard",
                            () => CheeseDialogs.FindOnDashboard(this, _cheese, p, _toast, Refresh)));
                }
                _linksBox.AddChild(row);
            }
            MainTrackerWindow.SetFontSizeRecursive(_linksBox, _settings.ContentFontSize);
        }

        private void RefreshAuto()
        {
            var keys = _settings.CheeseAutoSlots.ToList();
            string signature = string.Join("|", keys.Select(k => k + "=" + _settings.CheeseAutoPaused.GetValueOrDefault(k)));
            if (signature == _autoSignature) return;
            _autoSignature = signature;
            foreach (Node child in _autoBox.GetChildren()) child.QueueFree();
            if (keys.Count == 0)
            {
                _autoBox.AddChild(Text("No slot is updated automatically. Turn it on for a slot you've claimed from this tab's details or the slot's Properties.", Colors.Gray));
                MainTrackerWindow.SetFontSizeRecursive(_autoBox, _settings.ContentFontSize);
                return;
            }
            foreach (var key in keys)
            {
                int bar = key.IndexOf('|');
                if (bar <= 0) continue;
                string profileId = key.Substring(0, bar), slotName = key.Substring(bar + 1);
                var profile = _profiles().FirstOrDefault(p => p.Id == profileId);
                string paused = _settings.CheeseAutoPaused.GetValueOrDefault(key);
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                row.AddChild(new Label { Text = $"{slotName} ({profile?.Name ?? "deleted multiworld"})", CustomMinimumSize = new Vector2(220, 0), ClipText = true });
                var state = Text(paused == null ? "On" : "Paused: " + paused, paused == null ? Colors.LimeGreen : Colors.Orange);
                state.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(state);
                if (paused != null && profile != null) row.AddChild(SmallButton("Resume", "Carry on from the status the slot has now", () => _cheese.ResumeAuto(profileId, slotName)));
                row.AddChild(SmallButton("Turn off", "Stop updating this slot automatically", () => _cheese.SetAuto(profileId, slotName, false)));
                _autoBox.AddChild(row);
            }
            MainTrackerWindow.SetFontSizeRecursive(_autoBox, _settings.ContentFontSize);
        }

        private async void SaveKey()
        {
            string key = _keyInput.Text;
            if (string.IsNullOrWhiteSpace(key)) return;
            _saveKey.Disabled = true;
            SetStatus(_keyStatus, "Checking the key with Cheese Tracker…", Colors.Gray);
            string error = await _cheese.SetKeyAsync(key);
            if (!IsInstanceValid(this)) return;
            _saveKey.Disabled = false;
            if (error == null)
            {
                _keyInput.Text = "";
                SetStatus(_keyStatus, "Saved.", Colors.Gray);
            }
            else SetStatus(_keyStatus, error, Colors.Salmon);
            Refresh();
        }

        private static void SetStatus(Label label, string text, Color color)
        {
            label.Text = text;
            label.Visible = !string.IsNullOrEmpty(text);
            label.AddThemeColorOverride("font_color", color);
        }

        private static Button SmallButton(string text, string tooltip, Action onPressed)
        {
            var b = new Button { Text = text, TooltipText = tooltip, FocusMode = FocusModeEnum.None };
            b.Pressed += () => onPressed();
            return b;
        }

        private static Label Heading(string text, float ratio = 1.15f)
        {
            var label = new Label { Text = text };
            label.AddThemeColorOverride("font_color", ThemeColors.Accent.Lightened(0.2f));
            label.SetMeta("font_size_ratio", ratio);
            return label;
        }

        private static Label Text(string text, Color color)
        {
            var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            label.AddThemeColorOverride("font_color", color);
            return label;
        }
    }
}
