#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AP_Atlas.Core;
using Godot;
using Color = Godot.Color;
using Logger = AP_Atlas.Core.Logger;

namespace AP_Atlas.UI
{
    /// <summary>What the Properties panel needs from the main window.</summary>
    public interface IPropertiesHost
    {
        AppSettings Settings { get; }
        SlotTrackerControl SelectedSlot { get; }
        IEnumerable<SlotTrackerControl> ConnectedSlots { get; }
        IReadOnlyList<MultiworldProfile> Profiles { get; }
        bool IsSlotConnecting(string profileId, string slotName);
        void SelectSlot(SlotTrackerControl slot);
        void ConnectSlot(string profileId, string slotName);
        void DisconnectSlot(string profileId, string slotName);
        /// <summary>Switches to a tool's tab (a slot tool shows the selected slot).</summary>
        void ShowTool(Tool tool);
        void SelectProfile(string profileId);
        void Toast(string message, Color color);
        void OpenPackDoctor(string zipPath, string startTab = null);
        /// <summary>Cheese Tracker: linked trackers, slot statuses and changes.</summary>
        AP_Atlas.Core.CheeseTracker.CheeseTrackerService Cheese { get; }
        /// <summary>The Cheese Tracker tab's Settings page.</summary>
        void OpenCheeseSettings();
        /// <summary>The Cheese Tracker tab showing a multiworld (null: "My slots").</summary>
        void ShowCheeseTab(string profileId);
    }

    /// <summary>
    /// The PROPERTIES sidebar: every detail about whatever was last selected anywhere in the app, with
    /// back/forward history, quick actions, collapsible sections, and live refresh. Content is built per
    /// selection by the Build* methods in PropertiesPanel.Details.cs.
    /// </summary>
    public partial class PropertiesPanel : VBoxContainer
    {
        private readonly IPropertiesHost _host;

        private readonly List<InspectTarget> _history = new List<InspectTarget>();
        private int _historyIndex = -1;
        private const int MaxHistory = 100;

        private Button _backButton;
        private Button _forwardButton;
        private Label _kindLabel;
        private ScrollContainer _scroll;
        private VBoxContainer _content;

        // Per-render state.
        private int _renderGeneration;
        private readonly List<Action> _linkActions = new List<Action>();
        private StringBuilder _plainText = new StringBuilder();
        private VBoxContainer _currentSectionBody;

        private Timer _refreshTimer;
        private bool _refreshPending;

        public PropertiesPanel(IPropertiesHost host)
        {
            _host = host;
            Name = "PropertiesPanel";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddThemeConstantOverride("separation", 6);
        }

        /// <summary>The target on screen, or null for the selected slot's summary.</summary>
        public InspectTarget Current => _historyIndex >= 0 && _historyIndex < _history.Count ? _history[_historyIndex] : null;

        public override void _Ready()
        {
            var nav = new HBoxContainer();
            nav.AddThemeConstantOverride("separation", 2);
            AddChild(nav);
            _backButton = Kit.Button("◀", "Back (Alt+Left)", () => Navigate(-1), flat: true);
            _forwardButton = Kit.Button("▶", "Forward (Alt+Right)", () => Navigate(1), flat: true);
            nav.AddChild(_backButton);
            nav.AddChild(_forwardButton);
            _kindLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, VerticalAlignment = VerticalAlignment.Center };
            _kindLabel.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            nav.AddChild(_kindLabel);
            nav.AddChild(Kit.Button("⌂", "Show the selected slot's summary", () => Inspect(null), flat: true));
            nav.AddChild(Kit.Button("⧉", "Copy everything shown (Ctrl+Shift+C)", CopyAll, flat: true));

            _scroll = new ScrollContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            AddChild(_scroll);

            _refreshTimer = new Timer { OneShot = true, WaitTime = 0.4 };
            _refreshTimer.Timeout += () => { if (_refreshPending) Render(keepScroll: true); };
            AddChild(_refreshTimer);

            // Made while the panel is in the window, again after it's moved (docking, pop-outs), and removed while it isn't.
            AddChild(new TreeSubscriptions()
                .On(() => Inspector.Requested += Inspect, () => Inspector.Requested -= Inspect)
                .On(() => Annotations.Changed += QueueRefresh, () => Annotations.Changed -= QueueRefresh));
            Render(keepScroll: false);
        }

        // =====================================================================
        // Selection and history
        // =====================================================================

