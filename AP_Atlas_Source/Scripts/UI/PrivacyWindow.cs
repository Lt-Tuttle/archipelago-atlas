using System;
using System.Linq;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Settings → Privacy &amp; permissions: everything the user has allowed Atlas to do on its own, and every apworld source they
    /// trust, each with a way to take it back. (A first version; the full Settings page comes with the new shell.)
    /// </summary>
    public partial class PrivacyWindow : AcceptDialog
    {
        private readonly AppSettings _settings;
        private readonly int _fontSize;
        private VBoxContainer _content;

        public PrivacyWindow(AppSettings settings, int fontSize)
        {
            _settings = settings;
            _fontSize = fontSize;
            Title = "Privacy & permissions";
            OkButtonText = "Close";
            MinSize = new Vector2I(640, 480);
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(600, 420) };
            _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _content.AddThemeConstantOverride("separation", 10);
            scroll.AddChild(_content);
            AddChild(scroll);
            Confirmed += QueueFree;
            Canceled += QueueFree;
            Render();
        }

        private void Render()
        {
            foreach (Node n in _content.GetChildren()) n.QueueFree();
            _content.AddChild(Text("Atlas asks before it looks or writes outside its own folder, or goes online for something new. " +
                                   "This is everything you've told it to do without asking, and every source you trust. Take anything back to be asked again."));

            _content.AddChild(Heading("Allowed without asking"));
            var granted = Permissions.Granted(_settings);
            if (granted.Count == 0) _content.AddChild(Muted("Nothing. Atlas asks every time."));
            foreach (var (kind, scope) in granted)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                var label = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                label.AddChild(new Label { Text = kind.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                if (!string.IsNullOrEmpty(scope)) label.AddChild(Muted(scope));
                row.AddChild(label);
                var take = new Button { Text = "Take back", TooltipText = "Atlas will ask again next time" };
                var k = kind;
                string s = scope;
                take.Pressed += () =>
                {
                    Permissions.SetAlways(_settings, k, s, false);
                    Render();
                };
                row.AddChild(take);
                _content.AddChild(row);
            }

            _content.AddChild(Heading("Trusted apworld sources"));
            _content.AddChild(Muted("Apworlds are programs that run inside Atlas's logic engine. Atlas downloads them only from sources listed here, or after asking you."));
            var sources = (_settings.ApprovedApworldSources ?? new System.Collections.Generic.List<string>()).ToList();
            if (sources.Count == 0) _content.AddChild(Muted("None."));
            foreach (var source in sources)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                row.AddChild(new Label { Text = source, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                var remove = new Button { Text = "Stop trusting", TooltipText = "Atlas will ask before downloading from it again" };
                string src = source;
                remove.Pressed += () =>
                {
                    _settings.ApprovedApworldSources.RemoveAll(x => string.Equals(x, src, StringComparison.OrdinalIgnoreCase));
                    DataManager.SaveSettings(_settings);
                    Logger.LogInfo($"No longer trusting {src} for apworld downloads.");
                    Render();
                };
                row.AddChild(remove);
                _content.AddChild(row);
            }
            MainTrackerWindow.SetFontSizeRecursive(_content, _fontSize);
        }

        private static Label Heading(string text)
        {
            var l = new Label { Text = text };
            l.SetMeta("font_size_ratio", 1.15);
            l.AddThemeColorOverride("font_color", ThemeColors.Accent.Lightened(0.2f));
            return l;
        }

        private static Label Text(string text) => new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(560, 0) };

        private static Label Muted(string text)
        {
            var l = Text(text);
            l.AddThemeColorOverride("font_color", Colors.Gray);
            return l;
        }
    }
}
