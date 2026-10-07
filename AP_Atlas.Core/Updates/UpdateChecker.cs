using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core.Updates;

/// <summary>
/// Asks GitHub for Atlas's releases (one small request, through <see cref="GitHubApi"/>: cached with its ETag, so an
/// unchanged list costs nothing against the limit) and says whether a newer version of the user's channel exists. Nothing
/// is downloaded here.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "Lt-Tuttle/archipelago-atlas";
    public const string ReleasesPath = "/repos/" + Repository + "/releases?per_page=20";
    /// <summary>The page a user can open to download a release by hand.</summary>
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    public sealed record Outcome(ReleaseInfo? Newer, ReleaseInfo? Newest, string? Error)
    {
        public bool Ok => Error == null;
    }

    /// <summary>The newest release of the channel, and whether it's newer than <paramref name="current"/>.</summary>
    public static async Task<Outcome> CheckAsync(SemVer current, string channel, CancellationToken ct = default)
    {
        GitHubApi.Result result;
        try
        {
            result = await GitHubApi.GetAsync(ReleasesPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            return new Outcome(null, null, "GitHub couldn't be reached: " + ex.Message);
        }
        if (!result.Ok) return new Outcome(null, null, result.Message ?? "GitHub didn't answer.");
        IReadOnlyList<ReleaseInfo> releases = ReleaseInfo.Parse(result.Json);
        var newest = ReleaseInfo.Newest(releases, channel);
        return new Outcome(newest != null && newest.Version > current ? newest : null, newest, null);
    }
}
