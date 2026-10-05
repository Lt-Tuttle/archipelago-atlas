using Newtonsoft.Json;

namespace AP_Atlas.Core.Tests;

public class SafeFileTests
{
    [Fact]
    public void A_save_keeps_the_previous_version_as_the_backup()
    {
        using var dir = new TempFolder();
        string path = dir.File("data.json");
        SafeFile.WriteJson(path, new Sample { Version = 1 });
        SafeFile.WriteJson(path, new Sample { Version = 2 });

        Assert.Equal(2, SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version);
        Assert.Equal(1, JsonConvert.DeserializeObject<Sample>(File.ReadAllText(path + ".bak"))?.Version);
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_its_backup_used()
    {
        using var dir = new TempFolder();
        string path = dir.File("data.json");
        SafeFile.WriteJson(path, new Sample { Version = 1 });
        SafeFile.WriteJson(path, new Sample { Version = 2 });
        File.WriteAllText(path, "{\"Version\": 2, \"Items\": [\"cut off");

        Assert.Equal(1, SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version);
        Assert.Single(Directory.GetFiles(dir.Path, "data.json.corrupt-*"));
    }

    [Fact]
    public void A_zero_filled_file_counts_as_damaged()
    {
        using var dir = new TempFolder();
        string path = dir.File("data.json");
        SafeFile.WriteJson(path, new Sample { Version = 1 });
        SafeFile.WriteJson(path, new Sample { Version = 2 });
        File.WriteAllBytes(path, new byte[64]);

        Assert.Equal(1, SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version);
    }

    [Fact]
    public void A_missing_file_with_no_backup_gives_the_fallback()
    {
        using var dir = new TempFolder();
        Assert.Equal(-1, SafeFile.ReadJson(dir.File("none.json"), () => new Sample { Version = -1 }).Version);
    }

    [Fact]
    public async Task A_file_held_briefly_by_another_program_reads_as_it_is()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempFolder();
        string path = dir.File("held.json");
        SafeFile.WriteJson(path, new Sample { Version = 1 });
        SafeFile.WriteJson(path, new Sample { Version = 2 });

        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(250, ct); await holder.DisposeAsync(); }, ct);
        var read = await Task.Run(() => SafeFile.ReadJson(path, () => new Sample { Version = -1 }), ct);
        await release;

        Assert.Equal(2, read.Version);
        Assert.Empty(Directory.GetFiles(dir.Path, "held.json.corrupt-*"));
    }

    [Fact]
    public async Task A_file_held_throughout_is_never_saved_over_until_it_reads_again()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempFolder();
        string path = dir.File("held.json");
        SafeFile.WriteJson(path, new Sample { Version = 2 });
        var recovered = new List<string>();
        void OnRecovered(string file, string what) { lock (recovered) recovered.Add(what); }
        SafeFile.Recovered += OnRecovered;
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var fallback = await Task.Run(() => SafeFile.ReadJson(path, () => new Sample { Version = -1 }), ct);
                Assert.Equal(-1, fallback.Version);
            }
            Assert.Contains(recovered, r => r.Contains("another program"));
            Assert.Throws<IOException>(() => SafeFile.WriteJson(path, new Sample { Version = 99 }));
            Assert.Equal(2, JsonConvert.DeserializeObject<Sample>(File.ReadAllText(path))?.Version);
            Assert.Empty(Directory.GetFiles(dir.Path, "held.json.corrupt-*"));

            Assert.Equal(2, SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version);
            SafeFile.WriteJson(path, new Sample { Version = 3 });
            Assert.Equal(3, SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version);
        }
        finally
        {
            SafeFile.Recovered -= OnRecovered;
        }
    }

    [Fact]
    public void A_deleted_file_stays_deleted()
    {
        using var dir = new TempFolder();
        string path = dir.File("gone.json");
        SafeFile.WriteJson(path, new Sample { Version = 1 });
        SafeFile.WriteJson(path, new Sample { Version = 2 });
        File.WriteAllText(path + ".tmp", "left by an interrupted save");

        SafeFile.Delete(path);

        Assert.False(File.Exists(path) || File.Exists(path + ".bak") || File.Exists(path + ".tmp"));
        Assert.Equal(-1, SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version);
        SafeFile.Delete(path); // already gone: fine
    }

    [Fact]
    public void Deleting_a_file_whose_folder_is_missing_is_nothing_to_do()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "never-made", "cache.json");
        var time = System.Diagnostics.Stopwatch.StartNew();

        SafeFile.Delete(path); // as when a multiworld that never used a cache is deleted

        // At once: a missing folder isn't a file another program holds, so there's nothing to wait for.
        Assert.True(time.ElapsedMilliseconds < 400, $"deleting took {time.ElapsedMilliseconds} ms");
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "never-made")));
    }
}