        /// <summary>Shows a target and records it in history. Null shows the selected slot's summary.</summary>
        public void Inspect(InspectTarget target)
        {
            if (target != null && Current != null && Current.Key == target.Key)
            {
                Render(keepScroll: true);
                return;
            }
            if (target != null)
            {
                // Selecting something new drops any "forward" entries, like a browser.
                if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                _history.Add(target);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
                _historyIndex = _history.Count - 1;
            }
            else
            {
                _historyIndex = -1;
            }
            Render(keepScroll: false);
        }

        private void Navigate(int delta)
        {
            int next = _historyIndex + delta;
            if (_historyIndex == -1 && delta < 0) next = _history.Count - 1; // from the summary, Back returns to the last item
            if (next < 0 || next >= _history.Count) return;
            _historyIndex = next;
            Render(keepScroll: false);
        }

        private SlotTrackerControl _lastSelectedSlot;

        /// <summary>Called on every view refresh: the summary follows the selected slot; a specific selection stays.</summary>
        private bool _selectionRenderQueued;

        public void OnSelectedSlotChanged(SlotTrackerControl selected)
        {
            if (selected == _lastSelectedSlot) return;
            _lastSelectedSlot = selected;
            if (Current != null || _selectionRenderQueued) return;
            // Next frame: the window shows the newly selected slot first, then its summary here, each in a frame of its own.
            _selectionRenderQueued = true;
            Ui.NextFrame(this, () =>
            {
                _selectionRenderQueued = false;
                if (Current == null) Render(keepScroll: false);
            }, "showing the selected slot in Properties");
        }

        /// <summary>Live data changed somewhere; refresh soon, coalescing bursts.</summary>
        public void QueueRefresh()
        {
            _refreshPending = true;
            if (_refreshTimer != null && _refreshTimer.IsInsideTree() && _refreshTimer.IsStopped()) _refreshTimer.Start();
        }

        // =====================================================================
        // Shortcuts
        // =====================================================================

        public override void _Input(InputEvent @event)
        {
            if (!IsVisibleInTree()) return;
            if (@event is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.Xbutton1) { Navigate(-1); AcceptEvent(); }
                else if (mb.ButtonIndex == MouseButton.Xbutton2) { Navigate(1); AcceptEvent(); }
                return;
            }
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            if (key.AltPressed && key.Keycode == Key.Left) { Navigate(-1); AcceptEvent(); return; }
            if (key.AltPressed && key.Keycode == Key.Right) { Navigate(1); AcceptEvent(); return; }
            if (key.CtrlPressed && key.ShiftPressed && key.Keycode == Key.C) { CopyAll(); AcceptEvent(); return; }

