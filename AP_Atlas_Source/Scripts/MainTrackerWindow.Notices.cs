#nullable disable
using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;

/// <summary>Telling the user things: toasts, notices about failures and recoveries, and the logs.</summary>
public partial class MainTrackerWindow : Control, AP_Atlas.UI.IPropertiesHost
{
    private void OnLogMessageReceived(string msg, string level)
    {
        // Log lines can arrive from other threads (and while Atlas is closing, after the views are freed: then they're dropped).
        // Diagnostics (frame hitches) are for the Debug Log; the System Log is what Atlas did, in plain words.
        if (level != AP_Atlas.Core.Logger.DiagnosticLevel) _systemLog?.Append(msg);
        _debugLog?.Append(msg);
    }

    /// <summary>The status bar's message: plain words about what Atlas is doing or just did.</summary>
    private void ShowStatus(string text)
    {
        if (_globalStatusLabel != null) _globalStatusLabel.Text = text;
        if (UiTestRequested && _statusShown.Count < 10000) _statusShown.Add(text); // the UI test reads every message, not just the last
    }

    /// <summary>Every status bar message shown, in order (the UI test only).</summary>
    private readonly List<string> _statusShown = new();

    /// <summary>A frame hitch, from the hitch monitor: the status bar says so in developer mode; the Debug Log has it either way.</summary>
    private void OnHitch(string message)
    {
        if (_appSettings != null && _appSettings.DeveloperMode) ShowStatus(message);
    }

