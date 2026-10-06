#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Things Atlas asks before doing: looking or writing outside its own folder, and contacting a site on its own. The
    /// user answers Allow once, Always allow or Don't allow when it comes up; only "Always allow" is kept (in settings.json)
    /// and every kept answer is listed in Privacy &amp; permissions, where it can be taken back. "Don't allow" lasts for the
    /// session, so automatic checks don't ask again until Atlas restarts; a button the user presses always asks.
    /// </summary>
    public static class Permissions
    {
        public sealed class Kind
        {
            public string Id { get; }
            /// <summary>What Atlas wants to do, as a short title ("Look for Archipelago on this PC").</summary>
            public string Title { get; }
            /// <summary>Exactly what happens, in plain words, shown when asking.</summary>
            public string Explanation { get; }

            internal Kind(string id, string title, string explanation)
            {
                Id = id;
                Title = title;
                Explanation = explanation;
            }
        }

        public static readonly Kind FindArchipelago = new Kind("pc.find-archipelago", "Look for Archipelago on this PC",
            "Atlas checks the usual places Archipelago is installed (Program Files, ProgramData, your local Programs folder, and " +
            "C:\\, D:\\ or E:\\Archipelago) and Windows' list of installed programs. It only looks for ArchipelagoLauncher.exe; " +
            "nothing is opened or changed.");

        public static readonly Kind WriteArchipelago = new Kind("pc.write-archipelago", "Let Atlas add its files to your Archipelago install",
            "To run logic on your own Archipelago, Atlas adds its bridge (UltimateBridge.apworld) to the install's worlds folder and " +
            "updates it when Atlas updates. It also adds the Universal Tracker or an apworld there, but only when you ask it to. " +
            "Atlas records every change, and \"Remove Atlas's files\" in Atlas Engine undoes them.");

        public static readonly Kind GitHubLookups = new Kind("online.github-lookups", "Look up apworld versions on GitHub by itself",
            "When a seed was made with a different version of a game's apworld, Atlas reads the release lists of that game's GitHub " +
            "projects (and may search GitHub for where your copy came from) to find the seed's version, without you pressing a button. " +
            "Downloads still need you to trust their source first.");

        public static IReadOnlyList<Kind> All { get; } = new[] { FindArchipelago, WriteArchipelago, GitHubLookups };

        /// <summary>Every place Atlas reaches online, in plain words: the privacy statement (Settings → Privacy &amp; permissions, About). A new site joins it.</summary>
        public const string WhereAtlasGoesOnline =
            "The archipelago.gg rooms you connect to. Cheese Tracker (your instance) and spheretracker.de (the host's room) for the multiworlds you link. " +
            "GitHub for apworld releases, when you allow it. python.org, pypa.io and PyPI when you set up the Atlas Engine. Nothing else, and nothing at startup.";

        private static readonly HashSet<string> _deniedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _allowedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string Key(Kind kind, string scope) =>
            string.IsNullOrWhiteSpace(scope) ? kind.Id : kind.Id + "|" + scope.Trim().TrimEnd('\\', '/');

        /// <summary>Whether the user chose "Always allow" for this (for a scope like an install folder, for that one only).</summary>
        public static bool IsAlwaysAllowed(AppSettings settings, Kind kind, string scope = null)
        {
            if (settings?.PermissionsAllowed == null) return false;
            string key = Key(kind, scope);
            return settings.PermissionsAllowed.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Allowed now: "Always allow", or "Allow once" earlier in this session (it lasts until Atlas closes).</summary>
        public static bool IsAllowed(AppSettings settings, Kind kind, string scope = null)
        {
            if (IsAlwaysAllowed(settings, kind, scope)) return true;
            lock (_deniedThisSession) return _allowedThisSession.Contains(Key(kind, scope));
        }

        /// <summary>"Allow once": allowed until Atlas closes, never saved.</summary>
        public static void AllowForSession(Kind kind, string scope = null)
        {
            lock (_deniedThisSession)
            {
                _allowedThisSession.Add(Key(kind, scope));
                _deniedThisSession.Remove(Key(kind, scope));
            }
        }

        /// <summary>Keeps (or, with false, takes back) "Always allow" and saves settings.</summary>
        public static void SetAlways(AppSettings settings, Kind kind, string scope, bool always)
        {
            if (settings == null) return;
            settings.PermissionsAllowed ??= new List<string>();
            string key = Key(kind, scope);
            settings.PermissionsAllowed.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (always)
            {
                settings.PermissionsAllowed.Add(key);
                lock (_deniedThisSession) _deniedThisSession.Remove(key);
            }
            else lock (_deniedThisSession) _allowedThisSession.Remove(key);
            DataManager.SaveSettings(settings);
            Logger.LogInfo($"Permission {(always ? "kept" : "taken back")}: {kind.Title}{(string.IsNullOrWhiteSpace(scope) ? "" : " (" + scope + ")")}.");
        }

        /// <summary>Remembers "Don't allow" until Atlas restarts, so automatic checks don't keep asking.</summary>
        public static void DenyForSession(Kind kind, string scope = null)
        {
            lock (_deniedThisSession)
            {
                _deniedThisSession.Add(Key(kind, scope));
                _allowedThisSession.Remove(Key(kind, scope));
            }
        }

        public static bool DeniedThisSession(Kind kind, string scope = null)
        {
            lock (_deniedThisSession) return _deniedThisSession.Contains(Key(kind, scope));
        }

        /// <summary>Every kept "Always allow": the kind and its scope (null when it applies everywhere).</summary>
        public static List<(Kind Kind, string Scope)> Granted(AppSettings settings)
        {
            var result = new List<(Kind, string)>();
            if (settings?.PermissionsAllowed == null) return result;
            foreach (var key in settings.PermissionsAllowed)
            {
                int bar = key.IndexOf('|');
                string id = bar < 0 ? key : key.Substring(0, bar);
                var kind = All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));
                if (kind != null) result.Add((kind, bar < 0 ? null : key.Substring(bar + 1)));
            }
            return result;
        }

        /// <summary>The apworld sources the user trusts downloads from, as kept in the settings.</summary>
        public static IReadOnlyList<string> TrustedSources(AppSettings settings) =>
            settings?.ApprovedApworldSources?.ToList() ?? new List<string>();

        /// <summary>Takes back the user's trust in a source: Atlas asks before downloading from it again.</summary>
        public static void StopTrusting(AppSettings settings, string source)
        {
            if (settings?.ApprovedApworldSources == null) return;
            settings.ApprovedApworldSources.RemoveAll(x => string.Equals(x, source, StringComparison.OrdinalIgnoreCase));
            DataManager.SaveSettings(settings);
            Logger.LogInfo($"No longer trusting {source} for apworld downloads.");
        }

        internal static void ResetSessionForTests()
        {
            lock (_deniedThisSession)
            {
                _deniedThisSession.Clear();
                _allowedThisSession.Clear();
            }
        }
    }
}
