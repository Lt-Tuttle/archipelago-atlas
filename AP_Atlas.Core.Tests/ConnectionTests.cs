using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using AP_Atlas.Core.Connections;
using AP_Atlas.Core.Testing;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>The games' names (data packages) stay in Atlas's folder, and a server sends each version once.</summary>
public class DataPackageStoreTests
{
    private const string Checksum = "0123456789abcdef0123456789abcdef01234567";
    private const string NewChecksum = "fedcba9876543210fedcba9876543210fedcba98";

    private static FakeArchipelagoServer ServerWithOneGame(string checksum = Checksum)
    {
        var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame(checksum,
            new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001 },
            new Dictionary<string, long> { ["Cave Chest"] = 2000, ["Boss"] = 2001 });
        return server;
    }

    private static async Task<ArchipelagoSession> LogInAsync(FakeArchipelagoServer server, DataPackageStore store)
    {
        var ct = TestContext.Current.CancellationToken;
        var session = AtlasSessions.Create(server.Url, store);
        await session.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
        var result = await session.LoginAsync("", server.SlotName, ItemsHandlingFlags.AllItems, new Version(0, 5, 0), new[] { "Tracker" }, null, null, false)
            .WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(result.Successful, "the fake server refused the login");
        return session;
    }

    /// <summary>Closes a session as SessionManager does: its connection, then its thread (see AtlasSessions.Finished).</summary>
    private static async Task CloseAsync(ArchipelagoSession session)
    {
        await session.Socket.DisconnectAsync();
        AtlasSessions.Finished(session);
    }

    private static GameData Data(string checksum) => new()
    {
        Checksum = checksum,
        ItemLookup = new Dictionary<string, long> { ["Sword"] = 1000 },
        LocationLookup = new Dictionary<string, long> { ["Cave Chest"] = 2000 }
    };

    [Fact]
    public async Task Names_are_kept_in_Atlas_folder_and_a_server_sends_each_version_once()
    {
        using var dir = new TempFolder();
        var store = new DataPackageStore(dir.Path);
        await using var server = ServerWithOneGame();

        var first = await LogInAsync(server, store);
        // The session reads Atlas's store: the library's own cache (in %LocalAppData%) was never set up.
        Assert.Same(store, AtlasSessions.StoreOf(first));
        Assert.Equal("Sword", first.Items.GetItemName(1000, "Test Game"));
        await CloseAsync(first);
        Assert.Equal(1, server.Count("GetDataPackage"));
        Assert.Equal(Path.Combine(dir.Path, "Test Game", Checksum + ".json"), Assert.Single(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories)));

        var second = await LogInAsync(server, store);
        Assert.Equal("Cave Chest", second.Locations.GetLocationNameFromId(2000, "Test Game"));
        await CloseAsync(second);
        Assert.Equal(1, server.Count("GetDataPackage")); // the second connection read the store instead of asking again
    }

    [Fact]
    public async Task A_new_version_of_a_game_is_asked_for_and_kept_beside_the_old_one()
    {
        using var dir = new TempFolder();
        var store = new DataPackageStore(dir.Path);
        await using (var server = ServerWithOneGame())
            await CloseAsync(await LogInAsync(server, store));

        await using var updated = ServerWithOneGame(NewChecksum);
        var session = await LogInAsync(updated, store);
        await CloseAsync(session);
        Assert.Equal(1, updated.Count("GetDataPackage"));
        Assert.True(store.TryGet("Test Game", Checksum, out _));
        Assert.True(store.TryGet("Test Game", NewChecksum, out _));
    }

    [Fact]
    public async Task A_damaged_file_is_asked_for_again_and_replaced()
    {
        using var dir = new TempFolder();
        var store = new DataPackageStore(dir.Path);
        string file = Path.Combine(dir.Path, "Test Game", Checksum + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"Game\":\"Test Game\",\"Data\":{\"item_name_to_id\":{\"Swo");
        await using var server = ServerWithOneGame();

        var session = await LogInAsync(server, store);
        Assert.Equal("Shield", session.Items.GetItemName(1001, "Test Game"));
        await CloseAsync(session);
        Assert.Equal(1, server.Count("GetDataPackage"));
        Assert.True(store.TryGet("Test Game", Checksum, out var data));
        Assert.Equal(2001, data.LocationLookup["Boss"]);
    }

    [Fact]
    public void A_file_holding_another_game_or_version_is_never_used()
    {
        using var dir = new TempFolder();
        var store = new DataPackageStore(dir.Path);
        store.Save("Other Game", Data(Checksum));
        string theirs = Path.Combine(dir.Path, "Other Game", Checksum + ".json");
        Directory.CreateDirectory(Path.Combine(dir.Path, "Test Game"));
        File.Copy(theirs, Path.Combine(dir.Path, "Test Game", Checksum + ".json"));
        File.Copy(theirs, Path.Combine(dir.Path, "Other Game", NewChecksum + ".json"));

        Assert.False(store.TryGet("Test Game", Checksum, out _));
        Assert.False(store.TryGet("Other Game", NewChecksum, out _));
        Assert.True(store.TryGet("Other Game", Checksum, out _));
        Assert.Equal(new[] { theirs }, Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories)); // the mismatched copies were removed
    }

    [Theory]
    [InlineData("../../Escaped")]
    [InlineData("..\\..\\Escaped")]
    [InlineData("C:\\Windows\\Escaped")]
    [InlineData("\\\\server\\share\\Escaped")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("Game: The \"Sequel\" | Part <2>?*")]
    [InlineData("Trailing dots and spaces. . ")]
    [InlineData("A game whose name goes on and on and on and on and on and on and on and on and on and on and on and on")]
    public void A_game_name_from_a_server_never_reaches_outside_the_store(string game)
    {
        using var dir = new TempFolder();
        var store = new DataPackageStore(Path.Combine(dir.Path, "store"));

        store.Save(game, Data(Checksum));

        Assert.Equal(new[] { "store" }, Directory.GetFileSystemEntries(dir.Path).Select(Path.GetFileName));
        string file = Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "store"), "*", SearchOption.AllDirectories));
        Assert.Equal(Path.Combine(dir.Path, "store"), Path.GetDirectoryName(Path.GetDirectoryName(file)));
        Assert.True(store.TryGet(game, Checksum, out var data));
        Assert.Equal(1000, data.ItemLookup["Sword"]);
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("a/b")]
    [InlineData("C:")]
    [InlineData("")]
    [InlineData(null)]
    public void A_checksum_that_isnt_one_is_never_used_as_a_file_name(string? checksum)
    {
        using var dir = new TempFolder();
        var store = new DataPackageStore(Path.Combine(dir.Path, "store"));
        var data = Data(Checksum);
        data.Checksum = checksum;

        store.Save("Test Game", data);

        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
        Assert.False(store.TryGet("Test Game", checksum, out _));
    }

    [Fact]
    public void Names_unused_for_months_and_leftovers_are_removed_and_reading_keeps_a_file()
    {
        using var dir = new TempFolder();
        var store = new DataPackageStore(dir.Path);
        store.Save("Old Game", Data(Checksum));
        store.Save("Read Game", Data(Checksum));
        store.Save("New Game", Data(Checksum));
        File.SetLastWriteTimeUtc(Path.Combine(dir.Path, "Old Game", Checksum + ".json"), DateTime.UtcNow.AddDays(-100));
        File.SetLastWriteTimeUtc(Path.Combine(dir.Path, "Read Game", Checksum + ".json"), DateTime.UtcNow.AddDays(-100));
        string leftover = Path.Combine(dir.Path, "New Game", Checksum + ".json.0123abcd.tmp");
        File.WriteAllText(leftover, "{");
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-2));
        Assert.True(store.TryGet("Read Game", Checksum, out _)); // reading counts as using it

        Assert.Equal(2, store.RemoveUnused(TimeSpan.FromDays(90)));

        Assert.False(Directory.Exists(Path.Combine(dir.Path, "Old Game"))); // its empty folder went too
        Assert.False(File.Exists(leftover));
        Assert.True(store.TryGet("Read Game", Checksum, out _));
        Assert.True(store.TryGet("New Game", Checksum, out _));
        var missing = new DataPackageStore(Path.Combine(dir.Path, "missing"));
        Assert.Equal(0, missing.RemoveUnused(TimeSpan.Zero));
        Assert.False(Directory.Exists(missing.Folder)); // tidying never creates the folder
    }

    [Fact]
    public async Task Several_connections_can_use_the_store_at_once()
    {
        using var dir = new TempFolder();
        var store = new DataPackageStore(dir.Path);
        var ct = TestContext.Current.CancellationToken;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int n = 0; n < 40; n++)
            {
                store.Save("Test Game", Data(Checksum));
                Assert.True(store.TryGet("Test Game", Checksum, out _));
            }
        }, ct)));

        Assert.Single(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories)); // nothing left over
    }

    [Fact]
    public void A_session_is_never_given_a_second_cache()
    {
        using var dir = new TempFolder();
        var store = new DataPackageStore(dir.Path);
        var session = AtlasSessions.Create(new Uri("ws://127.0.0.1:9"), store); // never connected

        Assert.Null(LibraryCache.Problem);
        Assert.Same(store, AtlasSessions.StoreOf(session));
        var refused = Assert.Throws<NotSupportedException>(() => LibraryCache.Attach(session, new DataPackageStore(dir.Path)));
        Assert.Contains("won't connect", refused.Message);
        Assert.Same(store, AtlasSessions.StoreOf(session));
    }
}

