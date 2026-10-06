namespace AP_Atlas.Core.Tests;

public class LoggerTests
{
    [Fact]
    public void A_message_too_long_is_cut_saying_how_much()
    {
        string marker = "cut-test-" + Guid.NewGuid().ToString("N")[..8] + " ";
        var shown = new List<string>();
        void OnLine(string line, string level) { if (line.Contains(marker)) lock (shown) shown.Add(line); }
        Logger.OnLogMessage += OnLine;
        try { Logger.LogInfo(marker + new string('m', 100_000)); }
        finally { Logger.OnLogMessage -= OnLine; }
        string line = Assert.Single(shown);
        int kept = Logger.MaxMessageLength - marker.Length;
        Assert.Contains(marker + new string('m', kept) + $"… ({100_000 - kept:N0} more characters)", line);
        Assert.DoesNotContain(new string('m', kept + 1), line);
    }

    [Fact]
    public void Lines_are_echoed_and_written_to_the_file_once_a_folder_is_given_and_Godot_lines_only_to_the_file()
    {
        using var dir = new TempFolder();
        var echoed = new List<string>();
        var shown = new List<string>();
        var oldEcho = Logger.Echo;
        var oldEchoError = Logger.EchoError;
        void OnLine(string line, string level) { lock (shown) shown.Add(line); }
        Logger.Echo = line => { lock (echoed) echoed.Add(line); };
        Logger.EchoError = line => { lock (echoed) echoed.Add(line); };
        Logger.OnLogMessage += OnLine;
        var oldClock = Logger.DisplayClock;
        Logger.DisplayClock = () => new DateTime(2000, 1, 1, 12, 0, 0);
        try
        {
            Logger.LogInfo("before the folder");
            Assert.False(File.Exists(Path.Combine(dir.Path, "atlas_log.txt")), "nothing is written before the app names the folder");

            Logger.UseFolder(dir.Path);
            Logger.LogWarning("after the folder");
            // Godot's own errors: written to the file, never echoed back to Godot or shown in the window.
            Logger.RecordOnly("ERROR", "from Godot");

            string file = File.ReadAllText(Path.Combine(dir.Path, "atlas_log.txt"));
            Assert.Contains("[WARN] after the folder", file);
            Assert.Contains("[ERROR] from Godot", file);
            Assert.DoesNotContain("before the folder", file);
            lock (echoed) Assert.Contains(echoed, l => l.Contains("before the folder"));
            lock (echoed) Assert.DoesNotContain(echoed, l => l.Contains("from Godot"));
            // The window's line (BBCode) shows the time and the message as written: their "[" escaped.
            lock (shown) Assert.Contains(shown, l => l.Contains(Bbcode.Escape("[12:00:00]")) && l.Contains("after the folder"));
            lock (shown) Assert.DoesNotContain(shown, l => l.Contains("from Godot"));
        }
        finally
        {
            Logger.StopUsingFolder(); // the folder is deleted next: later lines in this run aren't written anywhere
            Logger.Echo = oldEcho;
            Logger.EchoError = oldEchoError;
            Logger.OnLogMessage -= OnLine;
            Logger.DisplayClock = oldClock;
        }
    }

    [Fact]
    public void A_log_file_that_cant_be_written_is_reported_once_until_writing_works_again()
    {
        using var dir = new TempFolder();
        string logs = Path.Combine(dir.Path, "logs");
        var errors = new List<string>();
        var oldEcho = Logger.Echo;
        var oldEchoError = Logger.EchoError;
        Logger.Echo = _ => { };
        Logger.EchoError = line => { lock (errors) errors.Add(line); };
        try
        {
            Logger.UseFolder(logs);
            Directory.Delete(logs, recursive: true); // the folder disappears while Atlas runs

            Logger.LogInfo("lost one");
            Logger.LogInfo("lost two");
            Logger.LogInfo("lost three");
            lock (errors) Assert.Single(errors, e => e.StartsWith("Failed to write to log", StringComparison.Ordinal));

            Directory.CreateDirectory(logs);
            Logger.LogInfo("written again");
            Assert.Contains("written again", File.ReadAllText(Path.Combine(logs, "atlas_log.txt")));

            Directory.Delete(logs, recursive: true);
            Logger.LogInfo("lost after that");
            lock (errors) Assert.Equal(2, errors.Count(e => e.StartsWith("Failed to write to log", StringComparison.Ordinal)));
        }
        finally
        {
            Logger.StopUsingFolder();
            Logger.Echo = oldEcho;
            Logger.EchoError = oldEchoError;
        }
    }
}
