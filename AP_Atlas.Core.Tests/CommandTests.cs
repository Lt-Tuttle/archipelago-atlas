namespace AP_Atlas.Core.Tests;

public class CommandTests
{
    private static Commands Registry(Dictionary<string, string>? rebinds = null) => new(() => rebinds);

    [Theory]
    [InlineData("ctrl+shift+p", "Ctrl+Shift+P")]
    [InlineData("CTRL + F", "Ctrl+F")]
    [InlineData("shift+ctrl+a", "Ctrl+Shift+A")]
    [InlineData("alt+win+f11", "Alt+Win+F11")]
    [InlineData("f1", "F1")]
    [InlineData("Control+1", "Ctrl+1")]
    [InlineData("ctrl+tab", "Ctrl+Tab")]
    [InlineData("escape", "Escape")]
    [InlineData("ctrl+plus", "Ctrl++")]
    [InlineData("ctrl+comma", "Ctrl+,")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    public void Shortcuts_are_written_one_way(string typed, string canonical) => Assert.Equal(canonical, Commands.Normalize(typed));

    [Theory]
    [InlineData("ctrl+shift")] // only modifiers
    [InlineData("a+b")] // two keys
    [InlineData("ctrl+francis")] // not a key
    [InlineData("")]
    public void What_is_not_a_shortcut_is_refused(string typed) => Assert.Null(Commands.Normalize(typed));

    [Fact]
    public void Two_commands_cannot_share_an_id()
    {
        var commands = Registry();
        commands.Add(new Command("test.one", "File", "One", "", () => { }));
        Assert.Throws<InvalidOperationException>(() => commands.Add(new Command("test.one", "File", "Again", "", () => { })));
    }

    [Fact]
    public void A_rebind_replaces_the_default_and_an_empty_one_unbinds()
    {
        var rebinds = new Dictionary<string, string>();
        var commands = Registry(rebinds);
        int ran = 0;
        commands.Add(new Command("test.go", "File", "Go", "Ctrl+G", () => ran++));
        Assert.Equal("Ctrl+G", commands.ShortcutOf("test.go"));
        Assert.True(commands.RunShortcut("Ctrl+G"));
        Assert.Equal(1, ran);

        rebinds["test.go"] = "ctrl+shift+g"; // as a user might type it into the settings file
        Assert.Equal("Ctrl+Shift+G", commands.ShortcutOf("test.go"));
        Assert.False(commands.RunShortcut("Ctrl+G"));
        Assert.True(commands.RunShortcut("Ctrl+Shift+G"));
        Assert.Equal(2, ran);

        rebinds["test.go"] = "";
        Assert.Equal("", commands.ShortcutOf("test.go"));
        Assert.False(commands.RunShortcut("Ctrl+G"));
        Assert.False(commands.RunShortcut("Ctrl+Shift+G"));

        // A rebind that isn't a shortcut falls back to the default, never binds something unintended.
        rebinds["test.go"] = "ctrl+nonsense";
        Assert.Equal("Ctrl+G", commands.ShortcutOf("test.go"));
    }

    [Fact]
    public void A_disabled_command_is_skipped_and_its_key_can_serve_another()
    {
        var commands = Registry();
        bool firstEnabled = false;
        int first = 0, second = 0;
        commands.Add(new Command("test.first", "File", "First", "Ctrl+K", () => first++) { Enabled = () => firstEnabled });
        commands.Add(new Command("test.second", "File", "Second", "Ctrl+K", () => second++));
        Assert.True(commands.RunShortcut("Ctrl+K"));
        Assert.Equal((0, 1), (first, second));
        Assert.False(commands.Run("test.first"));
        firstEnabled = true;
        Assert.True(commands.RunShortcut("Ctrl+K"));
        Assert.Equal((1, 1), (first, second));
        var conflict = Assert.Single(commands.Conflicts());
        Assert.Equal("Ctrl+K", conflict.Shortcut);
        Assert.Equal(2, conflict.Bound.Count);
    }

    [Fact]
    public void A_command_that_fails_is_reported_not_thrown()
    {
        var commands = Registry();
        commands.Add(new Command("test.broken", "File", "Broken", "", () => throw new InvalidOperationException("broken")));
        var reported = new List<string>();
        void OnFailed(string doing, Exception ex) { lock (reported) reported.Add(doing); }
        Async.Failed += OnFailed;
        try
        {
            Assert.True(commands.Run("test.broken"));
        }
        finally
        {
            Async.Failed -= OnFailed;
        }
        Assert.Contains(reported, doing => doing.Contains("Broken"));
    }

    [Fact]
    public void A_default_that_is_not_a_shortcut_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => new Command("test.bad", "File", "Bad", "ctrl+what", () => { }));
        Assert.Equal("", new Command("test.none", "File", "None", "", () => { }).DefaultShortcut);
    }
}
