using AP_Atlas.Core.EngineSetup;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// The engine's setup can't fail on Windows' path limit: a folder too deep is said before anything downloads, and the
/// package lock gives up one package for Atlas to unpack without the files that would pass the limit.
/// </summary>
public sealed class EngineSetupTests
{
    private const string Lock = """
        # The Atlas Engine's Python packages for Archipelago 0.6.7
        # requirements-signature: 51DD45A730EB9E05

        bsdiff4==1.2.6 \
            --hash=sha256:04bb2948301ad48123d308bf2342c83cae81d7edb52d11bdde00266d89ca071e \
            --hash=sha256:0b29568d1e33e32ea075c12a696b32e4d6cea344d0270a2292075254efd86014
        setuptools==80.10.2 \
            --hash=sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA \
            --hash=sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
        websockets==13.1 \
            --hash=sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc
        """;

    [Fact]
    public void A_shallow_folder_is_fine_and_a_deep_one_is_refused_in_plain_words()
    {
        Assert.Null(EnginePaths.DepthProblem(@"C:\Games\Atlas\PortableData"));
        // The folder the first beta tester's copy sat in: Windows' Extract All adds the zip's name as a folder.
        string deep = @"C:\Users\somebody\Documents\Archipelago\Atlas Beta\TheArchipelagoAtlas-0.1.0-beta.1-win-x64\TheArchipelagoAtlas\PortableData";
        Assert.Null(EnginePaths.DepthProblem(deep)); // 156 to site-packages, then at most 100: fits once setuptools' tests are left out
        string tooDeep = deep + @"\" + new string('x', 40);
        string? problem = EnginePaths.DepthProblem(tooDeep);
        Assert.NotNull(problem);
        Assert.Contains("too deep for Windows", problem);
        Assert.Contains(@"C:\Games\Atlas", problem);
        Assert.Contains(EnginePaths.WindowsPathLimit.ToString(), problem);
        Assert.Equal(EnginePaths.LongestPath(tooDeep) > EnginePaths.WindowsPathLimit, problem != null);
    }

    [Fact]
    public void The_lock_is_read_block_by_block()
    {
        var blocks = EngineLock.Blocks(Lock);
        Assert.Equal(new[] { "bsdiff4", "setuptools", "websockets" }, blocks.Select(b => b.Name));
        Assert.Equal("80.10.2", blocks[1].Version);
        Assert.Equal(2, blocks[1].Hashes.Count);
        Assert.Equal(new string('a', 64), blocks[1].Hashes[0]); // hashes compare in lower case
        Assert.Single(blocks[2].Hashes);
        Assert.StartsWith("setuptools==80.10.2 \\\n    --hash=sha256:", blocks[1].Text);
    }

    [Fact]
    public void One_package_is_taken_out_for_atlas_and_the_rest_stays_pip_readable()
    {
        var (rest, taken) = EngineLock.Take(Lock, "SetupTools");
        Assert.NotNull(taken);
        Assert.Equal("setuptools", taken!.Name);
        Assert.DoesNotContain("setuptools", rest);
        Assert.Contains("# requirements-signature: 51DD45A730EB9E05", rest);
        Assert.Equal(new[] { "bsdiff4", "websockets" }, EngineLock.Blocks(rest).Select(b => b.Name));
        Assert.Equal(3, EngineLock.Blocks(rest).Sum(b => b.Hashes.Count));
        // Every hash line is still a continuation of its pin, the way pip reads it.
        foreach (string line in rest.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')))
            Assert.True(line.StartsWith("    --hash=sha256:") || line.EndsWith(" \\"), line);

        var (same, none) = EngineLock.Take(Lock, "nothing-here");
        Assert.Null(none);
        Assert.Equal(3, EngineLock.Blocks(same).Count);
    }

    [Fact]
    public void Package_names_compare_the_way_pip_compares_them()
    {
        Assert.Equal("typing-extensions", EngineLock.Normalise("Typing_Extensions"));
        Assert.Equal("charset-normalizer", EngineLock.Normalise("charset.normalizer"));
    }
}
