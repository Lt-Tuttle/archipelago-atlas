#nullable enable
using System;
using System.Threading.Tasks;
using Godot;
using AP_Atlas.Core;
using AP_Atlas.Core.Spheres;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Linking a multiworld to the host's spheretracker.de room, from wherever the link is typed (the Multiworlds page's
    /// box, the Sphere Tracker tab): the room is read once and linked at once when its creator runs the multiworld's
    /// Cheese Tracker; otherwise the user confirms the creator is the host (the no-cheating rule: only the host's room).
    /// </summary>
    public static class SphereLinkFlow
    {
        /// <param name="toast">Shows a message to the user (the error, or what was linked).</param>
        /// <param name="onLinked">Runs once the room is linked (to refresh what shows it).</param>
        /// <returns>The error when the room can't be used, or null when it was linked or the user is being asked.</returns>
        public static async Task<string?> CheckAndLinkAsync(Node parent, SphereService spheres, MultiworldProfile profile, string text, Action<string, Color> toast, Action onLinked)
        {
            SphereRoomCheck check;
            try
            {
                check = await spheres.CheckRoomAsync(profile.Id, text);
            }
            catch (Exception ex)
            {
                string failed = "Checking the room failed: " + ex.Message;
                if (GodotObject.IsInstanceValid(parent)) toast(failed, ThemeColors.Error);
                return failed;
            }
            if (!GodotObject.IsInstanceValid(parent)) return "Atlas closed the page.";
            if (check.Error != null)
            {
                toast(check.Error, ThemeColors.Error);
                return check.Error;
            }
            if (check.ByOrganizer)
            {
                return Finish(spheres, profile, check, confirmed: false, toast, onLinked);
            }
            string room = string.IsNullOrWhiteSpace(check.Data?.RoomName) ? "This room" : $"The room \"{check.Data!.RoomName}\"";
            string question, requirement;
            if (check.Creator.Length == 0)
            {
                question = $"{room} doesn't say who created it. Only link it if {profile.Name}'s host created it and shared the link.";
                requirement = $"{profile.Name}'s host created this room";
            }
            else if (check.Organizer != null)
            {
                question = $"{room} was created by {check.Creator}, but Cheese Tracker says {profile.Name} is organized by {check.Organizer}. Only link it if {check.Creator} is the host.";
                requirement = $"{check.Creator} is {profile.Name}'s host";
            }
            else
            {
                question = $"{room} was created by {check.Creator}. Atlas can't tell who hosts {profile.Name} (once it's linked to Cheese Tracker, Atlas checks this itself), so only link it if {check.Creator} is the host.";
                requirement = $"{check.Creator} is {profile.Name}'s host";
            }
            Dialogs.Confirm(parent, "Is this the host's room?", question, "Link", () => Finish(spheres, profile, check, confirmed: true, toast, onLinked), requirement);
            return null;
        }

        private static string? Finish(SphereService spheres, MultiworldProfile profile, SphereRoomCheck check, bool confirmed, Action<string, Color> toast, Action onLinked)
        {
            string? error = spheres.LinkRoom(check, confirmed);
            if (error != null)
            {
                toast(error, ThemeColors.Error);
                return error;
            }
            toast(check.ByOrganizer ? $"Linked: the room was created by {check.Creator}, who runs {profile.Name}'s Cheese Tracker." : $"Linked {profile.Name}'s sphere tracker.", ThemeColors.TextSubtle);
            onLinked();
            return null;
        }
    }
}