    /// <summary>The status bar's right end: how many slots are connected (nothing while none is).</summary>
    private void ShowConnectedCount()
    {
        if (_statusConnectedLabel == null) return;
        int connected = ActiveSlotNodes().OfType<SlotTrackerControl>().Count();
        string text = connected == 0 ? "" : connected == 1 ? Tr("1 slot connected") : Tr("{0} slots connected").Replace("{0}", connected.ToString());
        if (_statusConnectedLabel.Text != text) _statusConnectedLabel.Text = text;
    }
    private bool _uiReady;
    private readonly List<(string, Godot.Color)> _pendingNotices = new List<(string, Godot.Color)>();
    private readonly Dictionary<string, System.DateTime> _lastSaveFailureToast = new Dictionary<string, System.DateTime>();
    private void Notice(string message, Godot.Color color)
    {
        AP_Atlas.UI.Ui.Defer(this, () =>
        {
            if (_uiReady) ShowToast(message, color);
            else _pendingNotices.Add((message, color));
        });
    }
    private void OnFileRecovered(string path, string what) =>
        Notice($"{System.IO.Path.GetFileName(path)} {what}.", AP_Atlas.Core.ThemeColors.Warning);
    private void OnSaveFailed(string file, string reason)
    {
        // A full disk fails every save: tell the user once a minute, not on every keystroke.
        lock (_lastSaveFailureToast)
        {
            // On the steady clock: putting the PC's clock back can't silence these for that long.
            var now = AP_Atlas.Core.SteadyClock.UtcNow;
            if (_lastSaveFailureToast.TryGetValue(file, out var last) && (now - last).TotalSeconds < 60) return;
            _lastSaveFailureToast[file] = now;
        }
        Notice($"Couldn't save {file}: {reason}. Check free disk space and folder permissions.", AP_Atlas.Core.ThemeColors.Error);
    }
    private readonly Dictionary<string, System.DateTime> _lastFailureNotice = new Dictionary<string, System.DateTime>();
    /// <summary>
    /// Work nobody awaited failed (any thread). Says so in plain words, at most once every 10 minutes for the same work
    /// (a check that fails on every refresh would otherwise repeat); the details are in the System Log.
    /// </summary>
    private void OnBackgroundWorkFailed(string doing, System.Exception error)
    {
        lock (_lastFailureNotice)
        {
            var now = AP_Atlas.Core.SteadyClock.UtcNow;
            if (_lastFailureNotice.TryGetValue(doing, out var last) && now - last < System.TimeSpan.FromMinutes(10)) return;
            _lastFailureNotice[doing] = now;
        }
        Notice($"Something went wrong while {doing}. Atlas carries on; the details are in the System Log.", AP_Atlas.Core.ThemeColors.Error);
    }
    /// <summary>
    /// Older damaged copies of profiles.json (set aside as ".corrupt-…") can still hold room passwords in plain text from
    /// before they were encrypted: offers once per session to delete them.
    /// </summary>
    private void OfferToDeletePlainTextPasswordCopies()
    {
        var copies = DataManager.PlainTextPasswordCopies();
        if (copies.Count == 0) return;
        AP_Atlas.UI.Ui.Defer(this, () => ShowToast($"{copies.Count} old damaged cop{(copies.Count == 1 ? "y" : "ies")} of your profiles still hold room passwords in plain text. Delete {(copies.Count == 1 ? "it" : "them")}?",
            AP_Atlas.Core.ThemeColors.Warning, "Delete", () =>
            {
                int deleted = 0;
                foreach (var file in copies)
                {
                    try { System.IO.File.Delete(file); deleted++; }
                    catch (System.Exception ex) { AP_Atlas.Core.Logger.LogWarning($"Couldn't delete {System.IO.Path.GetFileName(file)}: {ex.Message}"); }
                }
                LogToSystem($"Deleted {deleted} old damaged cop{(deleted == 1 ? "y" : "ies")} of profiles.json that held plain-text passwords.");
            }));
    }
    /// <summary>
    /// Says at startup whether logic can run. Atlas never searches the PC for an Archipelago install by itself: the user
    /// chooses one, or presses Find in Atlas Engine (which asks first).
    /// </summary>
    private void ReportEngineAtStartup()
    {
        var engine = AP_Atlas.Core.EngineSetup.AtlasEngine.Current;
        string problem = AP_Atlas.Core.EngineSetup.AtlasEngine.ProblemWith(engine);
        if (problem == null)
        {
            LogToSystem($"Logic engine: {engine.Describe()}.", "gray");
            return;
        }
        LogToSystem($"Logic engine: {problem} Settings → Atlas Engine sets it up.", "orange");
        AP_Atlas.UI.Ui.Defer(this, () => ShowToast("Logic needs the Atlas Engine. Atlas can set it up for you: about 55 MB to download (160 MB on disk), no installer.", AP_Atlas.Core.ThemeColors.Warning, "Set up", OpenEngineSetup));
    }
    private void ShowToast(string message, Godot.Color color) => ShowToast(message, color, null, null);
    /// <summary>A card on the alert feed (and a line in its history); with an action it shows a button and stays up longer.</summary>
    private void ShowToast(string message, Godot.Color color, string actionText, System.Action action) => _alerts.Show(message, color, actionText, action);

    /// <summary>A line in the System Log: plain text, shown as written, in a colour if given ("orange", "#8A2BE2").</summary>
    private void LogToSystem(string message, string color = null)
    {
        AP_Atlas.Core.Logger.LogInfo(message, color);
    }
    private void LogToDebug(string msg, string slotName = "")
    {
        // Slot debug lines (engine starts, failures) go to the log file too, for diagnosing problems after the fact.
        AP_Atlas.Core.Logger.LogDebug(string.IsNullOrEmpty(slotName) ? msg : $"[{slotName}] {msg}");
        // Plain text, shown as written: slot names and messages can hold text from outside Atlas.
        string time = AP_Atlas.Core.Logger.DisplayClock().ToString("HH:mm:ss");
        string prefix = string.IsNullOrEmpty(slotName) ? "" : AP_Atlas.Core.Bbcode.Colored($"[{slotName}]", "orange") + " ";
        _debugLog?.Append($"{prefix}{AP_Atlas.Core.Bbcode.Colored($"[{time}]", "gray")} {AP_Atlas.Core.Bbcode.Escape(AP_Atlas.Core.Logger.Shown(msg))}\n");
    }
}
