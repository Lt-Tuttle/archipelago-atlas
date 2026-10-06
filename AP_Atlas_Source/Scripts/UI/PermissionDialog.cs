using System;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Asks before Atlas looks or writes outside its own folder, or contacts a site on its own: what exactly will happen,
    /// then Allow once / Always allow / Don't allow. An "Always allow" given before answers at once without asking.
    /// </summary>
    public static class PermissionDialog
    {
        /// <param name="scope">What it applies to, e.g. an install folder (null: everywhere).</param>
        /// <param name="details">Specifics for this time (the folder, the file, the site), shown under the explanation.</param>
        /// <param name="decided">Called once with true (allowed) or false (not allowed or closed).</param>
        public static void Ask(Node parent, AppSettings settings, Permissions.Kind kind, string? scope, string? details, Action<bool> decided)
        {
            if (Permissions.IsAllowed(settings, kind, scope))
            {
                decided(true);
                return;
            }
            bool answered = false;
            void Answer(bool allowed)
            {
                if (answered) return;
                answered = true;
                decided(allowed);
            }

            var dialog = new ConfirmationDialog { Title = kind.Title, OkButtonText = "Allow once", CancelButtonText = "Don't allow", DialogHideOnOk = false };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            box.AddChild(new Label { Text = kind.Explanation, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(500, 0) });
            if (!string.IsNullOrWhiteSpace(details))
            {
                var detail = new Label { Text = details, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(500, 0) };
                detail.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
                box.AddChild(detail);
            }
            var where = new Label { Text = "\"Allow once\" lasts until Atlas closes. You can take back \"Always allow\" in Settings → Privacy & permissions.", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(500, 0) };
            where.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            box.AddChild(where);
            dialog.AddChild(box);
            dialog.AddButton("Always allow", true, "always");
            dialog.CustomAction += action =>
            {
                if (action.ToString() != "always") return;
                Permissions.SetAlways(settings, kind, scope, true);
                dialog.QueueFree();
                Answer(true);
            };
            dialog.Confirmed += () =>
            {
                Permissions.AllowForSession(kind, scope);
                dialog.QueueFree();
                Answer(true);
            };
            dialog.Canceled += () =>
            {
                Permissions.DenyForSession(kind, scope);
                dialog.QueueFree();
                Answer(false);
            };
            parent.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(560, 0));
        }
    }
}