            // F toggles a flag, but never while typing.
            if (key.Keycode == Key.F && !key.CtrlPressed && !key.AltPressed && !key.ShiftPressed && !IsTyping())
            {
                if (ToggleFlagShortcut()) AcceptEvent();
            }
        }

        private bool IsTyping()
        {
            var focus = GetViewport()?.GuiGetFocusOwner();
            return focus is LineEdit || focus is TextEdit;
        }

        private void CopyAll()
        {
            string text = _plainText.ToString().Trim();
            if (text.Length == 0) return;
            DisplayServer.ClipboardSet(text);
            _host.Toast("Copied properties to the clipboard", ThemeColors.TextSubtle);
        }

        // =====================================================================
        // Rendering framework
        // =====================================================================

        private void Render(bool keepScroll)
        {
            _refreshPending = false;
            if (_scroll == null) return;
            // Don't pull the rug out from under someone typing a note; refresh once they leave the field.
            if (keepScroll && IsTyping() && GetViewport()?.GuiGetFocusOwner() is Control f && IsAncestorOf(f))
            {
                _refreshPending = true;
                f.FocusExited -= OnEditorFocusExited;
                f.FocusExited += OnEditorFocusExited;
                return;
            }

            using var __perf = PerfMonitor.Measure("Properties: render");
            int scrollY = keepScroll ? _scroll.ScrollVertical : 0;
            _renderGeneration++;
            _linkActions.Clear();
            _plainText = new StringBuilder();
            _currentSectionBody = null;

            var old = _content;
            _content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _content.AddThemeConstantOverride("separation", 6);

            try
            {
                BuildFor(Current);
            }
            catch (Exception ex)
            {
                AddText($"[color=salmon]Could not show these properties: {Esc(ex.Message)}[/color]");
                Logger.LogWarning("Properties panel: " + ex);
            }

            _backButton.Disabled = !(_historyIndex > 0 || (_historyIndex == -1 && _history.Count > 0));
            _forwardButton.Disabled = !(_historyIndex >= 0 && _historyIndex < _history.Count - 1);

            // Size text before attaching, so nothing re-shapes after it's in the tree.
            MainTrackerWindow.SetFontSizeRecursive(_content, _host.Settings.PropertiesFontSize);
            if (old != null)
            {
                _scroll.RemoveChild(old);
                old.QueueFree();
            }
            _scroll.AddChild(_content);
            if (scrollY > 0) Ui.Defer(_scroll, () => _scroll.ScrollVertical = scrollY);
        }

        private void OnEditorFocusExited()
        {
            if (_refreshPending) QueueRefresh();
        }

        /// <summary>A callback for async sections: runs only if the panel still shows the same render.</summary>
        private Action<Action> StillCurrent()
        {
            int gen = _renderGeneration;
            return update =>
            {
                if (!IsInstanceValid(this) || gen != _renderGeneration) return;
                update();
            };
        }

        private static string Esc(string s) => Bbcode.Escape(s);

        private static string Hex(Color c) => "#" + c.ToHtml(false);

        private Color LinkColor => ThemeColors.Accent.Lightened(0.35f);

        /// <summary>BBCode for a clickable link that runs an action.</summary>
        private string Link(string text, Action action, Color? color = null)
        {
            _linkActions.Add(action);
            var c = color ?? LinkColor;
            return $"[url={_linkActions.Count - 1}][color={Hex(c)}]{Esc(text)}[/color][/url]";
        }

        /// <summary>BBCode for a link that opens another target in Properties.</summary>
        private string LinkTo(string text, InspectTarget target, Color? color = null) =>
            target == null ? $"[color={Hex(color ?? ThemeColors.Text)}]{Esc(text)}[/color]" : Link(text, () => Inspect(target), color);

        private static string Colored(string text, Color c) => $"[color={Hex(c)}]{Esc(text)}[/color]";

        private RichTextLabel MakeRichText(string bbcode)
        {
            var rtl = new SafeRichText
            {
                FitContent = true,
                ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SelectionEnabled = false,
                MetaUnderlined = false,
                Markup = bbcode
            };
            // Use the font Labels resolve to, so values match their labels ([code] blocks keep the mono font).
            var labelFont = _kindLabel?.GetThemeFont("font");
            if (labelFont != null) rtl.AddThemeFontOverride("normal_font", labelFont);
            rtl.MetaClicked += meta =>
            {
                if (int.TryParse(meta.AsString(), out int i) && i >= 0 && i < _linkActions.Count) _linkActions[i]();
            };
            rtl.MetaHoverStarted += _ => { rtl.MetaUnderlined = true; rtl.MouseDefaultCursorShape = CursorShape.PointingHand; };
            rtl.MetaHoverEnded += _ => { rtl.MetaUnderlined = false; rtl.MouseDefaultCursorShape = CursorShape.Arrow; };
            // Right-click copies the value's plain text.
            rtl.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton m && m.Pressed && m.ButtonIndex == MouseButton.Right)
                {
                    string text = rtl.GetParsedText().Trim();
                    if (text.Length == 0) return;
                    DisplayServer.ClipboardSet(text);
                    _host.Toast("Copied: " + (text.Length > 60 ? text.Substring(0, 60) + "…" : text), ThemeColors.TextSubtle);
                }
            };
            return rtl;
        }

        private Container Target => _currentSectionBody ?? (Container)_content;

        // --- Header ---

        private void SetHeader(string kind, string title, string subtitleBbcode, Color badgeColor, string badgeText)
        {
            _kindLabel.Text = kind.ToUpperInvariant();
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 2);
            var titleLabel = new Label { Text = title, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            titleLabel.SetMeta("font_size_ratio", 1.3);
            titleLabel.AddThemeColorOverride("font_color", ThemeColors.Text);
            box.AddChild(titleLabel);

            string badge = string.IsNullOrEmpty(badgeText) ? "" : $"[bgcolor={Hex(ThemeColors.Current.IsDark ? badgeColor.Darkened(0.55f) : badgeColor.Lightened(0.75f))}][color={Hex(badgeColor)}] {Esc(badgeText)} [/color][/bgcolor]  ";
            box.AddChild(MakeRichText(badge + (subtitleBbcode ?? "")));
            _content.AddChild(box);
            _plainText.AppendLine(title);
            if (!string.IsNullOrEmpty(badgeText)) _plainText.AppendLine(badgeText);
            _plainText.AppendLine();
        }

        // --- Quick actions ---

        private HFlowContainer _actionBar;

        private void BeginActions()
        {
            _actionBar = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _actionBar.AddThemeConstantOverride("h_separation", 4);
            _actionBar.AddThemeConstantOverride("v_separation", 4);
            _content.AddChild(_actionBar);
        }

        private Button AddAction(string text, string tooltip, Action onPressed, bool enabled = true)
        {
            if (_actionBar == null) BeginActions();
            var b = Kit.Button(text, tooltip, onPressed, enabled, small: true);
            _actionBar.AddChild(b);
            return b;
        }

        private void EndActions()
        {
            if (_actionBar != null && _actionBar.GetChildCount() == 0) _actionBar.QueueFree();
            _actionBar = null;
            _content.AddChild(new HSeparator());
        }

        // --- Sections ---

        /// <summary>Starts a collapsible section. Subsequent rows go into it until the next section.</summary>
        private void Section(string title)
        {
            var collapsed = _host.Settings.CollapsedPropertySections ??= new List<string>();
            bool isCollapsed = collapsed.Contains(title);

            var header = new Button
            {
                Text = (isCollapsed ? "▶  " : "▼  ") + title,
                Flat = true,
                Alignment = HorizontalAlignment.Left,
                FocusMode = FocusModeEnum.None,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            header.AddThemeColorOverride("font_color", ThemeColors.Accent.Lightened(0.2f));
            header.AddThemeColorOverride("font_hover_color", ThemeColors.Accent.Lightened(0.45f));
            _content.AddChild(header);

            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = !isCollapsed };
            margin.AddThemeConstantOverride("margin_left", 10);
            _content.AddChild(margin);
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 4);
            margin.AddChild(body);
            _currentSectionBody = body;

            header.Pressed += () =>
            {
                bool nowCollapsed = margin.Visible;
                margin.Visible = !nowCollapsed;
                header.Text = (nowCollapsed ? "▶  " : "▼  ") + title;
                if (nowCollapsed) { if (!collapsed.Contains(title)) collapsed.Add(title); }
                else collapsed.Remove(title);
                DataManager.SaveSettings(_host.Settings);
            };
            _plainText.AppendLine("[" + title + "]");
        }

        /// <summary>A labelled value. The value is BBCode (use Link/LinkTo/Colored); right-click copies it.</summary>
        private void Row(string label, string valueBbcode, string tooltip = null)
        {
            if (string.IsNullOrEmpty(valueBbcode)) return;
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 8);
            var key = new Label
            {
                Text = label,
                CustomMinimumSize = new Vector2(0, 0),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = 0.55f,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                TooltipText = tooltip ?? ""
            };
            key.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            row.AddChild(key);
            var value = MakeRichText(valueBbcode);
            value.SizeFlagsStretchRatio = 1f;
            value.TooltipText = "Right-click to copy";
            row.AddChild(value);
            Target.AddChild(row);
            _plainText.AppendLine(label + ": " + StripBbcode(valueBbcode));
        }

        private void PlainRow(string label, string value, Color? color = null) =>
            Row(label, string.IsNullOrEmpty(value) ? null : Colored(value, color ?? ThemeColors.Text));

        /// <summary>A full-width paragraph of BBCode.</summary>
        private RichTextLabel AddText(string bbcode)
        {
            var rtl = MakeRichText(bbcode);
            Target.AddChild(rtl);
            _plainText.AppendLine(StripBbcode(bbcode));
            return rtl;
        }

        /// <summary>A monospace block (rule source), selectable for copying.</summary>
        private void AddCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = ThemeColors.SurfaceDeep,
                ContentMarginLeft = 6,
                ContentMarginRight = 6,
                ContentMarginTop = 4,
                ContentMarginBottom = 4,
                CornerRadiusTopLeft = 3,
                CornerRadiusTopRight = 3,
                CornerRadiusBottomLeft = 3,
                CornerRadiusBottomRight = 3
            });
            var rtl = MakeRichText("[code]" + Esc(code) + "[/code]");
            rtl.SelectionEnabled = true;
            panel.AddChild(rtl);
            Target.AddChild(panel);
            _plainText.AppendLine(code);
        }

        private void AddHint(string text) => AddText(Colored(text, ThemeColors.TextSubtle));

        private static string StripBbcode(string bbcode)
        {
            if (string.IsNullOrEmpty(bbcode)) return "";
            var sb = new StringBuilder();
            int i = 0;
            while (i < bbcode.Length)
            {
                if (bbcode[i] == '[')
                {
                    if (bbcode.AsSpan(i).StartsWith("[lb]")) { sb.Append('['); i += 4; continue; }
                    int end = bbcode.IndexOf(']', i);
                    if (end > i) { i = end + 1; continue; }
                }
                sb.Append(bbcode[i++]);
            }
            return sb.ToString();
        }
    }
}