/// <summary>What the connection library itself can reach on the user's computer.</summary>
public class ConnectionLibraryTests
{
    private const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const string ReplacedCache = "Archipelago.MultiClient.Net.DataPackage.FileSystemCheckSumDataPackageProvider ";

    /// <summary>
    /// Reads every method of the library and lists what it calls that reaches past the network: files and folders, the
    /// user's folders and environment, the registry and other programs. Only the data cache Atlas replaces may. Run on
    /// every build, so a library update that reaches further fails here instead of on a user's computer.
    /// </summary>
    [Fact]
    public void The_connection_library_reaches_the_computer_only_through_the_cache_Atlas_replaces()
    {
        var uses = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in typeof(ArchipelagoSession).Assembly.GetTypes())
            foreach (var method in type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)))
                foreach (var used in Il.MembersUsedBy(method).Where(ReachesTheComputer))
                    uses.Add($"{type.FullName} {method.Name} -> {used.DeclaringType?.FullName}.{used.Name}");

        Assert.Contains(uses, u => u.StartsWith(ReplacedCache, StringComparison.Ordinal)); // the scan does see the cache's file use
        var elsewhere = uses.Where(u => !u.StartsWith(ReplacedCache, StringComparison.Ordinal)).ToList();
        Assert.True(elsewhere.Count == 0, "The connection library reaches files, folders, the registry or other programs outside the " +
            "data cache Atlas replaces. Check what it does before using this version:\n" + string.Join("\n", elsewhere));
    }

    private static bool ReachesTheComputer(MemberInfo member)
    {
        string type = member.DeclaringType?.FullName ?? "";
        if (type.StartsWith("Microsoft.Win32", StringComparison.Ordinal) || type.StartsWith("System.IO.IsolatedStorage", StringComparison.Ordinal)
            || type.StartsWith("System.IO.MemoryMappedFiles", StringComparison.Ordinal) || type.StartsWith("System.IO.Compression.ZipFile", StringComparison.Ordinal)
            || type == "System.Diagnostics.Process")
            return true;
        if (type is "System.IO.File" or "System.IO.Directory" or "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileSystemInfo"
            or "System.IO.FileStream" or "System.IO.DriveInfo" or "System.IO.FileSystemWatcher")
            return true;
        if (type == "System.IO.Path") return member.Name is "GetTempPath" or "GetTempFileName";
        if (type == "System.Environment")
            return member.Name is "GetFolderPath" or "GetEnvironmentVariable" or "GetEnvironmentVariables" or "SetEnvironmentVariable"
                or "ExpandEnvironmentVariables" or "set_CurrentDirectory";
        // A reader or writer opened on a file name rather than on a stream.
        if (type is "System.IO.StreamReader" or "System.IO.StreamWriter" && member is ConstructorInfo constructor)
            return constructor.GetParameters().FirstOrDefault()?.ParameterType == typeof(string);
        return false;
    }

    /// <summary>Reads a method's IL for the methods, constructors and fields it uses.</summary>
    private static class Il
    {
        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

        public static IEnumerable<MemberInfo> MembersUsedBy(MethodBase method)
        {
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) yield break;
            var typeArguments = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                short value = il[i++];
                if (value == 0xFE) value = (short)(0xFE00 | il[i++]);
                var op = OpCodesByValue[value];
                int size = op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4
                };
                if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok)
                {
                    MemberInfo? member = null;
                    try { member = method.Module.ResolveMember(BitConverter.ToInt32(il, i), typeArguments, methodArguments); }
                    catch (ArgumentException) { } // a token for a type, not a member
                    if (member != null) yield return member;
                }
                i += size;
            }
        }
    }
}

