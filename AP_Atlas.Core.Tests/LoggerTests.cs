namespace AP_Atlas.Core.Tests;

public class LoggerTests
{
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
            lock (shown) Assert.Contains(shown, l => l.Contains("[12:00:00]") && l.Contains("after the folder"));
            lock (shown) Assert.DoesNotContain(shown, l => l.Contains("from Godot"));
        }
        finally
        {
            Logger.Echo = oldEcho;
            Logger.EchoError = oldEchoError;
            Logger.OnLogMessage -= OnLine;
            Logger.DisplayClock = oldClock;
        }
    }
}
