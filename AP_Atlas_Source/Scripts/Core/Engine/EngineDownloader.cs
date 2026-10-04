using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>Downloads for the engine setup: progress, hash check, a few retries with backoff, atomic finish.</summary>
    public static class EngineDownloader
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AP-Atlas-EngineSetup/1.0");
            return client;
        }

        /// <summary>
        /// Downloads url to destPath. When expectedSha256 is given the file must match it or nothing is kept.
        /// progress gets (bytes so far, total or -1). Returns the file's SHA-256.
        /// </summary>
        public static async Task<string> DownloadAsync(string url, string destPath, string expectedSha256, Action<long, long> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            string part = destPath + ".part";
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 2 : 6), ct);
                try
                {
                    using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if ((int)response.StatusCode == 404) throw new FileNotFoundException($"Not found: {url}");
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? -1;
                    using (var source = await response.Content.ReadAsStreamAsync(ct))
                    using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        int read;
                        var lastReport = DateTime.MinValue;
                        while ((read = await source.ReadAsync(buffer, ct)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), ct);
                            done += read;
                            if ((DateTime.Now - lastReport).TotalMilliseconds > 150)
                            {
                                lastReport = DateTime.Now;
                                progress?.Invoke(done, total);
                            }
                        }
                        progress?.Invoke(done, total);
                    }
                    string hash = Sha256Of(part);
                    if (!string.IsNullOrEmpty(expectedSha256) && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(part);
                        // Not a network blip: retrying won't change what the server sends.
                        throw new InvalidDataException($"The download from {new Uri(url).Host} isn't the expected file (SHA-256 {hash[..12]}…, expected {expectedSha256[..12]}…). Nothing was installed.");
                    }
                    File.Move(part, destPath, true);
                    return hash;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException && ex is not FileNotFoundException || ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    last = ex;
                    TryDelete(part);
                }
            }
            throw new IOException($"Couldn't download {url}: {last?.Message}", last);
        }

        public static async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }

        public static string Sha256Of(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
