using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core;

/// <summary>A line of a program's output: its text, cut at the reader's limit, and how many characters were cut off it.</summary>
public readonly record struct OutputLine(string Text, long Cut)
{
    /// <summary>The text, with a note of how much was cut if anything was (for the log).</summary>
    public override string ToString() => Cut == 0 ? Text : $"{Text}… ({Cut:N0} more characters)";
}

/// <summary>
/// Reads a program's output a line at a time without trusting how long a line is. .NET's own line readers (ReadLine,
/// ReadLineAsync, BeginOutputReadLine and BeginErrorReadLine) hold a whole line however long it is: a program writing one
/// endless line would fill Atlas's memory, and running out on the thread that reads it ends Atlas. Here the part of a
/// line past the limit is read and dropped as it comes, never held, and the line says how much was cut. Lines end at
/// "\n", "\r" or "\r\n", as with ReadLine. One reader per stream; not thread-safe.
/// </summary>
public sealed class BoundedLineReader
{
    /// <summary>
    /// The most of an answer line kept, in characters: a logic engine's answer, or the names of the games asked about
    /// (every official game's come to about 10 million).
    /// </summary>
    public const int AnswerLimit = 64 << 20;

    /// <summary>
    /// The most of a line kept for the log, in characters: well under <see cref="Logger.MaxMessageLength"/>, so a line
    /// with its prefix is never cut twice.
    /// </summary>
    public const int LogLimit = 16 << 10;

    private readonly TextReader _reader;
    private readonly int _limit;
    private readonly char[] _buffer = new char[8192];
    private int _start;
    private int _end;
    // The last line ended with "\r": a "\n" right after it belongs to that line.
    private bool _afterReturn;

    public BoundedLineReader(TextReader reader, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _reader = reader;
        _limit = limit;
    }

    /// <summary>
    /// Reads a program's output to its end, giving each line that isn't blank (cut at <paramref name="limit"/>, with a note
    /// of how much was cut) to <paramref name="each"/>, or dropping it if that's null. The output closing because the
    /// program ended or was stopped is its end too.
    /// </summary>
    public static async Task ForEachAsync(TextReader output, int limit, Action<string>? each)
    {
        try
        {
            var lines = new BoundedLineReader(output, limit);
            while (await lines.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (!string.IsNullOrWhiteSpace(line.Text)) each?.Invoke(line.ToString());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } // the program ended: nothing more to read
    }

    /// <summary>The next line, or null at the end of the output.</summary>
    public async Task<OutputLine?> ReadLineAsync(CancellationToken ct = default)
    {
        StringBuilder? line = null;
        long cut = 0;
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                if (_end == 0) return line == null ? null : new OutputLine(line.ToString(), cut);
            }
            if (_afterReturn)
            {
                _afterReturn = false;
                if (_buffer[_start] == '\n')
                {
                    _start++;
                    continue;
                }
            }
            line ??= new StringBuilder();
            int found = _buffer.AsSpan(_start, _end - _start).IndexOfAny('\r', '\n');
            int stop = found < 0 ? _end : _start + found;
            int keep = Math.Min(stop - _start, _limit - line.Length);
            line.Append(_buffer, _start, keep);
            cut += stop - _start - keep;
            if (found < 0)
            {
                _start = _end;
                continue;
            }
            _afterReturn = _buffer[stop] == '\r';
            _start = stop + 1;
            return new OutputLine(line.ToString(), cut);
        }
    }
}
