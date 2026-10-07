using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core.Updates;

namespace AP_Atlas.Core.Reports
{
    /// <summary>What the user chose in the crash report dialog.</summary>
    public enum ReportDecision
    {
        SendOnce,
        SendAlways,
        DontSend
    }

    /// <summary>
    /// Crash reports, with the user's say at every step: on a start after a problem, a card offers a report; the dialog
    /// shows exactly what would be sent (<see cref="CrashReporter.Preview"/>) with a box for a note; Send once, Always send
    /// and Don't send are the crash-report permission's three answers (Settings → Privacy &amp; permissions takes "always"
    /// back). With the permission kept, reports go without asking, and a card says so. Help → Report a problem writes the
    /// scrubbed bundle for an issue (<see cref="ProblemBundle"/>). A build without a DSN can't report; the bundle still works.
    /// The window gives the cards, the dialog and the system information.
    /// </summary>
    public sealed class CrashReportService
    {
        public const string ReportsFolder = "reports";
        public const string LogsFolder = "logs";

        private readonly AppSettings _settings;
        private readonly Func<IEnumerable<MultiworldProfile>> _profiles;
        private readonly string _dataDir;
        private readonly SentryDsn? _dsn;
        private readonly ReportContext _context;
        private bool _busy;

        /// <param name="dsnText">Where reports go (Sentry's DSN); "" or not a DSN: this build can't report.</param>
        public CrashReportService(AppSettings settings, Func<IEnumerable<MultiworldProfile>> profiles, string dataDir, string dsnText, ReportContext context)
        {
            _settings = settings;
            _profiles = profiles;
            _dataDir = dataDir;
            _context = context;
            if (SentryDsn.TryParse(dsnText, out var dsn)) _dsn = dsn;
            else if (!string.IsNullOrWhiteSpace(dsnText)) Logger.LogWarning("The build's crash report address isn't a DSN; crash reporting is off.");
        }

        /// <summary>Whether the crash-report permission is allowed now (kept, or for this session).</summary>
        public Func<bool> PermissionAllowed { get; set; } = () => false;
        /// <summary>Whether the user chose "Always send".</summary>
        public Func<bool> PermissionAlways { get; set; } = () => false;
        /// <summary>A card: the text, how serious, and an action with its button text.</summary>
        public Action<string, NoticeKind, string?, Action?> Card { get; set; } = (_, _, _, _) => { };
        /// <summary>Shows the report (exactly what would be sent) and asks: the decision and the user's note come back.</summary>
        public Action<string, Action<ReportDecision, string?>> ShowDialog { get; set; } = (_, decided) => decided(ReportDecision.DontSend, null);
        /// <summary>The system information About shows (for the bundle; scrubbed like the rest).</summary>
        public Func<string> SystemInfo { get; set; } = () => "";
        /// <summary>Something the Settings page shows changed.</summary>
        public event Action? Changed;

        /// <summary>Whether this build can send reports at all.</summary>
        public bool Available => _dsn != null;
        public string LogsDir => Path.Combine(_dataDir, LogsFolder);
        public string ReportsDir => Path.Combine(_dataDir, ReportsFolder);
        public bool Busy => _busy;

        /// <summary>The scrubber for now: the Windows user name, and the servers and slot names of the user's multiworlds.</summary>
        public Scrubber Scrubber()
        {
            var profiles = _profiles().ToList();
            return new Scrubber(System.Environment.UserName, profiles.Select(p => p.ServerUrl), profiles.SelectMany(p => p.Slots));
        }

        private CrashReporter Reporter() => new CrashReporter(LogsDir, ReportsDir, _dsn!, Scrubber(), _context);

        /// <summary>The crash files still to be offered (none in a build that can't report).</summary>
        public IReadOnlyList<string> Pending() => _dsn == null ? Array.Empty<string>() : Reporter().PendingFiles();

        /// <summary>
        /// On a start: a problem last time is offered as a card (or sent straight away with the permission kept). Nothing
        /// is sent without the user's say.
        /// </summary>
        public async Task OfferAsync()
        {
            if (_dsn == null) return;
            var pending = Pending();
            if (pending.Count == 0) return;
            if (PermissionAlways())
            {
                await SendAllAsync(pending, null).ConfigureAwait(true);
                return;
            }
            string text = pending.Count == 1
                ? "Atlas had a problem last time it ran. Send a report to Atlas's developer? You see the report first; nothing is sent until you say so."
                : $"Atlas had {pending.Count} problems since reports were last sent. Send them to Atlas's developer? You see a report first; nothing is sent until you say so.";
            Card(text, NoticeKind.Warning, "See the report", () => Ask(pending));
        }

