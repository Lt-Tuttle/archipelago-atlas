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

        /// <summary>
        /// A file another program holds (a virus scan, a sync tool) is read again until it's free, and is never taken for
        /// damage: no .corrupt copy, no backup restored over it. If it stays held, Atlas uses the fallback and refuses to
        /// save over it until it reads again.
        /// </summary>
        private static async Task HeldFilesAreNeverMistakenOrOverwritten()
        {
            string path = Scratch("held.json");
            SafeFile.WriteJson(path, new Sample { Version = 1 });
            SafeFile.WriteJson(path, new Sample { Version = 2 });
            Expect(File.Exists(path + ".bak"), "setup: the older version is the backup");
            string folder = Path.GetDirectoryName(path);
            bool SetAside() => Directory.GetFiles(folder, "held.json.corrupt-*").Length > 0;
            var recovered = new List<string>();
            void OnRecovered(string file, string what) { lock (recovered) recovered.Add(what); }
            SafeFile.Recovered += OnRecovered;
            try
            {
                // Held briefly: read again until it's free, and get the real contents.
                var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var release = Task.Run(async () => { await Task.Delay(250); holder.Dispose(); });
                var read = await Task.Run(() => SafeFile.ReadJson(path, () => new Sample { Version = -1 }));
                await release;
                Expect(read.Version == 2, $"a briefly held file reads as it is (version 2), not {read.Version}");
                Expect(recovered.Count == 0, "a briefly held file isn't damage: " + string.Join("; ", recovered));
                Expect(!SetAside(), "a held file is never set aside as damaged");

                // Held throughout: the fallback, a message, and no saving over it.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var fallback = await Task.Run(() => SafeFile.ReadJson(path, () => new Sample { Version = -1 }));
                    Expect(fallback.Version == -1, "a file that stays held gives the fallback");
                }
                Expect(recovered.Count == 1 && recovered[0].Contains("another program"), "the user is told why it couldn't be read: " + string.Join("; ", recovered));
                bool refused = false;
                try { SafeFile.WriteJson(path, new Sample { Version = 99 }); }
                catch (IOException) { refused = true; }
                Expect(refused, "a file that couldn't be read must not be saved over");
                Expect(Newtonsoft.Json.JsonConvert.DeserializeObject<Sample>(File.ReadAllText(path)).Version == 2, "the file on disk is still the real one");
                Expect(!SetAside(), "still nothing set aside as damaged");

                // Once it reads again, saving works again.
                Expect(SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version == 2, "it reads once it's free");
                SafeFile.WriteJson(path, new Sample { Version = 3 });
                Expect(SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version == 3, "and saving works again");
            }
            finally
            {
                SafeFile.Recovered -= OnRecovered;
            }
        }

        /// <summary>SafeFile.Delete removes the backup and any temp file too, so a deleted file doesn't come back on the next read.</summary>
        private static void DeletedFilesStayDeleted()
        {
            string path = Scratch("gone.json");
            SafeFile.WriteJson(path, new Sample { Version = 1 });
            SafeFile.WriteJson(path, new Sample { Version = 2 });
            File.WriteAllText(path + ".tmp", "left by an interrupted save");
            Expect(File.Exists(path + ".bak"), "setup: a backup exists");
            SafeFile.Delete(path);
            Expect(!File.Exists(path) && !File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), "the file, its backup and its temp file are all gone");
            Expect(SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version == -1, "a deleted file doesn't come back from its backup");
            SafeFile.Delete(path); // deleting what's already gone is fine
        }
    }
}
