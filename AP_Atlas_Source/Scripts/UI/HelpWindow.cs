using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Help: the guide, what's new, the credits and disclaimer, the licences and Godot's components, from the documents built into Atlas
    /// (<see cref="AP_Atlas.Core.Docs"/>) rendered by <see cref="AP_Atlas.Core.Markdown"/> into Atlas's own rich text. The
    /// topics on the left, the topic on the right; a link in a topic opens in the browser (https only, when clicked).
    /// One window at a time; it frees itself when closed.
    /// </summary>
    public sealed partial class HelpWindow : AcceptDialog
    {
        public const string WhatsNew = "whats-new";
        public const string Credits = "credits";
        public const string Licences = "licences";
        public const string GodotComponents = "godot-components";

        private readonly Func<string, string> _tr;
        private readonly Action<string> _openWeb;
        private readonly ItemList _topics = new();
        private readonly SafeRichText _text = new();
        private readonly List<(string Id, string Title, Func<string> Markdown)> _pages = new();

        /// <param name="openWeb">Opens an https link in the browser (the window checks the scheme first).</param>
        public HelpWindow(Func<string, string> tr, Action<string> openWeb)
        {
            _tr = tr;
            _openWeb = openWeb;
            Title = _tr("Help");
            OkButtonText = _tr("Close");
            MinSize = new Vector2I(920, 640);
            Unresizable = false;

            foreach (var (title, body) in AP_Atlas.Core.Markdown.Sections(AP_Atlas.Core.Docs.Read(AP_Atlas.Core.Docs.Guide)))
            {
                string heading = title, text = body;
                _pages.Add(("guide:" + title, title, () => "## " + heading + "\n\n" + text));
            }
            _pages.Add((WhatsNew, "What's new", WhatsNewMarkdown));
            _pages.Add((Credits, "Credits & disclaimer", () => DisclaimerMarkdown() + AP_Atlas.Core.Docs.Read(AP_Atlas.Core.Docs.Credits)));
            _pages.Add((Licences, "Licences", () =>
                "## Atlas's licence\n\n```\n" + AP_Atlas.Core.Docs.Read(AP_Atlas.Core.Docs.License) + "\n```\n\n" + AP_Atlas.Core.Docs.Read(AP_Atlas.Core.Docs.ThirdPartyNotices)));
            _pages.Add((GodotComponents, "Godot's components", GodotComponentsMarkdown));

            var box = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 10);
            _topics.CustomMinimumSize = new Vector2(240, 0);
            _topics.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            foreach (var page in _pages) _topics.AddItem(_tr(page.Title));
            _topics.ItemSelected += index => Show((int)index);
            box.AddChild(_topics);
            _text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _text.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _text.SelectionEnabled = true;
            // Prose in the font the labels use (the rich text's own default is the mono font); code keeps the mono font.
            var labelFont = _topics.GetThemeFont("font");
            if (labelFont != null) _text.AddThemeFontOverride("normal_font", labelFont);
            _text.MetaClicked += meta =>
            {
                string url = meta.AsString();
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) _openWeb(url);
            };
            _text.MetaHoverStarted += _ => _text.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
            _text.MetaHoverEnded += _ => _text.MouseDefaultCursorShape = Control.CursorShape.Arrow;
            box.AddChild(_text);
            AddChild(box);
            Confirmed += QueueFree;
            Canceled += QueueFree;
        }

        /// <summary>The topics, in order: the guide's sections, then what's new, the credits, the licences and Godot's components.</summary>
        public IReadOnlyList<string> PageIds => _pages.Select(p => p.Id).ToList();

        /// <summary>The topic showing.</summary>
        public string CurrentPageId { get; private set; } = "";


        /// <summary>The topic's text as shown, without markup (for tests).</summary>
        public string ShownText => _text.GetParsedText();

        /// <summary>Opens the window at a topic (the guide's first section when none is named).</summary>
        public void Open(Node parent, string? pageId = null)
        {
            parent.AddChild(this);
            Select(pageId);
            PopupCentered();
        }

        /// <summary>Shows a document that isn't one of Atlas's own (an apworld's setup guide), as a topic of its own; the same id replaces it.</summary>
        public void ShowDocument(string id, string title, string markdown)
        {
            int index = _pages.FindIndex(p => p.Id == id);
            if (index < 0)
            {
                _pages.Add((id, title, () => markdown));
                _topics.AddItem(title);
            }
            else
            {
                _pages[index] = (id, title, () => markdown);
                _topics.SetItemText(index, title);
            }
            Select(id);
        }

        /// <summary>Shows a topic by id; an unknown one shows the first.</summary>
        public void Select(string? pageId)
        {
            int index = pageId == null ? 0 : _pages.FindIndex(p => p.Id == pageId);
            if (index < 0) index = 0;
            _topics.Select(index); // tells no one, unlike a click
            Show(index);
        }

        private void Show(int index)
        {
            var page = _pages[index];
            CurrentPageId = page.Id;
            _text.Markup = AP_Atlas.Core.Markdown.ToBbcode(page.Markdown(), "#" + AP_Atlas.Core.ThemeColors.Link.ToHtml(false));
            _text.ScrollToLine(0);
        }

        /// <summary>The changelog's newest version (and the one before it), as it's written.</summary>
        public static string WhatsNewMarkdown()
        {
            var sections = AP_Atlas.Core.Markdown.Sections(AP_Atlas.Core.Docs.Read(AP_Atlas.Core.Docs.Changelog)).Take(2).ToList();
            if (sections.Count == 0) return "## What's new\n\nNothing yet.";
            return string.Join("\n\n", sections.Select(s => "## " + s.Title + "\n\n" + s.Body));
        }

        /// <summary>The newest version's headline changes, one line each (Home's card): the top-level entries under Added.</summary>
        public static IReadOnlyList<string> WhatsNewLines(int most = 5)
        {
            var first = AP_Atlas.Core.Markdown.Sections(AP_Atlas.Core.Docs.Read(AP_Atlas.Core.Docs.Changelog)).FirstOrDefault();
            var lines = new List<string>();
            bool inAdded = false;
            foreach (string raw in (first.Body ?? "").Split('\n'))
            {
                if (raw.StartsWith("### ", StringComparison.Ordinal))
                {
                    inAdded = raw.Trim() == "### Added";
                    continue;
                }
                if (!inAdded || !raw.StartsWith("- ", StringComparison.Ordinal)) continue;
                string line = AP_Atlas.Core.Markdown.Plain(raw.Substring(2)).TrimEnd(':');
                if (line.Length > 110) line = line.Substring(0, 109).TrimEnd() + "…";
                lines.Add(line);
                if (lines.Count == most) break;
            }
            return lines;
        }

        /// <summary>
        /// Godot's own notices, as the engine reports them: every component Godot bundles, with its copyright holders and
        /// licence, then each licence's text. The same notices ship with every release as GODOT_COPYRIGHT.txt (the self-test
        /// keeps that file in step with the engine).
        /// </summary>
        public static string GodotComponentsMarkdown()
        {
            var text = new System.Text.StringBuilder();
            text.Append("## Godot's components\n\n");
            text.Append("Atlas runs on Godot ").Append(Engine.GetVersionInfo()["string"].AsString())
                .Append(", which is MIT-licensed and bundles the components below, each under its own licence. ")
                .Append("These are the engine's own notices, as it reports them; the same notices ship with every release of Atlas as GODOT_COPYRIGHT.txt.\n\n");
            foreach (var component in Engine.GetCopyrightInfo())
            {
                text.Append("### ").Append(component["name"].AsString()).Append("\n\n```\n");
                foreach (var part in component["parts"].AsGodotArray())
                {
                    var details = part.AsGodotDictionary();
                    foreach (var holder in details["copyright"].AsGodotArray()) text.Append("Copyright: ").Append(holder.AsString()).Append('\n');
                    text.Append("Licence: ").Append(details["license"].AsString()).Append('\n');
                }
                text.Append("```\n\n");
            }
            text.Append("## Licence texts\n\n");
            var licences = Engine.GetLicenseInfo();
            foreach (var name in licences.Keys)
                text.Append("### ").Append(name.AsString()).Append("\n\n```\n").Append(licences[name].AsString().TrimEnd()).Append("\n```\n\n");
            return text.ToString();
        }

        private static string DisclaimerMarkdown() =>
            "## Credits & disclaimer\n\n" +
            "The Archipelago Atlas is an unofficial community tool. It is not affiliated with or endorsed by the Archipelago project.\n\n" +
            "Atlas is designed, directed and tested by Lt-Tuttle. Most of its code was written with an AI assistant (Anthropic's Claude). " +
            "Atlas's icon is AI-generated, drawn in the style of Archipelago's logo.\n\n";
    }
}
