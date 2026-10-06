#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

            page.AddChild(Kit.Heading("Cheese Tracker settings", 1.3f));
            page.AddChild(Kit.Text("Cheese Tracker is the community tracker for async multiworlds. Atlas shows your multiworlds' trackers in this tab, " +
                               "suggests statuses from its logic, and can make the changes for you.", ThemeColors.TextMuted));

            page.AddChild(Kit.Heading("Your account"));
            _account = Kit.Text("", ThemeColors.Text);
            page.AddChild(_account);
            page.AddChild(Kit.Text("Reading trackers needs nothing. To change statuses, claim slots and edit notes as you, Atlas needs your API key:\n" +
                               "  1. Open Cheese Tracker's settings and sign in with Discord.\n" +
                               "  2. Under API key, press the ↻ button to generate a key, then copy it.\n" +
                               "  3. Paste it here and press Save key.", ThemeColors.TextMuted));
            var open = new Button { Text = "Open Cheese Tracker's settings ↗", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            open.Pressed += () => AP_Atlas.Core.ExternalLinks.OpenWeb(_cheese.Site + "/settings");
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
                SetStatus(_keyStatus, "Removed. Atlas now only reads trackers.", ThemeColors.TextSubtle);
            };
            keyRow.AddChild(_removeKey);
            page.AddChild(keyRow);
            _keyStatus = Kit.Text("", ThemeColors.TextSubtle);
            _keyStatus.Visible = false;
            page.AddChild(_keyStatus);
            page.AddChild(Kit.Text("The key is stored encrypted for your Windows account and is only ever sent to the site below. " +
                               "Anyone with it can act as you on Cheese Tracker; if it leaks, generate a new one there (the old one stops working).", ThemeColors.TextSubtle));

            page.AddChild(Kit.Heading("Your multiworlds"));
            page.AddChild(Kit.Text("Link each multiworld to its Cheese Tracker page (an archipelago.gg room link works too).", ThemeColors.TextSubtle));
            _linksBox = new VBoxContainer();
            _linksBox.AddThemeConstantOverride("separation", 4);
            page.AddChild(_linksBox);

            page.AddChild(Kit.Heading("Automatic updates"));
            _autoBox = new VBoxContainer();
            _autoBox.AddThemeConstantOverride("separation", 4);
            page.AddChild(_autoBox);

            page.AddChild(Kit.Heading("What Atlas does"));
            page.AddChild(Kit.Text("• Reads a linked tracker every 10 minutes while one of its slots is connected or this tab shows it, and right before any change, " +
                               "so a change never overwrites someone else's newer edit. If the site has trouble, Atlas waits (1 minute, growing to 30) before asking again.\n" +
                               "• Suggests BK, Unblocked or Go mode from its logic (BK after 5 minutes with nothing in logic). It only suggests when " +
                               "its logic is verified for the slot (apworld matches the seed, rebuild matches the server).\n" +
                               "• Changes a slot automatically only if you turn that on for a slot claimed by your account. It never touches " +
                               "Soft BK, Goal, Done or Forfeit, makes at most one change every 5 minutes, and pauses if anyone else changes the status.\n" +
                               "• Never changes a slot claimed by someone else.", ThemeColors.TextMuted));

            page.AddChild(Kit.Heading("Site"));
            page.AddChild(Kit.Text("Only change this if you use another Cheese Tracker instance. Changing it removes the saved key (keys belong to one site).", ThemeColors.TextSubtle));
            var siteRow = new HBoxContainer();
            siteRow.AddThemeConstantOverride("separation", 6);
            _siteInput = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            siteRow.AddChild(_siteInput);
            var useSite = new Button { Text = "Use this site" };
            useSite.Pressed += () =>
            {
                string error = _cheese.SetSite(_siteInput.Text);
                SetStatus(_siteStatus, error ?? "Saved.", error == null ? ThemeColors.TextSubtle : ThemeColors.Error);
            };
            siteRow.AddChild(useSite);
            var reset = new Button { Text = "Default" };
            reset.Pressed += () => _siteInput.Text = CheeseClient.DefaultInstance;
            siteRow.AddChild(reset);
            page.AddChild(siteRow);
            _siteStatus = Kit.Text("", ThemeColors.TextSubtle);
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
                SetStatus(_account, "Cheese Tracker no longer accepts the saved key (was it regenerated?). Paste the current one below.", ThemeColors.Warning);
            else if (_cheese.HasKey)
                SetStatus(_account, $"Signed in as {_cheese.AccountName ?? "your account"}. Atlas can change your slots' status.", ThemeColors.Success);
            else
                SetStatus(_account, "No API key: Atlas can read trackers but not change them.", ThemeColors.TextSubtle);
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
                _linksBox.AddChild(Kit.Text("No multiworlds yet: add one on the Multiworlds page.", ThemeColors.TextSubtle));
                return;
            }
            foreach (var profile in profiles)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                var name = new Label { Text = profile.Name, CustomMinimumSize = new Vector2(160, 0), ClipText = true };
                row.AddChild(name);
                var room = _cheese.RoomView(profile.Id);
                var state = Kit.Text(room == null ? "Not linked" : "Linked: " + (string.IsNullOrWhiteSpace(room.Tracker?.Title) ? room.Link : room.Tracker.Title), room == null ? ThemeColors.TextSubtle : ThemeColors.Success);
                state.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(state);
                var p = profile;
                if (room != null)
                {
                    row.AddChild(Kit.Button("Show", "Show this tracker", () => _showView(p.Id)));
                    row.AddChild(Kit.Button("Change link…", "Link this multiworld to a different tracker", () => CheeseDialogs.Link(this, _cheese, p, _toast, Refresh)));
                    row.AddChild(Kit.Button("Unlink…", "Stop using Cheese Tracker for this multiworld", () =>
                        CheeseDialogs.Confirm(this, $"Unlink {p.Name} from Cheese Tracker? Nothing changes on Cheese Tracker; Atlas just stops reading it (and stops automatic updates).", "Unlink",
                            () => _cheese.Unlink(p.Id))));
                }
                else
                {
                    row.AddChild(Kit.Button("Link…", "Paste the tracker's link, or the archipelago.gg room link", () => CheeseDialogs.Link(this, _cheese, p, _toast, Refresh)));
                    if (_cheese.HasKey)
                        row.AddChild(Kit.Button("Find on my dashboard", "Look for this room among the trackers on your Cheese Tracker dashboard", () => CheeseDialogs.FindOnDashboard(this, _cheese, p, _toast, Refresh)));
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
                _autoBox.AddChild(Kit.Text("No slot is updated automatically. Turn it on for a slot you've claimed from this tab's details or the slot's Properties.", ThemeColors.TextSubtle));
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
                var state = Kit.Text(paused == null ? "On" : "Paused: " + paused, paused == null ? ThemeColors.Success : ThemeColors.Warning);
                state.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(state);
                if (paused != null && profile != null) row.AddChild(Kit.Button("Resume", "Carry on from the status the slot has now", () => _cheese.ResumeAuto(profileId, slotName)));
                row.AddChild(Kit.Button("Turn off", "Stop updating this slot automatically", () => _cheese.SetAuto(profileId, slotName, false)));
                _autoBox.AddChild(row);
            }
            MainTrackerWindow.SetFontSizeRecursive(_autoBox, _settings.ContentFontSize);
        }

        private void SaveKey() => AP_Atlas.Core.Async.Fire(SaveKeyAsync(), "saving your Cheese Tracker API key");

        private async Task SaveKeyAsync()
        {
            string key = _keyInput.Text;
            if (string.IsNullOrWhiteSpace(key)) return;
            _saveKey.Disabled = true;
            SetStatus(_keyStatus, "Checking the key with Cheese Tracker…", ThemeColors.TextSubtle);
            string error = await _cheese.SetKeyAsync(key);
            if (!IsInstanceValid(this)) return;
            _saveKey.Disabled = false;
            if (error == null)
            {
                _keyInput.Text = "";
                SetStatus(_keyStatus, "Saved.", ThemeColors.TextSubtle);
            }
            else SetStatus(_keyStatus, error, ThemeColors.Error);
            Refresh();
        }

        private static void SetStatus(Label label, string text, Color color)
        {
            label.Text = text;
            label.Visible = !string.IsNullOrEmpty(text);
            label.AddThemeColorOverride("font_color", color);
        }

    }
}
