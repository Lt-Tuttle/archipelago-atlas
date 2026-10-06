using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>
    /// Downloads for the engine setup, through <see cref="PoliteHttp"/> like every other request: progress, a hash check,
    /// size and time limits, and an atomic finish. A failure is reported plainly instead of being retried at once; a site
    /// that failed is left alone for a while, and the message says when Atlas tries again.
    /// </summary>
    public static class EngineDownloader
    {
        /// <summary>Largest file the engine setup downloads (Archipelago's source archive is the biggest, well under this).</summary>
        private const long MaxDownloadBytes = 512L * 1024 * 1024;
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Downloads url to destPath. When expectedSha256 is given the file must match it or nothing is kept.
        /// progress gets (bytes so far, total or -1). Returns the file's SHA-256.
        /// </summary>
        public static async Task<string> DownloadAsync(string url, string destPath, string expectedSha256, Action<long, long> progress, CancellationToken ct)
        {
            string host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
            var r = await PoliteHttp.DownloadAsync(url, host, destPath, expectedSha256, MaxDownloadBytes, DownloadTimeout, progress, ct).ConfigureAwait(false);
            if (r.Ok) return r.Text;
            if (r.Outcome == WebOutcome.NotFound) throw new FileNotFoundException($"Not found: {url}");
            // A file that isn't the expected one isn't a network blip: say so plainly.
            if (r.Outcome == WebOutcome.BadResponse && r.Message != null && r.Message.Contains("isn't the expected file")) throw new InvalidDataException(r.Message);
            throw new IOException($"Couldn't download {url}: {r.Message}");
        }

        /// <summary>Reads a small text file (up to 8 MB) from an https URL.</summary>
        public static async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            string host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
            var r = await PoliteHttp.GetAsync(url, host, accept: "application/json, text/plain, */*", maxBytes: 8 * 1024 * 1024, ct: ct).ConfigureAwait(false);
            if (!r.Ok) throw new IOException(r.Message ?? $"Couldn't read {url}.");
            return r.Text;
        }

        public static string Sha256Of(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { } // best effort: a file held now (an antivirus scan, say) is replaced by the next download
        }
    }
}
