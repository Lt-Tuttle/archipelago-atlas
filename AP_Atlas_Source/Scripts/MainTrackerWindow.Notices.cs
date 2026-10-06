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
        _systemLog?.Append(msg);
        _debugLog?.Append(msg);
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
        Notice($"{System.IO.Path.GetFileName(path)} {what}.", Godot.Colors.Orange);
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
        Notice($"Couldn't save {file}: {reason}. Check free disk space and folder permissions.", Godot.Colors.Salmon);
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
        Notice($"Something went wrong while {doing}. Atlas carries on; the details are in the System Log.", Godot.Colors.Salmon);
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
            Godot.Colors.Orange, "Delete", () =>
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
        AP_Atlas.UI.Ui.Defer(this, () => ShowToast("Logic needs the Atlas Engine. Atlas can set it up for you: about 55 MB to download (160 MB on disk), no installer.", Godot.Colors.Orange, "Set up", OpenEngineSetup));
    }
    private void ShowToast(string message, Godot.Color color) => ShowToast(message, color, null, null);
    /// <summary>A toast; with an action it shows a button and stays up longer.</summary>
    private void ShowToast(string message, Godot.Color color, string actionText, System.Action action)
    {
        var toastPanel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = new Godot.Color(0.1f, 0.1f, 0.1f, 0.9f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = color,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        toastPanel.AddThemeStyleboxOverride("panel", style);
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 10);
        var circle = new ColorRect { CustomMinimumSize = new Godot.Vector2(10, 10), Color = color, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var label = new Label { Text = message };
        hbox.AddChild(circle);
        hbox.AddChild(label);
        var canvas = new CanvasLayer { Layer = 100 };
        if (action != null)
        {
            var button = new Button { Text = actionText ?? "Open", FocusMode = FocusModeEnum.None };
            button.Pressed += () => { action(); canvas.QueueFree(); };
            hbox.AddChild(button);
        }
        toastPanel.AddChild(hbox);
        canvas.AddChild(toastPanel);
        AddChild(canvas);
        toastPanel.Modulate = new Godot.Color(1, 1, 1, 0);
        SetFontSizeRecursive(toastPanel, _appSettings.GlobalFontSize);
        // Wait a frame to let Godot calculate the minimum size
        CallDeferred(nameof(AnimateToast), toastPanel, canvas, action != null ? 10.0f : 3.0f);
    }
    private void AnimateToast(PanelContainer toastPanel, CanvasLayer canvas, float hold)
    {
        var winSize = GetWindow().Size;
        var panelSize = toastPanel.Size;
        // Position at bottom-right, slightly offset
        toastPanel.Position = new Godot.Vector2(winSize.X - panelSize.X - 20, winSize.Y - panelSize.Y - 20);
        var tween = CreateTween();
        tween.TweenProperty(toastPanel, "modulate", new Godot.Color(1, 1, 1, 1), 0.3f).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenInterval(hold);
        // The visual check's pictures must not depend on the moment they're taken: its toasts stay up.
        if (VisualCheckRequested) return;
        tween.TweenProperty(toastPanel, "modulate", new Godot.Color(1, 1, 1, 0), 0.5f).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenCallback(Callable.From(() => canvas.QueueFree()));
    }
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
