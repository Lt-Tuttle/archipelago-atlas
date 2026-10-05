using System;
using System.IO;
using Godot;

namespace AP_Atlas.Core
{
    /// <summary>
    /// The only way Atlas opens anything outside itself: web pages in the browser, and folders in Explorer.
    /// Links often come from other people (map packs, Cheese Tracker, servers), so a link is opened only when it's a plain
    /// https web page (http only for this PC); anything else (files, programs, network shares, other schemes) is refused
    /// and logged. Atlas never asks Windows to open a file, because that could run it.
    /// </summary>
    public static class ExternalLinks
    {
        private const int MaxLength = 2048;

        /// <summary>Why a link won't be opened, or null if it's a web page Atlas may open. <paramref name="safe"/> is its cleaned-up form.</summary>
        public static string CheckWeb(string url, out string safe)
        {
            safe = null;
            url = (url ?? "").Trim();
            if (url.Length == 0) return "there's no link";
            if (url.Length > MaxLength) return "the link is too long";
            foreach (char c in url)
                if (char.IsControl(c)) return "the link contains control characters";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "it isn't a web address";
            if (uri.IsUnc || uri.IsFile) return "it points to a file or network share, not a web page";
            bool https = uri.Scheme == Uri.UriSchemeHttps;
            bool localHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
            if (!https && !localHttp) return $"only https web pages are opened (this is {uri.Scheme}:)";
            if (string.IsNullOrEmpty(uri.Host)) return "it has no web site";
            if (!string.IsNullOrEmpty(uri.UserInfo)) return "it contains a user name or password";
            if (uri.HostNameType != UriHostNameType.Dns && uri.HostNameType != UriHostNameType.IPv4 && uri.HostNameType != UriHostNameType.IPv6)
                return "it doesn't name a web site";
            safe = uri.AbsoluteUri;
            return null;
        }

        /// <summary>Opens a web page in the browser if it's safe to; otherwise logs why not. Returns whether it was opened.</summary>
        public static bool OpenWeb(string url)
        {
            string problem = CheckWeb(url, out string safe);
            if (problem != null)
            {
                Logger.LogWarning($"Didn't open a link ({problem}): {Shorten(url)}");
                return false;
            }
            var error = OS.ShellOpen(safe);
            if (error != Error.Ok)
            {
                Logger.LogWarning($"Windows couldn't open {Shorten(safe)} ({error}).");
                return false;
            }
            return true;
        }

        /// <summary>Why a folder won't be opened, or null if it's an existing local folder. <paramref name="full"/> is its full path.</summary>
        public static string CheckFolder(string path, out string full)
        {
            full = null;
            if (string.IsNullOrWhiteSpace(path)) return "there's no folder";
            string trimmed = path.Trim();
            if (trimmed.StartsWith(@"\\") || trimmed.StartsWith("//")) return "network folders aren't opened";
            if (!Path.IsPathFullyQualified(trimmed)) return "it isn't a full folder path";
            try { full = Path.GetFullPath(trimmed); }
            catch (Exception ex) { return "it isn't a valid path (" + ex.Message + ")"; }
            if (full.StartsWith(@"\\")) return "network folders aren't opened";
            // Only an existing directory: asking Windows to open a file would run it.
            if (!Directory.Exists(full)) return "the folder doesn't exist";
            return null;
        }

        /// <summary>Shows a local folder in Explorer. Files are never opened (that could run them). Returns whether it was opened.</summary>
        public static bool OpenFolder(string path)
        {
            string problem = CheckFolder(path, out string full);
            if (problem != null)
            {
                Logger.LogWarning($"Didn't open a folder ({problem}): {Shorten(path)}");
                return false;
            }
            var error = OS.ShellOpen(full);
            if (error != Error.Ok)
            {
                Logger.LogWarning($"Windows couldn't open the folder {full} ({error}).");
                return false;
            }
            return true;
        }

        private static string Shorten(string s)
        {
            s ??= "";
            return s.Length <= 200 ? s : s.Substring(0, 200) + "…";
        }
    }
}
