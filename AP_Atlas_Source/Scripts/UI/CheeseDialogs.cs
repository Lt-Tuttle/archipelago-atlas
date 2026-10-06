#nullable disable
using System;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.CheeseTracker;
using Godot;
using Color = Godot.Color;
using Logger = AP_Atlas.Core.Logger;

namespace AP_Atlas.UI
{
    /// <summary>The small dialogs behind Cheese Tracker actions, shared by Properties and the Cheese Tracker tab.</summary>
    public static class CheeseDialogs
    {
        private static readonly Color Bad = ThemeColors.Error;

        /// <summary>Runs a change; a failure shows as a toast (and so does <paramref name="success"/>, if given).</summary>
        public static void Run(Node owner, Task<string> change, Action<string, Color> toast, string success = null, Action done = null) => AP_Atlas.Core.Async.Fire(RunAsync(owner, change, toast, success, done), "updating Cheese Tracker");

        private static async Task RunAsync(Node owner, Task<string> change, Action<string, Color> toast, string success = null, Action done = null)
        {
            string error;
            try
            {
                error = await change;
            }
            catch (Exception ex)
            {
                error = "That didn't work: " + ex.Message;
                Logger.LogWarning("Cheese Tracker change failed: " + ex);
            }
            if (!GodotObject.IsInstanceValid(owner)) return;
            if (error != null) toast(error, Bad);
            else if (success != null) toast(success, ThemeColors.TextSubtle);
            done?.Invoke();
        }

        public static void Confirm(Node parent, string text, string okText, Action onConfirm) =>
            Dialogs.Confirm(parent, "Cheese Tracker", text, okText, onConfirm);

        /// <summary>Asks for a multiworld's Cheese Tracker link (or its archipelago.gg room link) and links it.</summary>
        public static void Link(Node parent, CheeseTrackerService cheese, MultiworldProfile profile, Action<string, Color> toast, Action done)
        {
            var dialog = new ConfirmationDialog { Title = "Link to Cheese Tracker", OkButtonText = "Link" };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 6);
            box.AddChild(new Label
            {
                Text = $"Paste {profile.Name}'s Cheese Tracker link. An archipelago.gg room or tracker link works too: Atlas asks Cheese Tracker for that room's tracker (Cheese Tracker starts one if nobody has yet).",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(460, 0)
            });
            var input = new LineEdit { Text = profile.CheeseTrackerUrl ?? "", PlaceholderText = CheeseClient.DefaultInstance + "/tracker/…", SelectAllOnFocus = true };
            box.AddChild(input);
            dialog.AddChild(box);
            dialog.RegisterTextEnter(input);
            dialog.Confirmed += () => Async.Fire(async () =>
            {
                string text = input.Text;
                dialog.QueueFree();
                string error = await cheese.LinkAsync(profile.Id, text);
                if (!GodotObject.IsInstanceValid(parent)) return;
                toast(error ?? (string.IsNullOrWhiteSpace(text) ? $"Unlinked {profile.Name}" : $"Linked {profile.Name} to Cheese Tracker"), error == null ? ThemeColors.TextSubtle : Bad);
                done?.Invoke();
            }, "linking Cheese Tracker");
            dialog.Canceled += () => dialog.QueueFree();
            parent.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(520, 180));
            input.GrabFocus();
        }

        public static void Notes(Node parent, string slotName, string notes, Action<string> onSave)
        {
            var dialog = new ConfirmationDialog { Title = $"Notes for {slotName}", OkButtonText = "Save" };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 6);
            box.AddChild(new Label
            {
                Text = "Everyone in the room sees these on Cheese Tracker (what you're waiting for, when you'll be back…).",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(460, 0)
            });
            var edit = new TextEdit { Text = notes ?? "", WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(460, 180) };
            box.AddChild(edit);
            dialog.AddChild(box);
            dialog.Confirmed += () =>
            {
                string text = edit.Text;
                dialog.QueueFree();
                onSave(text);
            };
            dialog.Canceled += () => dialog.QueueFree();
            parent.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(500, 300));
            edit.GrabFocus();
        }

        /// <summary>Looks for the multiworld on the user's Cheese Tracker dashboard and offers to link what it finds.</summary>
        public static void FindOnDashboard(Node parent, CheeseTrackerService cheese, MultiworldProfile profile, Action<string, Color> toast, Action done) => AP_Atlas.Core.Async.Fire(FindOnDashboardAndOfferAsync(parent, cheese, profile, toast, done), "looking on your Cheese Tracker dashboard");

        private static async Task FindOnDashboardAndOfferAsync(Node parent, CheeseTrackerService cheese, MultiworldProfile profile, Action<string, Color> toast, Action done)
        {
            toast("Looking on your Cheese Tracker dashboard…", ThemeColors.TextSubtle);
            var (link, title, error) = await cheese.FindOnDashboardAsync(profile.Id);
            if (!GodotObject.IsInstanceValid(parent)) return;
            if (link == null)
            {
                toast(error, Bad);
                return;
            }
            Confirm(parent, $"Found “{(string.IsNullOrWhiteSpace(title) ? link : title)}” on your dashboard. Link {profile.Name} to it?", "Link",
                () => Run(parent, cheese.LinkAsync(profile.Id, link), toast, $"Linked {profile.Name} to Cheese Tracker", done));
        }

        public static void ConfirmDone(Node parent, string name, Action onConfirm) =>
            Confirm(parent, $"Mark {name} as Done on Cheese Tracker? Use it when there's nothing more you can get (for example with minimal accessibility). Goal and All checks are set by Cheese Tracker itself.",
                "Mark Done", onConfirm);

        public static void ConfirmForfeit(Node parent, string name, Action onConfirm) =>
            Confirm(parent, $"Mark {name} as Forfeit on Cheese Tracker? It tells the room you've abandoned the slot. It doesn't release anything in Archipelago; talk to the organizer first.",
                "Mark Forfeit", onConfirm);

        public static void ConfirmDisclaim(Node parent, string name, Action onConfirm) =>
            Confirm(parent, $"Release your claim on {name}? It will show as open for someone else to take.", "Disclaim", onConfirm);
    }
}
