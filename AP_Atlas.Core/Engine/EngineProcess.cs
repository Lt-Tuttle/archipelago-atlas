using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>Why a logic engine gave no answer.</summary>
    public enum EngineFailure
    {
        /// <summary>It answered.</summary>
        None,
        /// <summary>No answer within the time limit; the engine is still running (it may answer later, which is ignored).</summary>
        TimedOut,
        /// <summary>The engine ended by itself (it crashed, or closed its output).</summary>
        Ended,
        /// <summary>The bridge couldn't load (the tracker or a component is missing or broken).</summary>
        CouldNotLoad,
        /// <summary>Atlas stopped the engine.</summary>
        Stopped
    }

    /// <summary>What a request to a logic engine came to: its answer, or why there isn't one (in a few words, for the log).</summary>
    public sealed record EngineAnswer(JObject? Reply, EngineFailure Failure, string? Why)
    {
        public static EngineAnswer Of(JObject reply) => new(reply, EngineFailure.None, null);

        public static EngineAnswer Failed(EngineFailure failure, string why) => new(null, failure, why);
    }

    /// <summary>
    /// One logic engine process: the bridge (or a test's fake engine), asked one request at a time on its standard input,
    /// answering one JSON object per line on its standard output.
    /// <list type="bullet">
    /// <item>Every request carries an id, and only an answer with that id counts: a late answer to an earlier request (one
    /// that ran out of time) or a line some code in the engine printed is never taken for it.</item>
    /// <item>A reader of its own reads and parses the output as it comes, off the main thread.</item>
    /// <item>A bridge that couldn't load says so ("boot_failed"): the request waiting fails at once instead of running
    /// out of time.</item>
    /// <item>Stopping it kills its whole process tree. One that ends by itself raises <see cref="Exited"/>, and a request
    /// waiting on it fails at once.</item>
    /// </list>
    /// Godot-free, so its tests run against the fake engine.
    /// </summary>
    public sealed class EngineProcess
    {
        private readonly Process _process;
        private readonly Action<string> _log;
        // One request at a time: the engine answers in order, and each answer is waited for with its own time limit.
        private readonly SemaphoreSlim _oneAtATime = new(1, 1);
        private readonly object _lock = new();
        private int _waitingId;
        private TaskCompletionSource<EngineAnswer>? _waiting;
        private int _nextId;
        // Why the engine can't answer any more, once it can't.
        private EngineAnswer? _ended;
        private volatile bool _stopping;
        private Task _reading = Task.CompletedTask;

        private EngineProcess(Process process, Action<string> log)
        {
            _process = process;
            _log = log;
        }

        /// <summary>
        /// Starts an engine process: tied to Atlas's lifetime, and counted under its engine folder (so setup can tell it's
        /// in use). Its error output goes to <paramref name="log"/> as it comes (a full pipe would block the engine).
        /// </summary>
        /// <exception cref="Win32Exception">The program couldn't be started.</exception>
        public static EngineProcess Start(ProcessStartInfo info, string? engineRoot, Action<string> log)
        {
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var engine = new EngineProcess(process, log);
            process.Exited += (_, _) => engine.OnExited();
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) log("PYTHON: " + e.Data);
            };
            process.Start();
            ProcessJob.Track(process, engineRoot);
            process.BeginErrorReadLine();
            engine._reading = engine.ReadAsync();
            return engine;
        }

        /// <summary>Raised (on a process thread) when the engine ends without being stopped: its exit code.</summary>
        public event Action<int>? Exited;

        /// <summary>The process's id (for tests and the log).</summary>
        public int Id => _process.Id;

        /// <summary>Whether the engine can still answer: running, not stopped, and loaded.</summary>
        public bool Running
        {
            get
            {
                lock (_lock)
                    if (_ended != null) return false;
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        /// <summary>
        /// Sends a request (an id is added) and waits up to <paramref name="timeout"/> for its answer, once any request
        /// before it has finished. A request that runs out of time leaves the engine working on it: its answer is thrown
        /// away when it comes, and the next request is read after it.
        /// </summary>
        public async Task<EngineAnswer> AskAsync(JObject request, TimeSpan timeout, CancellationToken ct = default)
        {
            await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var answer = new TaskCompletionSource<EngineAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
                int id;
                lock (_lock)
                {
                    if (_ended != null) return _ended;
                    id = ++_nextId;
                    _waitingId = id;
                    _waiting = answer;
                }
                request["id"] = id;
                try
                {
                    await _process.StandardInput.WriteLineAsync(request.ToString(Formatting.None)).ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    Forget(id);
                    return _stopping ? EngineAnswer.Failed(EngineFailure.Stopped, "the engine was stopped") : EngineAnswer.Failed(EngineFailure.Ended, "the engine closed (" + ex.Message + ")");
                }
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timer = Task.Delay(timeout, limit.Token);
                var first = await Task.WhenAny(answer.Task, timer).ConfigureAwait(false);
                await limit.CancelAsync().ConfigureAwait(false); // ends the timer when the answer won
                if (first != answer.Task)
                {
                    Forget(id);
                    ct.ThrowIfCancellationRequested();
                    return EngineAnswer.Failed(EngineFailure.TimedOut, $"no answer within {timeout.TotalSeconds:0.#} seconds");
                }
                return await answer.Task.ConfigureAwait(false);
            }
            finally
            {
                _oneAtATime.Release();
            }
        }

        /// <summary>Stops the engine: kills its whole process tree (anything it started too). A request waiting fails at once. Safe to call more than once.</summary>
        public void Stop()
        {
            _stopping = true;
            End(EngineAnswer.Failed(EngineFailure.Stopped, "the engine was stopped"));
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // It ended by itself meanwhile, or Windows refused (then Atlas's job object still ends it when Atlas closes).
                _log("Stopping the logic engine: " + ex.Message);
            }
            Async.Fire(DisposeWhenReadAsync(), "closing a stopped logic engine", tellUser: false);
        }

        private async Task DisposeWhenReadAsync()
        {
            // The reader sees the end of the output once the process is gone; then nothing uses the process any more.
            await _reading.ConfigureAwait(false);
            _process.Dispose();
        }

        private async Task ReadAsync()
        {
            string why;
            try
            {
                var output = _process.StandardOutput;
                while (await output.ReadLineAsync().ConfigureAwait(false) is { } line) Take(line.Trim());
                why = "the engine stopped";
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                why = "the engine stopped (" + ex.Message + ")";
            }
            End(EngineAnswer.Failed(EngineFailure.Ended, why));
        }

        /// <summary>One line of the engine's output: an answer (by its id), the bridge saying it couldn't load, or anything else, which is logged.</summary>
        private void Take(string line)
        {
            if (line.Length == 0) return;
            JObject? message = null;
            if (line.StartsWith('{') && line.EndsWith('}'))
            {
                try { message = JObject.Parse(line); }
                catch (JsonException) { } // not JSON after all: logged below
            }
            if (message == null)
            {
                _log("PYTHON: " + line);
                return;
            }
            if ((string?)message["event"] == "boot_failed")
            {
                _log($"The logic engine couldn't load: {message["error"]}\n{message["trace"]}");
                End(EngineAnswer.Failed(EngineFailure.CouldNotLoad, $"the engine couldn't load ({message["error"]})"));
                return;
            }
            var id = message["id"];
            TaskCompletionSource<EngineAnswer>? answer = null;
            lock (_lock)
            {
                if (_waiting != null && id?.Type == JTokenType.Integer && (int)id == _waitingId)
                {
                    answer = _waiting;
                    _waiting = null;
                }
            }
            if (answer != null) answer.TrySetResult(EngineAnswer.Of(message));
            else if (id?.Type == JTokenType.Integer) _log($"The logic engine answered request {id} late; its answer was ignored.");
            else _log("PYTHON: " + line); // printed by code in the engine, not an answer
        }

        private void Forget(int id)
        {
            lock (_lock)
                if (_waiting != null && _waitingId == id) _waiting = null;
        }

        /// <summary>The engine can't answer any more: a request waiting fails now, and later ones at once (the first reason stands).</summary>
        private void End(EngineAnswer why)
        {
            TaskCompletionSource<EngineAnswer>? waiting;
            EngineAnswer reason;
            lock (_lock)
            {
                _ended ??= why;
                reason = _ended;
                waiting = _waiting;
                _waiting = null;
            }
            waiting?.TrySetResult(reason);
        }

        private void OnExited()
        {
            if (_stopping) return;
            int code = -1;
            try { code = _process.ExitCode; }
            catch (InvalidOperationException) { } // not known
            _log($"The logic engine process exited unexpectedly (exit code {code}).");
            try { Exited?.Invoke(code); }
            catch (Exception ex) { Async.Report(ex, "handling the logic engine stopping", tellUser: false); }
        }
    }
}
