using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AP_Atlas.Core.Testing;
using AP_Atlas.Core.Updates;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v0.1.0-beta.1", 0, 1, 0, "beta.1")]
    [InlineData("0.1.0-beta.1+4afefca", 0, 1, 0, "beta.1")]
    [InlineData("10.0.0-rc.1.x-y", 10, 0, 0, "rc.1.x-y")]
    public void Versions_parse(string text, int major, int minor, int patch, string pre)
    {
        Assert.True(SemVer.TryParse(text, out var version));
        Assert.Equal((major, minor, patch), (version.Major, version.Minor, version.Patch));
        Assert.Equal(pre, string.Join('.', version.PreRelease));
        Assert.Equal(pre.Length > 0, version.IsPreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta.01")]
    [InlineData("1.0.0-be ta")]
    [InlineData("a.b.c")]
    [InlineData("1.0.0.0")]
    public void Other_text_is_refused(string text) => Assert.False(SemVer.TryParse(text, out _));

    [Fact]
    public void Precedence_follows_the_specification()
    {
        string[] ordered = { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0-beta.1", "2.0.0" };
        for (int i = 1; i < ordered.Length; i++)
            Assert.True(SemVer.Parse(ordered[i - 1]) < SemVer.Parse(ordered[i]), $"{ordered[i - 1]} should come before {ordered[i]}");
        Assert.Equal(SemVer.Parse("1.0.0+a"), SemVer.Parse("1.0.0+b"));
        Assert.Equal("0.1.0-beta.1", SemVer.Parse("v0.1.0-beta.1+sha").ToString());
    }
}

public class ReleaseInfoTests
{
    private static string Releases(params JObject[] items) => new JArray(items).ToString();

    private static JObject Release(string tag, bool prerelease, bool draft = false, bool withAssets = true, string body = "notes") =>
        new()
        {
            ["tag_name"] = tag,
            ["prerelease"] = prerelease,
            ["draft"] = draft,
            ["published_at"] = "2026-10-07T12:00:00Z",
            ["body"] = body,
            ["assets"] = withAssets
                ? new JArray(
                    new JObject { ["name"] = $"TheArchipelagoAtlas-{tag.TrimStart('v')}-win-x64.zip", ["browser_download_url"] = $"https://github.com/x/y/releases/download/{tag}/z.zip", ["size"] = 1234 },
                    new JObject { ["name"] = "SHA256SUMS.txt", ["browser_download_url"] = $"https://github.com/x/y/releases/download/{tag}/SHA256SUMS.txt", ["size"] = 100 })
                : new JArray()
        };

    [Fact]
    public void The_list_is_read_without_drafts_or_odd_tags()
    {
        var releases = ReleaseInfo.Parse(JToken.Parse(Releases(Release("v0.2.0", false), Release("v0.3.0", false, draft: true), Release("nightly", false), Release("v0.2.1-beta.1", true, withAssets: false))));
        Assert.Equal(new[] { "v0.2.0", "v0.2.1-beta.1" }, releases.Select(r => r.Tag));
        Assert.True(releases[0].Installable);
        Assert.False(releases[1].Installable);
        Assert.Equal("TheArchipelagoAtlas-0.2.0-win-x64.zip", releases[0].ZipName);
        Assert.Equal(1234, releases[0].ZipSize);
        Assert.Equal(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc), releases[0].PublishedUtc);
        Assert.Empty(ReleaseInfo.Parse(JToken.Parse("{\"message\":\"Not Found\"}")));
        Assert.Empty(ReleaseInfo.Parse(null));
    }

    [Fact]
    public void An_asset_from_elsewhere_is_not_taken()
    {
        var odd = Release("v0.2.0", false);
        odd["assets"]![0]!["browser_download_url"] = "http://example.com/evil.zip";
        var releases = ReleaseInfo.Parse(JToken.Parse(Releases(odd)));
        Assert.Null(releases[0].ZipUrl);
        Assert.False(releases[0].Installable);
    }

    [Fact]
    public void The_newest_of_a_channel_is_chosen()
    {
        var releases = ReleaseInfo.Parse(JToken.Parse(Releases(Release("v0.2.0", false), Release("v0.3.0-beta.1", true), Release("v0.1.0", false), Release("v0.4.0", false, withAssets: false))));
        Assert.Equal("v0.2.0", ReleaseInfo.Newest(releases, "stable")!.Tag);
        Assert.Equal("v0.3.0-beta.1", ReleaseInfo.Newest(releases, "beta")!.Tag);
        Assert.Null(ReleaseInfo.Newest(Array.Empty<ReleaseInfo>(), "beta"));
        Assert.Equal("beta", ReleaseInfo.ChannelFor("auto", SemVer.Parse("0.1.0-beta.1")));
        Assert.Equal("stable", ReleaseInfo.ChannelFor("auto", SemVer.Parse("0.1.0")));
        Assert.Equal("stable", ReleaseInfo.ChannelFor("stable", SemVer.Parse("0.1.0-beta.1")));
        Assert.Equal("beta", ReleaseInfo.ChannelFor("beta", SemVer.Parse("1.0.0")));
    }

    [Fact]
    public void The_checksum_file_is_read_in_sha256sum_format()
    {
        string hash = new string('a', 64);
        string sums = $"{hash}  TheArchipelagoAtlas-0.2.0-win-x64.zip\n{new string('b', 64)} *other.zip\nnot a line\n";
        Assert.Equal(hash, UpdateDownloader.ExpectedHash(sums, "TheArchipelagoAtlas-0.2.0-win-x64.zip"));
        Assert.Equal(new string('b', 64), UpdateDownloader.ExpectedHash(sums, "other.zip"));
        Assert.Null(UpdateDownloader.ExpectedHash(sums, "missing.zip"));
        Assert.Null(UpdateDownloader.ExpectedHash($"{new string('z', 64)}  x.zip", "x.zip"));
        Assert.Null(UpdateDownloader.ExpectedHash(null, "x.zip"));
    }
}

public class UpdateInstallerTests
{
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string path) => File.ReadAllText(path);

    /// <summary>A complete program folder with the given word in every file.</summary>
    private static void Program(string dir, string word)
    {
        Write(Path.Combine(dir, UpdateLayout.Exe), word + " exe");
        Write(Path.Combine(dir, UpdateLayout.Pck), word + " pck");
        Write(Path.Combine(dir, UpdateLayout.DataFolder, UpdateLayout.MainAssembly), word + " dll");
        Write(Path.Combine(dir, "README.md"), word + " readme");
    }

    [Fact]
    public void A_swap_moves_the_release_in_and_keeps_what_it_replaced()
    {
        using var temp = new TempFolder();
        string install = temp.File("install"), staged = temp.File("next/TheArchipelagoAtlas"), previous = temp.File("previous");
        Program(install, "old");
        Write(Path.Combine(install, UpdateLayout.PortableData, "settings.json"), "{}");
        Write(Path.Combine(install, "mine.txt"), "the user's own file");
        Program(staged, "new");
        var log = new List<string>();
        UpdateInstaller.Swap(install, staged, previous, "0.1.0", log.Add);
        Assert.Equal("new exe", Read(Path.Combine(install, UpdateLayout.Exe)));
        Assert.Equal("new dll", Read(Path.Combine(install, UpdateLayout.DataFolder, UpdateLayout.MainAssembly)));
        Assert.Equal("old exe", Read(Path.Combine(previous, UpdateLayout.Exe)));
        Assert.Equal("old readme", Read(Path.Combine(previous, "README.md")));
        Assert.Equal("0.1.0", UpdateInstaller.VersionIn(previous));
        Assert.Equal("{}", Read(Path.Combine(install, UpdateLayout.PortableData, "settings.json")));
        Assert.Equal("the user's own file", Read(Path.Combine(install, "mine.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staged));
        Assert.Contains(log, line => line.Contains("Swapped"));

        // Putting the previous version back keeps the one that failed.
        string failed = temp.File("failed");
        Assert.Equal("0.1.0", UpdateInstaller.RollBack(install, previous, failed, "0.2.0", log.Add));
        Assert.Equal("old exe", Read(Path.Combine(install, UpdateLayout.Exe)));
        Assert.Equal("new exe", Read(Path.Combine(failed, UpdateLayout.Exe)));
        Assert.Equal("0.2.0", UpdateInstaller.VersionIn(failed));
        Assert.Null(UpdateInstaller.VersionIn(previous));
        Assert.Equal("{}", Read(Path.Combine(install, UpdateLayout.PortableData, "settings.json")));
    }

    [Fact]
    public void An_incomplete_release_is_refused_before_anything_moves()
    {
        using var temp = new TempFolder();
        string install = temp.File("install"), staged = temp.File("next/TheArchipelagoAtlas"), previous = temp.File("previous");
        Program(install, "old");
        Program(staged, "new");
        File.Delete(Path.Combine(staged, UpdateLayout.Pck));
        var ex = Assert.Throws<InvalidOperationException>(() => UpdateInstaller.Swap(install, staged, previous, "0.1.0", _ => { }));
        Assert.Contains(UpdateLayout.Pck, ex.Message);
        Assert.Equal("old exe", Read(Path.Combine(install, UpdateLayout.Exe)));
        Assert.Throws<InvalidOperationException>(() => UpdateInstaller.RollBack(install, temp.File("nothing"), temp.File("failed"), "0.2.0", _ => { }));
    }

    [Fact]
    public void A_swap_that_fails_midway_is_undone()
    {
        using var temp = new TempFolder();
        string install = temp.File("install"), staged = temp.File("next/TheArchipelagoAtlas"), previous = temp.File("previous");
        Program(install, "old");
        Program(staged, "new");
        // A file in the install folder that can't be moved (held open without sharing) fails the swap partway through.
        using (new FileStream(Path.Combine(install, "README.md"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Swap(install, staged, previous, "0.1.0", _ => { }));
        }
        Assert.Equal("old exe", Read(Path.Combine(install, UpdateLayout.Exe)));
        Assert.Equal("old pck", Read(Path.Combine(install, UpdateLayout.Pck)));
        Assert.Equal("new exe", Read(Path.Combine(staged, UpdateLayout.Exe)));
        Assert.Null(UpdateLayout.Validate(install));
        Assert.Null(UpdateLayout.Validate(staged));
        Assert.False(Directory.Exists(Path.Combine(previous, UpdateLayout.DataFolder)), "a folder made for the swap was left behind");
    }

    [Fact]
    public void A_swap_moves_a_folder_whose_files_a_running_program_holds_open()
    {
        using var temp = new TempFolder();
        string install = temp.File("install"), staged = temp.File("next/TheArchipelagoAtlas"), previous = temp.File("previous"), failed = temp.File("failed");
        Program(install, "old");
        Program(staged, "new");
        string dataFolder = Path.Combine(install, UpdateLayout.DataFolder);
        // A running program's files are open with delete sharing: each can be moved, but Windows won't rename a folder holding one
        // (the exported build's self-check found this: the swap used to move data_AP_Atlas_windows_x86_64 as a whole).
        using (new FileStream(Path.Combine(dataFolder, UpdateLayout.MainAssembly), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            bool folderRenamed = true;
            try { Directory.Move(dataFolder, temp.File("aside")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folderRenamed = false; }
            if (folderRenamed) Directory.Move(temp.File("aside"), dataFolder);
            if (OperatingSystem.IsWindows()) Assert.False(folderRenamed, "Windows renamed a folder with an open file in it; the premise of this test is gone");
            var log = new List<string>();
            UpdateInstaller.Swap(install, staged, previous, "0.1.0", log.Add);
            Assert.Equal("new dll", Read(Path.Combine(dataFolder, UpdateLayout.MainAssembly)));
            Assert.Equal("new exe", Read(Path.Combine(install, UpdateLayout.Exe)));
            Assert.Equal("old dll", Read(Path.Combine(previous, UpdateLayout.DataFolder, UpdateLayout.MainAssembly)));
            Assert.Equal("old exe", Read(Path.Combine(previous, UpdateLayout.Exe)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(staged));
            Assert.Contains(log, line => line.Contains("files moved"));
        }
        // The supervisor runs from the previous version while it puts it back.
        using (new FileStream(Path.Combine(previous, UpdateLayout.DataFolder, UpdateLayout.MainAssembly), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.Equal("0.1.0", UpdateInstaller.RollBack(install, previous, failed, "0.2.0", _ => { }));
        }
        Assert.Equal("old dll", Read(Path.Combine(dataFolder, UpdateLayout.MainAssembly)));
        Assert.Equal("new dll", Read(Path.Combine(failed, UpdateLayout.DataFolder, UpdateLayout.MainAssembly)));
        Assert.Null(UpdateLayout.Validate(install));
        Assert.Null(UpdateLayout.Validate(failed));
    }

    [Fact]
    public async Task The_watch_sees_a_start_an_exit_and_a_timeout()
    {
        using var temp = new TempFolder();
        string updates = temp.File("updates");
        Assert.Null(UpdateInstaller.ReadMarker(updates));
        UpdateInstaller.WriteMarker(updates, new UpdateMarker { FromVersion = "0.1.0", ToVersion = "0.2.0", State = UpdateMarker.Starting });
        Assert.Equal(UpdateInstaller.Watch.Exited, await UpdateInstaller.WatchAsync(updates, () => true, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(UpdateInstaller.Watch.TimedOut, await UpdateInstaller.WatchAsync(updates, () => false, TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken));
        var marker = UpdateInstaller.ReadMarker(updates)!;
        marker.State = UpdateMarker.Ready;
        UpdateInstaller.WriteMarker(updates, marker);
        Assert.Equal(UpdateInstaller.Watch.Started, await UpdateInstaller.WatchAsync(updates, () => false, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        // A note written just before the process ends still counts as a start.
        Assert.Equal(UpdateInstaller.Watch.Started, await UpdateInstaller.WatchAsync(updates, () => true, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        UpdateInstaller.DeleteMarker(updates);
        Assert.Null(UpdateInstaller.ReadMarker(updates));
    }
}

public class UpdateStageTests
{
    internal static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    internal static (string Name, string Content)[] Release(string word) => new[]
    {
        ($"{UpdateLayout.ProgramFolder}/{UpdateLayout.Exe}", word + " exe"),
        ($"{UpdateLayout.ProgramFolder}/{UpdateLayout.Pck}", word + " pck"),
        ($"{UpdateLayout.ProgramFolder}/{UpdateLayout.DataFolder}/{UpdateLayout.MainAssembly}", word + " dll"),
        ($"{UpdateLayout.ProgramFolder}/README.md", word + " readme")
    };

    [Fact]
    public void A_release_unpacks_and_is_checked_for_completeness()
    {
        using var temp = new TempFolder();
        string zip = temp.File("release.zip");
        File.WriteAllBytes(zip, Zip(Release("new")));
        var (programDir, error) = UpdateStage.Unpack(zip, temp.File("next"));
        Assert.Null(error);
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(programDir!, UpdateLayout.DataFolder, UpdateLayout.MainAssembly)));

        File.WriteAllBytes(zip, Zip(Release("new").Take(2).ToArray()));
        (programDir, error) = UpdateStage.Unpack(zip, temp.File("next"));
        Assert.Null(programDir);
        Assert.Contains("incomplete", error);

        File.WriteAllBytes(zip, Zip(("elsewhere/file.txt", "x")));
        (_, error) = UpdateStage.Unpack(zip, temp.File("next"));
        Assert.Contains(UpdateLayout.ProgramFolder, error);
    }

    [Fact]
    public void An_entry_that_would_land_outside_the_folder_is_refused()
    {
        using var temp = new TempFolder();
        string zip = temp.File("release.zip");
        File.WriteAllBytes(zip, Zip(Release("new").Append(("../outside.txt", "x")).ToArray()));
        var (programDir, error) = UpdateStage.Unpack(zip, temp.File("next"));
        Assert.Null(programDir);
        Assert.Contains("outside", error);
        Assert.False(File.Exists(temp.File("outside.txt")));
        Assert.False(Directory.Exists(temp.File("next")));
    }

    [Fact]
    public void A_file_past_the_limit_is_refused_as_it_unpacks()
    {
        using var temp = new TempFolder();
        string zip = temp.File("release.zip");
        File.WriteAllBytes(zip, Zip(Release("new")));
        var ex = Assert.Throws<InvalidDataException>(() => SafeZip.UnpackTo(zip, temp.File("small"), fileLimit: 4, totalLimit: 1000));
        Assert.Contains("more than", ex.Message);
        ex = Assert.Throws<InvalidDataException>(() => SafeZip.UnpackTo(zip, temp.File("total"), fileLimit: 1000, totalLimit: 10));
        Assert.Contains("unpacks to more than", ex.Message);
    }
}

public class UpdateDownloaderTests
{
    [Fact]
    public async Task A_release_is_downloaded_only_when_it_matches_its_checksum_file()
    {
        PoliteHttp.Spacing = TimeSpan.Zero;
        try
        {
            byte[] zip = UpdateStageTests.Zip(UpdateStageTests.Release("new"));
            string hash = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            string zipName = $"{ReleaseInfo.ZipPrefix}0.2.0{ReleaseInfo.ZipSuffix}";
            string sums = $"{hash}  {zipName}\n";
            await using var site = new FakeWebSite();
            site.Answer = (path, _) => path switch
            {
                "/dl/SHA256SUMS.txt" => new FakeWebSite.FullAnswer(200, Encoding.UTF8.GetBytes(sums), "text/plain"),
                "/dl/zip" => new FakeWebSite.FullAnswer(200, zip, "application/zip"),
                _ => new FakeWebSite.FullAnswer(404, Array.Empty<byte>())
            };
            var release = new ReleaseInfo("v0.2.0", SemVer.Parse("0.2.0"), false, null, "", zipName, site.Site + "/dl/zip", zip.Length, site.Site + "/dl/SHA256SUMS.txt");
            using var temp = new TempFolder();
            var (zipPath, error) = await UpdateDownloader.DownloadAsync(release, temp.File("download"), null, TestContext.Current.CancellationToken);
            Assert.Null(error);
            Assert.Equal(zip, await File.ReadAllBytesAsync(zipPath!, TestContext.Current.CancellationToken));

            sums = $"{new string('0', 64)}  {zipName}\n";
            (zipPath, error) = await UpdateDownloader.DownloadAsync(release, temp.File("download"), null, TestContext.Current.CancellationToken);
            Assert.Null(zipPath);
            Assert.NotNull(error);
            Assert.Empty(Directory.GetFiles(temp.File("download"), "*.zip"));

            sums = $"{hash}  other.zip\n";
            (_, error) = await UpdateDownloader.DownloadAsync(release, temp.File("download"), null, TestContext.Current.CancellationToken);
            Assert.Contains("doesn't name", error);
        }
        finally
        {
            PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
        }
    }
}
