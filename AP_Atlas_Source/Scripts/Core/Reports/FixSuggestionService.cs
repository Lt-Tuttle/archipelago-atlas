#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using AP_Atlas.Core.Updates;

namespace AP_Atlas.Core.Reports
{
    /// <summary>
    /// Sends the user's pack fixes to Atlas's developer when they say so (<see cref="Permissions.FixSuggestions"/>): the
    /// Pack Doctor's dialog shows exactly what goes (<see cref="FixSuggestions.Document"/>, scrubbed) and its three answers
    /// are the permission's. A copy of each suggestion sent stays in the reports folder. Set up by the window; a build
    /// without the suggestions' address can't send (<see cref="Available"/>).
    /// </summary>
    public static class FixSuggestionService
    {
        public const string Folder = "fix-suggestions";

        private static SentryDsn? _dsn;
        private static Func<Scrubber> _scrubber = () => new Scrubber(null);
        private static ReportContext? _context;
        private static AppSettings? _settings;
        private static string? _reportsDir;

        /// <summary>A card: the text and how serious.</summary>
        public static Action<string, NoticeKind> Card { get; set; } = (_, _) => { };

        /// <summary>Whether this build can send suggestions at all.</summary>
        public static bool Available => _dsn != null && _context != null;

        /// <summary>Whether the user chose "Always send".</summary>
        public static bool AlwaysAllowed => _settings != null && Permissions.IsAlwaysAllowed(_settings, Permissions.FixSuggestions);

        /// <summary>Set up for this run (the window does, with the crash reports' scrubber and context).</summary>
        public static void Configure(string dsnText, Func<Scrubber> scrubber, ReportContext context, AppSettings settings, string reportsDir)
        {
            _dsn = SentryDsn.TryParse(dsnText, out var dsn) ? dsn : null;
            if (_dsn == null && !string.IsNullOrWhiteSpace(dsnText)) Logger.LogWarning("The build's fix suggestions address isn't a DSN; suggestions can't be sent.");
            _scrubber = scrubber;
            _context = context;
            _settings = settings;
            _reportsDir = reportsDir;
        }

        /// <summary>The text the dialog shows: exactly what would be sent.</summary>
        public static string Preview(FixSuggestion suggestion) =>
            FixSuggestions.Document(suggestion, _context?.Release ?? AtlasVersion.Full, _scrubber().Scrub).ToString(Newtonsoft.Json.Formatting.Indented);

        /// <summary>
        /// The user's answer in the dialog: Send once and Always send record the permission and send; Don't send records the
        /// "no" for this session. Says how it went on a card.
        /// </summary>
        public static async Task AnswerAsync(ReportDecision decision, FixSuggestion suggestion)
        {
            if (_settings == null) return;
            switch (decision)
            {
                case ReportDecision.SendOnce: Permissions.AllowForSession(Permissions.FixSuggestions); break;
                case ReportDecision.SendAlways: Permissions.SetAlways(_settings, Permissions.FixSuggestions, null, true); break;
                default:
                    Permissions.DenyForSession(Permissions.FixSuggestions);
                    Logger.LogInfo("Pack fix suggestion not sent, at the user's choice.");
                    return;
            }
            await SendAsync(suggestion).ConfigureAwait(true);
        }

        /// <summary>Sends a suggestion when the permission allows it; true when it went.</summary>
        public static async Task<bool> SendAsync(FixSuggestion suggestion)
        {
            if (_dsn == null || _context == null || _settings == null || !Permissions.IsAllowed(_settings, Permissions.FixSuggestions)) return false;
            var scrubber = _scrubber();
            var response = await FixSuggestions.SendAsync(_dsn, suggestion, _context, scrubber.Scrub).ConfigureAwait(true);
            if (!response.Ok)
            {
                Card($"Your fixes couldn't be sent ({response.Message}). Nothing was lost: they're still in the Pack Doctor.", NoticeKind.Warning);
                return false;
            }
            KeepCopy(suggestion, scrubber);
            Card($"Your {suggestion.Pairs.Count} fix{(suggestion.Pairs.Count == 1 ? "" : "es")} for {suggestion.Game} went to Atlas's developer. Thank you: they help every pack.", NoticeKind.Info);
            return true;
        }

        /// <summary>The copy kept in the reports folder: what was sent, as sent.</summary>
        private static void KeepCopy(FixSuggestion suggestion, Scrubber scrubber)
        {
            if (_reportsDir == null) return;
            try
            {
                string dir = Path.Combine(_reportsDir, Folder);
                Directory.CreateDirectory(dir);
                string name = string.Concat(suggestion.Game.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
                string path = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.json"); // wall clock: a file's name
                SafeFile.WriteAllText(path, FixSuggestions.Document(suggestion, _context?.Release ?? AtlasVersion.Full, scrubber.Scrub).ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning("The copy of the sent fixes couldn't be kept: " + ex.Message);
            }
        }
    }
}
