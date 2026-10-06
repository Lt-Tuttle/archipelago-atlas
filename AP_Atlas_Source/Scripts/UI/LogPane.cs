using System.Collections.Concurrent;
using System.Text;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Writes lines to a log view (the System Log, the Debug Log) from any thread: they're queued and added once per
    /// frame, all together, and the view keeps only its last lines. A busy session logs hundreds of lines a second, and an
    /// hours-long one would otherwise grow the view without limit. The log file keeps everything.
    /// </summary>
    public sealed class LogPane
    {
        /// <summary>How many lines the view keeps.</summary>
        public const int Lines = 2000;

        // Trimmed in steps, not a line at a time: removing the first line shifts all the others.
        private const int TrimStep = 200;

        private readonly SafeRichText _view;
        private readonly ConcurrentQueue<string> _queue = new();
        private int _scheduled;

        public LogPane(SafeRichText view) => _view = view;

        /// <summary>Adds text (BBCode, ending in a line break) at the end of this frame. Any thread.</summary>
        public void Append(string text)
        {
            _queue.Enqueue(text);
            if (System.Threading.Interlocked.Exchange(ref _scheduled, 1) == 0) Ui.DeferQuiet(_view, Flush);
        }

        private void Flush()
        {
            System.Threading.Volatile.Write(ref _scheduled, 0);
            var text = new StringBuilder();
            while (_queue.TryDequeue(out var line)) text.Append(line);
            if (text.Length == 0) return;
            _view.Append(text.ToString());
            int extra = _view.GetParagraphCount() - Lines;
            if (extra < TrimStep) return;
            for (int i = 0; i < extra; i++) _view.RemoveParagraph(0, noInvalidate: i < extra - 1);
        }
    }
}
