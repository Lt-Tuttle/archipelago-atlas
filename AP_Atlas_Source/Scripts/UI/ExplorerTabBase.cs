using System;
using System.Collections.Generic;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// What the multiworld tools (the Cheese and Sphere tabs) share: an explorer list the window mounts beside the content
    /// (<see cref="SidebarContent"/>, drawn again only when its signature changes), a view the list chooses and the
    /// settings remember (<see cref="ShowView"/>), pages shown one at a time (<see cref="ShowPage"/>, among them a message
    /// page with actions), a redraw that runs while the tab shows and once it does otherwise (<see cref="Refresh"/>), a
    /// debounced one for the service's events (<see cref="QueueRefresh"/>), and a minute's tick while shown so what's
    /// on screen is read when that's due (<see cref="WatchShown"/>).
    /// </summary>
    public abstract partial class ExplorerTabBase : MarginContainer
    {
        protected readonly AppSettings Settings;
        protected readonly Func<IReadOnlyList<MultiworldProfile>> Profiles;
        protected readonly Action<string, Color> Toast;

        /// <summary>The explorer list; the window mounts it in the explorer while this tab is shown.</summary>
        public VBoxContainer SidebarContent { get; }

        /// <summary>What the tab shows: a multiworld, a slot, "my slots", the settings; the tab names them.</summary>
        public string View { get; private set; }

        private string? _sidebarSignature;
        private readonly List<Control> _pages = new();
        private VBoxContainer? _messagePage;
        private Timer? _refreshDebounce;
        private ViewRefresh? _redraw;

        protected ExplorerTabBase(AppSettings settings, Func<IReadOnlyList<MultiworldProfile>> profiles, Action<string, Color> toast, string name, string? initialView)
        {
            Settings = settings;
            Profiles = profiles;
            Toast = toast;
            Name = name;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            foreach (string side in new[] { "left", "top", "right", "bottom" }) AddThemeConstantOverride("margin_" + side, 10);
            SidebarContent = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            SidebarContent.AddThemeConstantOverride("separation", 4);
            View = string.IsNullOrEmpty(initialView) ? DefaultView : initialView!;
        }

        public override void _Ready()
        {
            BuildPages();
            _messagePage = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
            _messagePage.AddThemeConstantOverride("separation", 10);
            AddPage(_messagePage);

            // The service's events come in bursts (a read finished, a link changed): one redraw a moment later.
            _refreshDebounce = new Timer { OneShot = true, WaitTime = 0.3 };
            _refreshDebounce.Timeout += () =>
            {
                WatchShown();
                Refresh();
            };
            AddChild(_refreshDebounce);
            // While shown: keep what's on screen fresh (the services read at most every 10 minutes) and the ages current.
            var minute = new Timer { WaitTime = 60, Autostart = true };
            minute.Timeout += () =>
            {
                if (!IsVisibleInTree()) return;
                WatchShown();
                Refresh();
            };
            AddChild(minute);
            // Made while the tab is in the window, again after it's moved (docking, pop-outs), and removed while it isn't.
            AddChild(Subscribe(new TreeSubscriptions()));
            Refresh();
        }

        /// <summary>The view shown when none is remembered.</summary>
        protected abstract string DefaultView { get; }

        /// <summary>Builds the tab's pages (each through <see cref="AddPage"/>).</summary>
        protected abstract void BuildPages();

        /// <summary>The events the tab follows while it's in the window.</summary>
        protected abstract TreeSubscriptions Subscribe(TreeSubscriptions subscriptions);

        /// <summary>Remembers the view in the settings.</summary>
        protected abstract void SaveView(string view);

        /// <summary>Draws the tab now: the explorer list, then the page the view asks for.</summary>
        protected abstract void RefreshNow();

        /// <summary>The view changed (before the redraw): forget what belonged to the old one.</summary>
        protected virtual void OnViewChanged() { }

        /// <summary>Reads what's shown if that's due (nothing is read for what isn't shown).</summary>
        protected virtual void WatchShown() { }

        /// <summary>The tab was just shown: read what it shows if that's due, and draw it.</summary>
        public void OnShown()
        {
            WatchShown();
            Refresh();
        }

        /// <summary>Shows a view (the tab's own names; empty means the default), remembers it, and redraws.</summary>
        public void ShowView(string? view)
        {
            if (string.IsNullOrEmpty(view)) view = DefaultView;
            if (view != View)
            {
                View = view!;
                OnViewChanged();
                SaveView(View);
            }
            InvalidateSidebar();
            WatchShown();
            Refresh();
        }

        /// <summary>Redraws the tab: now if it shows, else when it does (at most once a frame).</summary>
        public void Refresh() => (_redraw ??= new ViewRefresh(this, RefreshNow, "redrawing " + Name)).Request();

        /// <summary>A redraw a moment after the service's event, if the tab shows (a shown tab redraws by itself).</summary>
        protected void QueueRefresh()
        {
            if (!IsVisibleInTree()) return;
            if (_refreshDebounce != null && _refreshDebounce.IsInsideTree() && _refreshDebounce.IsStopped()) _refreshDebounce.Start();
        }

        /// <summary>Adds a page (hidden until <see cref="ShowPage"/>).</summary>
        protected void AddPage(Control page)
        {
            page.Visible = false;
            AddChild(page);
            _pages.Add(page);
        }

        protected void ShowPage(Control page)
        {
            foreach (var p in _pages) p.Visible = p == page;
        }

        /// <summary>A page of its own for a state: a title, an explanation and the actions that lead out of it.</summary>
        protected void ShowMessage(string title, string text, List<(string Label, Action Action)> actions)
        {
            if (_messagePage == null) return;
            ShowPage(_messagePage);
            foreach (Node child in _messagePage.GetChildren()) child.QueueFree();
            var heading = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            heading.SetMeta("font_size_ratio", 1.25);
            _messagePage.AddChild(heading);
            var body = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            body.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            _messagePage.AddChild(body);
            var row = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center };
            row.AddThemeConstantOverride("h_separation", 6);
            foreach (var (label, action) in actions) row.AddChild(Kit.Button(label, null, action));
            _messagePage.AddChild(row);
            MainTrackerWindow.SetFontSizeRecursive(_messagePage, Settings.ContentFontSize);
        }

        /// <summary>Draws the explorer list again when its signature changed (what it lists, and the view); otherwise leaves it.</summary>
        protected void RenderSidebar(string signature, Action build)
        {
            if (signature == _sidebarSignature) return;
            _sidebarSignature = signature;
            foreach (Node child in SidebarContent.GetChildren()) child.QueueFree();
            build();
            MainTrackerWindow.SetFontSizeRecursive(SidebarContent, Settings.ExplorerFontSize);
        }

        protected void InvalidateSidebar() => _sidebarSignature = null;

        /// <summary>A line of the explorer list: pressed, it shows a view; the shown view's line is lit.</summary>
        protected Control ExplorerButton(string text, string tooltip, string view, bool dim, int indent = 0, Action? afterShow = null)
        {
            var button = new Button { Text = text, TooltipText = tooltip, Alignment = HorizontalAlignment.Left, ToggleMode = true, ButtonPressed = view == View, FocusMode = FocusModeEnum.None, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            if (dim && view != View) button.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            button.Pressed += () =>
            {
                ShowView(view);
                afterShow?.Invoke();
            };
            if (indent == 0) return button;
            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            margin.AddThemeConstantOverride("margin_left", indent);
            margin.AddChild(button);
            return margin;
        }

        /// <summary>A quiet wrapped note in the explorer list ("No multiworlds yet").</summary>
        protected static Label SidebarNote(string text) => Kit.Subtle(text);
    }
}
