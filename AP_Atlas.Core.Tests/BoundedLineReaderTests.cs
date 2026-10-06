namespace AP_Atlas.Core.Tests;

public class BoundedLineReaderTests
{
    private static async Task<List<OutputLine>> ReadAllAsync(TextReader text, int limit)
    {
        var reader = new BoundedLineReader(text, limit);
        var lines = new List<OutputLine>();
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line) lines.Add(line);
        return lines;
    }

    [Fact]
    public async Task Lines_end_as_ReadLine_ends_them()
    {
        const string text = "one\ntwo\r\nthree\rfour\n\nsix";
        var expected = new List<string>();
        using (var plain = new StringReader(text))
            while (plain.ReadLine() is { } line) expected.Add(line);
        var lines = await ReadAllAsync(new StringReader(text), 100);
        Assert.Equal(expected, lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(0, l.Cut));
        Assert.Empty(await ReadAllAsync(new StringReader(""), 100));
        Assert.Equal(new[] { "" }, (await ReadAllAsync(new StringReader("\n"), 100)).Select(l => l.Text));
    }

    [Fact]
    public async Task A_line_break_split_between_reads_is_one_break()
    {
        // The reader takes 8,192 characters at a time: "\r" ends one read and "\n" starts the next.
        string text = new string('a', 8191) + "\r\nnext\r\n";
        var lines = await ReadAllAsync(new StringReader(text), 10_000);
        Assert.Equal(new[] { new string('a', 8191), "next" }, lines.Select(l => l.Text));
    }

    [Fact]
    public async Task A_line_past_the_limit_is_cut_and_the_next_one_is_whole()
    {
        string text = new string('x', 50_000) + "\nshort\n" + new string('y', 20) + "\r\nlast";
        var lines = await ReadAllAsync(new StringReader(text), 1_000);
        Assert.Equal(4, lines.Count);
        Assert.Equal((new string('x', 1_000), 49_000L), (lines[0].Text, lines[0].Cut));
        Assert.Equal(new string('x', 1_000) + "… (49,000 more characters)", lines[0].ToString());
        Assert.Equal(("short", 0L), (lines[1].Text, lines[1].Cut));
        Assert.Equal(new string('y', 20), lines[2].Text);
        Assert.Equal("last", lines[3].ToString());
    }

    /// <summary>A program writing one endless line: characters made up as they're read, never held.</summary>
    private sealed class EndlessLine(long length) : TextReader
    {
        private long _left = length;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            int count = (int)Math.Min(buffer.Length, _left);
            buffer.Span[..count].Fill('z');
            _left -= count;
            return ValueTask.FromResult(count);
        }
    }

    [Fact]
    public async Task An_endless_line_is_read_to_its_end_holding_only_the_limit()
    {
        // 500 million characters (a gigabyte held as a string) read through the log's limit.
        var lines = await ReadAllAsync(new EndlessLine(500_000_000), BoundedLineReader.LogLimit);
        var line = Assert.Single(lines);
        Assert.Equal(BoundedLineReader.LogLimit, line.Text.Length);
        Assert.Equal(500_000_000L - BoundedLineReader.LogLimit, line.Cut);
    }
}
