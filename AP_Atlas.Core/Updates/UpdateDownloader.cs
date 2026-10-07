using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core.Updates;

/// <summary>
/// Downloads a release's two files through <see cref="PoliteHttp"/>, the checksum file first: the zip is kept only when
/// it is exactly the file the release's SHA256SUMS.txt names (the polite client checks the hash as it downloads and
/// keeps nothing otherwise). At most one gigabyte, within half an hour.
/// </summary>
public static class UpdateDownloader
{
    public const long MaxZipBytes = 1024L * 1024 * 1024;
    public const string SiteName = "GitHub";
    private static readonly TimeSpan ZipTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SumsTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Downloads the release into <paramref name="downloadDir"/> (emptied first). The zip's path, or what went wrong.
    /// <paramref name="progress"/> gets (bytes so far, total or -1) for the zip.
    /// </summary>
    public static async Task<(string? ZipPath, string? Error)> DownloadAsync(ReleaseInfo release, string downloadDir, Action<long, long>? progress, CancellationToken ct)
    {
        if (!release.Installable) return (null, "This release has no download for Windows.");
        if (release.ZipSize > MaxZipBytes) return (null, $"The download is {release.ZipSize / (1024 * 1024)} MB, more than Atlas accepts.");
        try
        {
            if (Directory.Exists(downloadDir)) Directory.Delete(downloadDir, recursive: true);
            Directory.CreateDirectory(downloadDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "The downloads folder couldn't be prepared: " + ex.Message);
        }
        string sumsPath = Path.Combine(downloadDir, ReleaseInfo.SumsName);
        var sums = await PoliteHttp.DownloadAsync(release.SumsUrl!, SiteName, sumsPath, null, 64 * 1024, SumsTimeout, null, ct).ConfigureAwait(false);
        if (!sums.Ok) return (null, sums.Message ?? "The release's checksum file couldn't be downloaded.");
        string? expected = ExpectedHash(await File.ReadAllTextAsync(sumsPath, ct).ConfigureAwait(false), release.ZipName!);
        if (expected == null) return (null, $"The release's {ReleaseInfo.SumsName} doesn't name {release.ZipName}.");
        string zipPath = Path.Combine(downloadDir, release.ZipName!);
        var zip = await PoliteHttp.DownloadAsync(release.ZipUrl!, SiteName, zipPath, expected, MaxZipBytes, ZipTimeout, progress, ct).ConfigureAwait(false);
        if (!zip.Ok) return (null, zip.Message ?? "The download failed.");
        return (zipPath, null);
    }

    /// <summary>The SHA-256 a checksum file gives for a file (sha256sum's lines: the hash, two spaces, the name), or null.</summary>
    public static string? ExpectedHash(string? sums, string fileName)
    {
        foreach (string raw in (sums ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length < 66) continue;
            string hash = line.Substring(0, 64);
            if (line[64] != ' ' || !IsHex(hash)) continue;
            string name = line.Substring(65).TrimStart(' ', '*').Trim();
            if (name == fileName) return hash.ToLowerInvariant();
        }
        return null;
    }

    private static bool IsHex(string text)
    {
        foreach (char c in text)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
