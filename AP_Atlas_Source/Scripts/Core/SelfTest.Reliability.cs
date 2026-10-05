using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core
{
    /// <summary>Self-tests for the safety nets: unawaited work, file handling and saving.</summary>
    public static partial class SelfTest
    {
        /// <summary>
        /// Async.Fire: a failure is reported once with what was being done (before or after the first await, or already
        /// failed); cancelling isn't a failure; routine work is only logged; success and "nothing" report nothing.
        /// </summary>
        private static async Task UnawaitedWorkIsReported()
        {
            var reported = new List<(string Doing, Exception Error)>();
            void OnFailed(string doing, Exception error) { lock (reported) reported.Add((doing, error)); }
            Async.Failed += OnFailed;
            try
            {
                Async.Fire(Task.Run(async () => { await Task.Delay(30); throw new InvalidOperationException("late failure"); }), "testing a late failure");
                Async.Fire(() => throw new ArgumentException("early failure"), "testing an early failure");
                Async.Fire(Task.FromException(new IOException("already failed")), "testing a failed task");
                Async.Fire(Task.FromCanceled(new CancellationToken(true)), "testing a cancellation");
                Async.Fire(Task.Run(() => throw new OperationCanceledException()), "testing a cancellation from inside");
                Async.Fire(Task.Run(() => throw new InvalidOperationException("routine")), "testing routine work", tellUser: false);
                Async.Fire(Task.CompletedTask, "testing success");
                Async.Fire((Task)null, "testing nothing");

                for (int i = 0; i < 100; i++)
                {
                    lock (reported) if (reported.Count >= 3) break;
                    await Task.Delay(20);
                }
                // Give anything that shouldn't be reported time to arrive as well.
                await Task.Delay(200);
                List<(string Doing, Exception Error)> seen;
                lock (reported) seen = reported.ToList();
                Expect(seen.Count == 3, $"3 failures should be reported, got {seen.Count}: {string.Join(", ", seen.Select(r => r.Doing))}");
                Expect(seen.Any(r => r.Doing == "testing a late failure" && r.Error is InvalidOperationException && r.Error.Message == "late failure"),
                    "a failure after an await is reported with its own exception (not wrapped)");
                Expect(seen.Any(r => r.Doing == "testing an early failure" && r.Error is ArgumentException), "a throw before the first await is reported");
                Expect(seen.Any(r => r.Doing == "testing a failed task" && r.Error is IOException), "an already-failed task is reported");
            }
            finally
            {
                Async.Failed -= OnFailed;
            }
        }
    }
}