        /// <summary>Shows the newest pending report and acts on the answer (for every pending file).</summary>
        public void Ask(IReadOnlyList<string> pending)
        {
            if (_dsn == null || pending.Count == 0) return;
            string preview;
            try { preview = Reporter().Preview(pending[0], null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning("A crash report couldn't be read: " + ex.Message);
                return;
            }
            ShowDialog(preview, (decision, note) =>
            {
                switch (decision)
                {
                    case ReportDecision.SendOnce:
                        Permissions.AllowForSession(Permissions.CrashReports);
                        Async.Fire(SendAllAsync(pending, note), "sending a crash report", tellUser: true);
                        break;
                    case ReportDecision.SendAlways:
                        Permissions.SetAlways(_settings, Permissions.CrashReports, null, true);
                        Async.Fire(SendAllAsync(pending, note), "sending a crash report", tellUser: true);
                        break;
                    default:
                        Permissions.DenyForSession(Permissions.CrashReports);
                        Reporter().Dismiss(pending);
                        Logger.LogInfo($"{pending.Count} crash report(s) not sent, at the user's choice.");
                        break;
                }
                Changed?.Invoke();
            });
        }

        /// <summary>Sends every file given (with the user's note on each), then says how it went.</summary>
        public async Task SendAllAsync(IReadOnlyList<string> files, string? note)
        {
            if (_dsn == null || _busy || !PermissionAllowed()) return;
            _busy = true;
            Changed?.Invoke();
            int sent = 0;
            string? failure = null;
            try
            {
                var reporter = Reporter();
                foreach (string file in files)
                {
                    var response = await reporter.SendAsync(file, note).ConfigureAwait(true);
                    if (response.Ok) sent++;
                    else failure ??= response.Message;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex.Message;
            }
            finally
            {
                _busy = false;
                Changed?.Invoke();
            }
            if (sent > 0 && failure == null) Card(sent == 1 ? "The crash report was sent. Thank you." : $"{sent} crash reports were sent. Thank you.", NoticeKind.Info, null, null);
            else if (sent > 0) Card($"{sent} crash report(s) were sent; another couldn't be ({failure}). Atlas tries again next time.", NoticeKind.Warning, null, null);
            else Card($"The crash report couldn't be sent ({failure}). Atlas tries again next time.", NoticeKind.Warning, null, null);
        }

        /// <summary>Help → Report a problem: the scrubbed bundle's path, or null (and a card) when it couldn't be written.</summary>
        public string? WriteBundle()
        {
            try
            {
                string path = ProblemBundle.Write(ReportsDir, LogsDir, Scrubber(), SystemInfo(), DateTime.Now); // wall clock: a file's name
                Logger.LogInfo("A problem report was written: " + Path.GetFileName(path));
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning("The problem report couldn't be written: " + ex.Message);
                Card("The report couldn't be written: " + ex.Message, NoticeKind.Error, null, null);
                return null;
            }
        }

        /// <summary>
        /// Developer mode: writes a test crash file and offers it like any other, so the whole path (the dialog, the send, the
        /// project on Sentry) can be checked by hand.
        /// </summary>
        public void OfferTest()
        {
            if (_dsn == null)
            {
                Card("This build has no crash report address, so there's nothing to test.", NoticeKind.Warning, null, null);
                return;
            }
            try
            {
                Directory.CreateDirectory(LogsDir);
                string path = Path.Combine(LogsDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt"); // wall clock: a file's name, as CrashGuard names them
                SafeFile.WriteAllText(path, $"{DateTime.Now:O}\nA test report (sent on purpose from Help)\n\nSystem.InvalidOperationException: This is a test report from The Archipelago Atlas; nothing went wrong.\n   at AP_Atlas.Core.Reports.CrashReportService.OfferTest()\n"); // wall clock: the report's time
                Ask(new[] { path });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Card("The test report couldn't be written: " + ex.Message, NoticeKind.Error, null, null);
            }
        }
    }
}
