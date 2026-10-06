using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Settings → Privacy &amp; permissions: everything Atlas may do outside its own folder or online by itself, each with
    /// its state and a way to take a kept answer back; the apworld sources the user trusts; where Atlas goes online.
    /// Drawn again whenever the page shows (<see cref="Refresh"/>).
    /// </summary>
    public sealed partial class PrivacyPanel : VBoxContainer
    {
        private readonly AppSettings _settings;
        private readonly Func<string, string> _tr;
        private readonly VBoxContainer _permissions = new();
        private readonly VBoxContainer _sources = new();
        private readonly Dictionary<Permissions.Kind, string> _states = new();
        private readonly List<Button> _takeBack = new();
        private readonly List<Button> _stopTrusting = new();

        public PrivacyPanel(AppSettings settings, Func<string, string> tr)
        {
            _settings = settings;
            _tr = tr;
            Name = "Privacy";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            AddThemeConstantOverride("separation", 10);

            AddChild(Heading(_tr("What Atlas may do by itself")));
            _permissions.AddThemeConstantOverride("separation", 8);
            AddChild(_permissions);

            AddChild(Heading(_tr("Trusted apworld sources")));
            AddChild(Text(_tr("Apworlds are programs that run inside Atlas's logic engine. Atlas downloads them only from sources listed here, or after asking you."), Colors.Gray));
            _sources.AddThemeConstantOverride("separation", 6);
            AddChild(_sources);

            AddChild(Heading(_tr("Where Atlas goes online")));
            AddChild(Text(_tr("The archipelago.gg rooms you connect to. Cheese Tracker (your instance) and spheretracker.de (the host's room) for the multiworlds you link. GitHub for apworld releases, when you allow it. python.org, pypa.io and PyPI when you set up the Atlas Engine. Nothing else, and nothing at startup."), Colors.Gray));
            Refresh();
        }

        /// <summary>The state shown for a permission: asks each time, allowed until Atlas closes, always allowed, or not until Atlas restarts.</summary>
        public string StateOf(Permissions.Kind kind) => _states.GetValueOrDefault(kind, "");

        /// <summary>One per kept "Always allow", in the order shown.</summary>
        public IReadOnlyList<Button> TakeBackButtons => _takeBack;

        /// <summary>One per trusted apworld source, in the order shown.</summary>
        public IReadOnlyList<Button> StopTrustingButtons => _stopTrusting;

        /// <summary>Draws everything again from the settings and the session's answers.</summary>
        public void Refresh()
        {
            Clear(_permissions);
            Clear(_sources);
            _states.Clear();
            _takeBack.Clear();
            _stopTrusting.Clear();

            var granted = Permissions.Granted(_settings);
            foreach (var kind in Permissions.All)
            {
                var grants = granted.Where(g => g.Kind == kind).ToList();
                string state = grants.Count > 0 ? _tr("Always allowed")
                    : Permissions.IsAllowed(_settings, kind) ? _tr("Allowed until Atlas closes")
                    : Permissions.DeniedThisSession(kind) ? _tr("Not until Atlas restarts")
                    : _tr("Asks each time");
                _states[kind] = state;

                var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 16);
                var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                text.AddThemeConstantOverride("separation", 2);
                text.AddChild(new Label { Text = _tr(kind.Title) });
                var explanation = Text(_tr(kind.Explanation), Colors.LightGray);
                explanation.SetMeta("font_size_ratio", 0.9f);
                text.AddChild(explanation);
                row.AddChild(text);
                var shown = new Label { Text = state, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                shown.AddThemeColorOverride("font_color", grants.Count > 0 ? ThemeColors.Accent.Lightened(0.3f) : Colors.LightGray);
                row.AddChild(shown);
                _permissions.AddChild(row);

                foreach (var (_, scope) in grants)
                {
                    var grant = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    grant.AddThemeConstantOverride("separation", 16);
                    var where = Text(string.IsNullOrEmpty(scope) ? _tr("Everywhere") : scope, Colors.LightGray);
                    grant.AddChild(where);
                    var take = new Button { Text = _tr("Take back"), TooltipText = _tr("Atlas will ask again next time.") };
                    var k = kind;
                    string s = scope;
                    take.Pressed += () =>
                    {
                        Permissions.SetAlways(_settings, k, s, false);
                        Refresh();
                    };
                    grant.AddChild(take);
                    _takeBack.Add(take);
                    _permissions.AddChild(grant);
                }
            }

            var sources = Permissions.TrustedSources(_settings);
            if (sources.Count == 0) _sources.AddChild(Text(_tr("None."), Colors.Gray));
            foreach (string source in sources)
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 16);
                row.AddChild(Text(source, Colors.LightGray));
                var stop = new Button { Text = _tr("Stop trusting"), TooltipText = _tr("Atlas will ask before downloading from it again.") };
                string src = source;
                stop.Pressed += () =>
                {
                    Permissions.StopTrusting(_settings, src);
                    Refresh();
                };
                row.AddChild(stop);
                _stopTrusting.Add(stop);
                _sources.AddChild(row);
            }
            // Drawn after the page was mounted, so the rows take the content font size themselves.
            MainTrackerWindow.SetFontSizeRecursive(this, _settings.ContentFontSize);
        }

        /// <summary>Takes a box's rows out now (a freed node stays a child until the frame ends, which would confuse anyone counting).</summary>
        private static void Clear(Node box)
        {
            foreach (Node child in box.GetChildren())
            {
                box.RemoveChild(child);
                child.QueueFree();
            }
        }

        private static Label Heading(string text)
        {
            var label = new Label { Text = text };
            label.SetMeta("font_size_ratio", 1.1f);
            label.AddThemeColorOverride("font_color", ThemeColors.Accent.Lightened(0.2f));
            return label;
        }

        private static Label Text(string text, Color color)
        {
            var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            label.AddThemeColorOverride("font_color", color);
            return label;
        }
    }
}