/// <summary>The engine's runner keeps Archipelago's cache and Python's temporary files in the engine's own folder.</summary>
public class EngineRunnerTests
{
    [Fact]
    public async Task Archipelagos_cache_and_temporary_files_stay_in_the_engine_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        string? python = await TestEnvironment.PythonAsync(ct);
        if (python == null)
        {
            Assert.Skip("Python isn't installed here (CI runs this test).");
            return;
        }
        using var dir = new TempFolder();
        string engine = Path.Combine(dir.Path, "engine");
        string archipelago = Path.Combine(engine, "archipelago");
        // Stands in for the user's folders: the process's temp folder points here, and so does Archipelago's cache.
        string outside = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "Temp"));
        Directory.CreateDirectory(Path.Combine(archipelago, "worlds"));
        File.Copy(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "Python", "atlas_run.py"), Path.Combine(engine, "atlas_run.py"));
        // Something asks for the temp folder before the runner sets it up, and Python keeps that first answer.
        File.WriteAllText(Path.Combine(archipelago, "ModuleUpdate.py"), """
            import tempfile
            tempfile.gettempdir()
            update_ran = False
            """);
        // A stand-in for Archipelago's Utils: the first call picks a folder outside the engine and keeps it.
        File.WriteAllText(Path.Combine(archipelago, "Utils.py"), """
            import os
            def cache_path(*path):
                if not hasattr(cache_path, "cached_path"):
                    cache_path.cached_path = os.path.join(os.environ["ATLAS_TEST_OUTSIDE"], "Archipelago", "Cache")
                return os.path.join(cache_path.cached_path, *path)
            def load_data_package_for_checksum(game, checksum):
                return cache_path("datapackage", game, checksum + ".json")
            """);
        File.WriteAllText(Path.Combine(archipelago, "worlds", "__init__.py"), "");
        File.WriteAllText(Path.Combine(archipelago, "worlds", "LauncherComponents.py"), """
            import json, sys, tempfile
            class Component:
                def __init__(self, display_name, func):
                    self.display_name, self.func = display_name, func
            def probe():
                import Utils
                from Utils import cache_path
                # Answers go to Atlas's channel, the process's own standard output (the runner sends prints elsewhere).
                sys.__stdout__.write(json.dumps({"direct": cache_path("common.json"), "inside": Utils.load_data_package_for_checksum("G", "c"),
                                                 "temp": tempfile.mkdtemp(prefix="probe_")}) + "\n")
            components = [Component("AtlasProbe", probe)]
            """);

        var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-u", "-X", "utf8", Path.Combine(engine, "atlas_run.py"), archipelago, "AtlasProbe" }) info.ArgumentList.Add(arg);
        info.Environment["ATLAS_TEST_OUTSIDE"] = outside;
        info.Environment["TEMP"] = Path.Combine(outside, "Temp");
        info.Environment["TMP"] = Path.Combine(outside, "Temp");
        info.Environment.Remove("TMPDIR");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(1), ct);
        Assert.True(process.ExitCode == 0, "the runner failed: " + await errors);

        var paths = JObject.Parse((await output).Trim().Split('\n').Last());
        foreach (string name in new[] { "direct", "inside", "temp" })
            Assert.StartsWith(engine + Path.DirectorySeparatorChar, (string?)paths[name]);
        Assert.Empty(Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
        Assert.Equal(new[] { Path.Combine(outside, "Temp") }, Directory.GetDirectories(outside, "*", SearchOption.AllDirectories));
    }
}
