namespace AP_Atlas.Core.Tests;

public class AlertLogTests
{
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0);

    [Fact]
    public void Alerts_are_kept_newest_first_with_their_ids_in_order()
    {
        var log = new AlertLog();
        log.Add(AlertKind.Info, "first", Noon);
        log.Add(AlertKind.Error, "second", Noon.AddSeconds(1));
        Assert.Equal(new[] { "second", "first" }, log.Entries.Select(e => e.Message));
        Assert.True(log.Entries[0].Id > log.Entries[1].Id);
        Assert.Equal(AlertKind.Error, log.Entries[0].Kind);
    }

    [Fact]
    public void The_oldest_go_once_the_log_is_full()
    {
        var log = new AlertLog();
        for (int i = 0; i < AlertLog.MaxEntries + 5; i++) log.Add(AlertKind.Info, "alert " + i, Noon);
        Assert.Equal(AlertLog.MaxEntries, log.Entries.Count);
        Assert.Equal("alert " + (AlertLog.MaxEntries + 4), log.Entries[0].Message);
        Assert.Equal("alert 5", log.Entries[^1].Message);
    }

    [Fact]
    public void Unseen_counts_what_was_added_since_the_window_opened()
    {
        var log = new AlertLog();
        int changes = 0;
        log.Changed += () => changes++;
        log.Add(AlertKind.Warning, "a", Noon);
        log.Add(AlertKind.Warning, "b", Noon);
        Assert.Equal(2, log.Unseen);
        log.MarkSeen();
        Assert.Equal(0, log.Unseen);
        log.MarkSeen(); // nothing to do: no change reported
        Assert.Equal(3, changes);
        log.Clear();
        Assert.Empty(log.Entries);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void A_message_is_cut_to_what_a_line_may_show() =>
        Assert.True(new AlertLog().Add(AlertKind.Info, new string('x', 10_000), Noon).Message.Length <= Logger.MaxShownLength + 60); // the cut, plus how much more there was
}
