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
    public partial class PropertiesPanel
    {
        // =====================================================================
        // Cheese Tracker: a slot's status there, and the whole room for a profile
        // =====================================================================

        private HFlowContainer SectionButtons()
        {
            var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            flow.AddThemeConstantOverride("h_separation", 4);
            flow.AddThemeConstantOverride("v_separation", 4);
            Target.AddChild(flow);
            return flow;
        }

        private static Button SectionButton(HFlowContainer flow, string text, string tooltip, Action onPressed, bool enabled = true)
        {
            var b = new Button { Text = text, TooltipText = tooltip ?? "", Disabled = !enabled, FocusMode = FocusModeEnum.None };
            b.SetMeta("font_size_ratio", 0.9);
            b.Pressed += () => onPressed();
            flow.AddChild(b);
            return b;
        }

        /// <summary>Runs a Cheese Tracker change; a failure is shown as a toast (the section shows the new state).</summary>
        private void RunCheese(Task<string> change, string success = null) => CheeseDialogs.Run(this, change, _host.Toast, success, QueueRefresh);

        private void ConfirmCheese(string text, string okText, Action onConfirm) => CheeseDialogs.Confirm(this, text, okText, onConfirm);

        private void ShowCheeseLinkDialog(MultiworldProfile profile) => CheeseDialogs.Link(this, _host.Cheese, profile, _host.Toast, QueueRefresh);

        private void ShowCheeseNotesDialog(string profileId, string slotName, string notes) =>
            CheeseDialogs.Notes(this, slotName, notes, text => RunCheese(_host.Cheese.SetNotesAsync(profileId, slotName, text), "Notes saved"));

        private void FindCheeseTracker(MultiworldProfile profile) => CheeseDialogs.FindOnDashboard(this, _host.Cheese, profile, _host.Toast, QueueRefresh);

        private void BuildCheese(string profileId, string slotName, bool live)
        {
            using var __perf = PerfMonitor.Measure("Properties: Cheese Tracker");
            var cheese = _host.Cheese;
            var profile = _host.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (cheese == null || profile == null) return;
            Section("Cheese Tracker");
            if (string.IsNullOrWhiteSpace(profile.CheeseTrackerUrl))
            {
                AddHint($"Link {profile.Name} to its Cheese Tracker page to see and change this slot's status there from here.");
                CheeseLinkButtons(profile);
                return;
            }
            cheese.Watch(profileId);
            var view = cheese.SlotView(profileId, slotName);
            var room = view?.Room;
            if (room?.Tracker == null)
            {
                Row("Tracker", room?.Busy == true ? Colored("Reading…", Muted) : Colored(room?.Problem ?? "Not read yet", room?.Problem != null ? Warn : Muted));
                CheeseRoomButtons(profile, room, full: false);
                return;
            }
            var row = view.Row;
            if (row == null)
            {
                AddText(Colored(view.NotFound ?? "This slot isn't on the linked tracker.", Warn));
                CheeseRoomButtons(profile, room, full: false);
                return;
            }

            Row("Status", Colored(CtStatus.Label(row.Progression), AP_Atlas.UI.CheeseColors.Of(row.Progression)) + Colored("  ·  ", Muted) +
                Colored(CtStatus.Label(row.Completion), AP_Atlas.UI.CheeseColors.Of(row.Completion)));
            Row("Claimed by", OwnerText(row, view.Ownership));
            Row("Checks", Colored($"{row.ChecksDone} / {row.ChecksTotal}", row.ChecksTotal > 0 && row.ChecksDone >= row.ChecksTotal ? Good : Colors.White),
                "As Cheese Tracker last read them from the Archipelago tracker");
            Row("Last activity", ActivityText(row, room.Tracker));
            PlainRow("Ping", CtStatus.Label(row.Ping));
            Row("Notes", string.IsNullOrWhiteSpace(row.Notes) ? Colored("None", Muted) : Colored(row.Notes.Length > 500 ? row.Notes.Substring(0, 500) + "…" : row.Notes, Colors.White));

            var advice = view.Advice;
            if (!live) Row("Atlas's logic", Colored("Connect this slot for Atlas to suggest a status.", Muted));
            else if (advice == null) Row("Atlas's logic", Colored("Working it out…", Muted));
            else if (advice.InSync) Row("Atlas's logic", Colored("Agrees: " + advice.Reason, Good));
            else if (advice.Status != null)
                Row("Atlas suggests", Colored(CtStatus.Label(advice.Status), AP_Atlas.UI.CheeseColors.Of(advice.Status)) + "  " +
                    Colored(advice.Reason + (advice.Ready ? "" : " (confirming)"), Colors.LightGray),
                    "From Atlas's logic for this slot. BK is suggested after 5 minutes with nothing in logic; good news after a minute.");
            else if (advice.Quiet != null) Row("Atlas's logic", Colored(advice.Quiet, Muted));

            bool canEdit = view.CannotEdit == null && !view.Busy;
            var buttons = SectionButtons();
            if (advice?.Status != null)
                SectionButton(buttons, "Set " + CtStatus.Label(advice.Status), $"{advice.Reason}. Sets {slotName} to {CtStatus.Label(advice.Status)} on Cheese Tracker.",
                    () => RunCheese(cheese.SetProgressionAsync(profileId, slotName, advice.Status)), canEdit);
            foreach (var status in CtStatus.ProgressionIds)
            {
                if (status == "unknown" || status == row.Progression || status == advice?.Status) continue;
                SectionButton(buttons, CtStatus.Label(status), $"Set {slotName} to {CtStatus.Label(status)} on Cheese Tracker",
                    () => RunCheese(cheese.SetProgressionAsync(profileId, slotName, status)), canEdit);
            }
            if (row.Progression is "bk" or "soft_bk")
                SectionButton(buttons, "Still BK", "Tell the room you're still watching this slot (resets its inactivity clock)",
                    () => RunCheese(cheese.StillBkAsync(profileId, slotName), "Marked still BK"), canEdit);
            SectionButton(buttons, "Notes…", "Edit this slot's notes on Cheese Tracker", () => ShowCheeseNotesDialog(profileId, slotName, row.Notes), canEdit);
            AddCheesePingMenu(buttons, profileId, slotName, row.Ping, canEdit);
            AddCheeseCompletionMenu(buttons, profileId, slotName, row, canEdit);
            if (view.Ownership is CheeseOwnership.Nobody or CheeseOwnership.YouByName)
                SectionButton(buttons, "Claim", "Claim this slot with your Cheese Tracker account",
                    () => RunCheese(cheese.ClaimAsync(profileId, slotName), $"Claimed {slotName}"), cheese.CanClaim(profileId, row) && !view.Busy);
            else if (view.Ownership == CheeseOwnership.You)
                SectionButton(buttons, "Disclaim…", "Release your claim on this slot",
                    () => CheeseDialogs.ConfirmDisclaim(this, slotName, () => RunCheese(cheese.DisclaimAsync(profileId, slotName), $"Released {slotName}")), canEdit);
            SectionButton(buttons, "Open ↗", "Open the tracker on Cheese Tracker", () => AP_Atlas.Core.ExternalLinks.OpenWeb(room.Link));
            SectionButton(buttons, "In the tab", "Show the whole room in the Cheese Tracker tab", () => _host.ShowCheeseTab(profileId));
            SectionButton(buttons, "Refresh", "Read the tracker again now", () => RunCheese(cheese.RefreshAsync(profileId)), !room.Busy);

            // A toggle button rather than a checkbox: the theme's checkboxes show nothing when unticked.
            var auto = new Button
            {
                Text = view.AutoOn ? "Updating automatically" : "Update automatically",
                ToggleMode = true,
                ButtonPressed = view.AutoOn,
                FocusMode = FocusModeEnum.None,
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                Disabled = !view.AutoOn && (view.Ownership != CheeseOwnership.You || view.CannotEdit != null),
                TooltipText = "Atlas sets BK, Unblocked and Go mode from its logic while this slot is connected.\n" +
                              "Only for slots claimed by your account; never Soft BK, Goal, Done or Forfeit; it pauses if anyone else changes the status."
            };
            auto.Toggled += on =>
            {
                string error = cheese.SetAuto(profileId, slotName, on);
                if (error != null) _host.Toast(error, Bad);
                QueueRefresh();
            };
            Target.AddChild(auto);
            if (view.AutoOn && view.AutoPaused != null)
            {
                AddText(Colored("Paused: " + view.AutoPaused + ".", Warn));
                var resume = SectionButtons();
                SectionButton(resume, "Resume", "Carry on from the status the slot has now", () => cheese.ResumeAuto(profileId, slotName));
            }
            else if (view.AutoOn)
                AddHint("Atlas keeps BK, Unblocked and Go mode current from its logic. It never touches Soft BK, Goal, Done or Forfeit, and pauses if anyone else changes the status.");
            else if (view.Ownership != CheeseOwnership.You && cheese.CanClaim(profileId, row))
                AddHint("Claim this slot to change its status from Atlas and let Atlas keep it updated automatically.");

            if (view.CannotEdit != null) AddHint(view.CannotEdit);
            if (view.LastError != null) AddText(Colored(view.LastError, Warn));
            if (view.Busy) AddHint("Updating Cheese Tracker…");
            Row("Read", Colored(room.Busy ? "Reading…" : CtTime.Ago(room.FetchedUtc), Muted),
                "Atlas reads the tracker every 10 minutes while one of its slots is connected, and again right before each change");
            if (room.Problem != null) CheeseProblem(profile, room);
        }

        private void BuildCheeseRoom(MultiworldProfile profile)
        {
            using var __perf = PerfMonitor.Measure("Properties: Cheese Tracker room");
            var cheese = _host.Cheese;
            if (cheese == null) return;
            Section("Cheese Tracker");
            if (string.IsNullOrWhiteSpace(profile.CheeseTrackerUrl))
            {
                AddHint("Not linked. Link this multiworld to its Cheese Tracker page to see everyone's status here and set your slots' status from Atlas.");
                CheeseLinkButtons(profile);
                return;
            }
            cheese.Watch(profile.Id);
            var room = cheese.RoomView(profile.Id);
            if (room == null)
            {
                AddText(Colored("The saved Cheese Tracker link can't be used; link it again.", Warn));
                CheeseLinkButtons(profile);
                return;
            }
            var t = room.Tracker;
            Row("Tracker", Link(string.IsNullOrWhiteSpace(t?.Title) ? room.Link : t.Title, () => AP_Atlas.Core.ExternalLinks.OpenWeb(room.Link)));
            if (!string.IsNullOrEmpty(t?.OwnerName)) PlainRow("Organizer", t.OwnerName);
            Row("Read", Colored(room.Busy ? "Reading…" : CtTime.Ago(room.FetchedUtc), Muted),
                "Atlas reads the tracker every 10 minutes while one of its slots is connected, and again right before each change");
            if (room.OtherSite) AddHint("This tracker is on another Cheese Tracker site than the one in Cheese Tracker → Settings, so Atlas only reads it.");
            if (room.Problem != null) CheeseProblem(profile, room);
            CheeseRoomButtons(profile, room, full: true);
            if (t == null || t.Games.Count == 0) return;

            var counts = t.Games.GroupBy(CtStatus.Headline).ToDictionary(g => g.Key ?? "unknown", g => g.Count());
            var order = new[] { "bk", "soft_bk", "unknown", "unblocked", "go", "all_checks", "goal", "done", "released" };
            Row("Slots", Colored($"{t.Games.Count}: ", Colors.LightGray) +
                string.Join(Colored(", ", Muted), order.Where(counts.ContainsKey).Select(id => Colored($"{counts[id]} {CtStatus.Label(id)}", AP_Atlas.UI.CheeseColors.Of(id)))));
            long done = t.Games.Sum(g => (long)g.ChecksDone), total = t.Games.Sum(g => (long)g.ChecksTotal);
            if (total > 0) PlainRow("Checks", $"{done} / {total} ({100.0 * done / total:0}%)");

            // This profile's slots link to their own Properties.
            var mine = new Dictionary<int, string>();
            foreach (var name in profile.Slots)
            {
                var row = cheese.SlotView(profile.Id, name)?.Row;
                if (row != null) mine[row.Id] = name;
            }
            var lines = new List<string>();
            foreach (var g in t.Games.OrderBy(g => g.Position))
            {
                string headline = CtStatus.Headline(g);
                string name = mine.TryGetValue(g.Id, out var slotName)
                    ? LinkTo(g.Name, InspectTarget.ForSlot(profile.Id, slotName), Colors.Magenta)
                    : Colored(g.Name, Colors.White);
                string owner = string.IsNullOrEmpty(g.OwnerName) ? Colored("unclaimed", Muted)
                    : Colored(g.OwnerName + (g.OwnerAway ? " (away)" : ""), g.OwnerAway ? Warn : Colors.LightGray);
                lines.Add($"{Colored(g.Position + ".", Muted)} {name} {Colored(g.Game, Muted)}  {Colored(CtStatus.Label(headline), AP_Atlas.UI.CheeseColors.Of(headline))}  " +
                          $"{owner}  {Colored($"{g.ChecksDone}/{g.ChecksTotal}", Colors.LightGray)}  {ActivityText(g, t)}" +
                          (string.IsNullOrWhiteSpace(g.Notes) ? "" : Colored("  *notes", Colors.LightGray)));
            }
            AddText(string.Join("\n", lines));
        }

        private void CheeseLinkButtons(MultiworldProfile profile)
        {
            var buttons = SectionButtons();
            SectionButton(buttons, "Link…", "Paste the tracker's Cheese Tracker link, or the archipelago.gg room link", () => ShowCheeseLinkDialog(profile));
            if (_host.Cheese.HasKey)
                SectionButton(buttons, "Find on my dashboard", "Look for this room among the trackers on your Cheese Tracker dashboard", () => FindCheeseTracker(profile));
            else
                SectionButton(buttons, "Cheese Tracker settings…", "Add your API key so Atlas can change statuses as you", () => _host.OpenCheeseSettings());
        }

        private void CheeseRoomButtons(MultiworldProfile profile, CheeseRoomView room, bool full)
        {
            var buttons = SectionButtons();
            if (room != null)
            {
                SectionButton(buttons, "Refresh", "Read the tracker again now", () => RunCheese(_host.Cheese.RefreshAsync(profile.Id)), !room.Busy);
                SectionButton(buttons, "Open ↗", "Open the tracker on Cheese Tracker", () => AP_Atlas.Core.ExternalLinks.OpenWeb(room.Link));
                SectionButton(buttons, "In the tab", "Show this tracker in the Cheese Tracker tab, with filters and sorting", () => _host.ShowCheeseTab(profile.Id));
            }
            SectionButton(buttons, "Change link…", "Link this multiworld to a different Cheese Tracker page", () => ShowCheeseLinkDialog(profile));
            if (full)
                SectionButton(buttons, "Unlink…", "Stop using Cheese Tracker for this multiworld", () =>
                    ConfirmCheese($"Unlink {profile.Name} from Cheese Tracker? Nothing changes on Cheese Tracker; Atlas just stops reading it (and stops automatic updates).", "Unlink",
                        () =>
                        {
                            _host.Cheese.Unlink(profile.Id);
                            QueueRefresh();
                        }));
        }

        private void CheeseProblem(MultiworldProfile profile, CheeseRoomView room)
        {
            AddText(Colored(room.Problem, Warn));
            var buttons = SectionButtons();
            SectionButton(buttons, "Try now", "Ask Cheese Tracker again now", () => RunCheese(_host.Cheese.RefreshAsync(profile.Id, tryNow: true)), !room.Busy);
        }

        private static string OwnerText(CtGame row, CheeseOwnership owner)
        {
            string away = row.OwnerAway ? Colored(" · away", Warn) : "";
            string who = owner switch
            {
                CheeseOwnership.You => Colored("You (" + row.OwnerName + ")", Good) + away,
                CheeseOwnership.YouByName => Colored(row.OwnerName + " (claimed without signing in; Claim makes it your account's)", Colors.LightGray),
                CheeseOwnership.SomeoneElse => Colored(string.IsNullOrEmpty(row.OwnerName) ? "Someone" : row.OwnerName, Colors.White) + away,
                _ => Colored("Nobody", Muted)
            };
            return who + Colored($"  ({CtStatus.Label(row.Availability)})", Muted);
        }

        /// <summary>Time since the slot's last check (or "still BK"), colored by the tracker's inactivity thresholds.</summary>
        private static string ActivityText(CtGame g, CtTracker t)
        {
            var activity = g.LastActivityUtc;
            var stillBk = g.LastCheckedUtc;
            var latest = stillBk != null && (activity == null || stillBk > activity) ? stillBk : activity;
            if (latest == null) return Colored("no checks yet", Muted);
            double hours = (DateTime.UtcNow - latest.Value).TotalHours; // wall clock: how long ago a time the site sent was
            var color = hours < t.YellowHours ? Good : hours < t.RedHours ? Colors.Gold : Bad;
            return Colored(CtTime.Ago(latest) + (latest == stillBk && stillBk != activity ? " (still BK)" : ""), color);
        }

        private void AddCheesePingMenu(HFlowContainer flow, string profileId, string slotName, string current, bool enabled)
        {
            var menu = new MenuButton { Text = "Ping ▾", TooltipText = "When others may ping you on Discord about this slot", Flat = false, Disabled = !enabled, FocusMode = FocusModeEnum.None };
            menu.SetMeta("font_size_ratio", 0.9);
            var popup = menu.GetPopup();
            for (int i = 0; i < CtStatus.PingIds.Length; i++)
            {
                popup.AddRadioCheckItem(CtStatus.Label(CtStatus.PingIds[i]), i);
                popup.SetItemChecked(popup.GetItemIndex(i), CtStatus.PingIds[i] == current);
            }
            popup.IdPressed += id => RunCheese(_host.Cheese.SetPingAsync(profileId, slotName, CtStatus.PingIds[(int)id]));
            flow.AddChild(menu);
        }

        private void AddCheeseCompletionMenu(HFlowContainer flow, string profileId, string slotName, CtGame row, bool enabled)
        {
            var menu = new MenuButton { Text = "Completion ▾", TooltipText = "Cheese Tracker sets Goal and All checks itself; Done and Forfeit are yours to set", Flat = false, Disabled = !enabled, FocusMode = FocusModeEnum.None };
            menu.SetMeta("font_size_ratio", 0.9);
            var popup = menu.GetPopup();
            const int DoneId = 0, ForfeitId = 1, IncompleteId = 2;
            if (row.Completion != "done") popup.AddItem("Done (nothing more I can get)…", DoneId);
            if (row.Completion != "released") popup.AddItem("Forfeit…", ForfeitId);
            if (row.Completion is "done" or "released") popup.AddItem("Not done after all", IncompleteId);
            var cheese = _host.Cheese;
            popup.IdPressed += id =>
            {
                switch ((int)id)
                {
                    case DoneId:
                        CheeseDialogs.ConfirmDone(this, slotName, () => RunCheese(cheese.SetCompletionAsync(profileId, slotName, "done")));
                        break;
                    case ForfeitId:
                        CheeseDialogs.ConfirmForfeit(this, slotName, () => RunCheese(cheese.SetCompletionAsync(profileId, slotName, "released")));
                        break;
                    case IncompleteId:
                        RunCheese(cheese.SetCompletionAsync(profileId, slotName, "incomplete"));
                        break;
                }
            };
            flow.AddChild(menu);
        }
    }
}
