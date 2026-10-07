using System;
using System.Runtime.InteropServices;
using System.Text;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Help → About: the version, what Atlas is and who made it, what sets it apart, where it goes online (the privacy
    /// statement, the same words as Settings → Privacy &amp; permissions), and the system information a bug report needs,
    /// with a button that copies it. Links open in the browser when clicked. Frees itself when closed.
    /// </summary>
    public sealed partial class AboutDialog : AcceptDialog
    {
        private readonly SafeRichText _text = new();

        /// <param name="engineLine">The logic engine as it is ("Atlas portable engine, Archipelago 0.6.7", or that it isn't set up).</param>
        /// <param name="commit">The build's commit, named after the version (empty for none: the visual check, so its picture doesn't change with every commit).</param>
        public AboutDialog(Func<string, string> tr, string engineLine, string commit, Action<string> openWeb, Action openGuide, Action<string> toast)
        {
            Title = tr("About The Archipelago Atlas");
            OkButtonText = tr("Close");
            MinSize = new Vector2I(760, 600);
            Unresizable = false;
            SystemInfo = BuildSystemInfo(engineLine, commit);

            _text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _text.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _text.SelectionEnabled = true;
            // Prose in the font the labels use (the rich text's own default is the mono font); the system block keeps the mono font.
            var labelFont = GetThemeFont("font", "Label");
            if (labelFont != null) _text.AddThemeFontOverride("normal_font", labelFont);
            _text.CustomMinimumSize = new Vector2(720, 520);
            _text.Markup = AP_Atlas.Core.Markdown.ToBbcode(Markdown(tr, commit), "#" + AP_Atlas.Core.ThemeColors.Accent.Lightened(0.3f).ToHtml(false));
            _text.MetaClicked += meta =>
            {
                string url = meta.AsString();
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) openWeb(url);
            };
            _text.MetaHoverStarted += _ => _text.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
            _text.MetaHoverEnded += _ => _text.MouseDefaultCursorShape = Control.CursorShape.Arrow;
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 10);
            box.AddChild(Wordmark.Make(56, tr("The Archipelago Atlas")));
            box.AddChild(_text);
            AddChild(box);

            AddButton(tr("Copy System Info"), false, "copy");
            AddButton(tr("Guide"), false, "guide");
            AddButton(tr("Atlas on GitHub"), false, "github");
            AddButton(tr("Releases"), false, "releases");
            AddButton(tr("Report an Issue"), false, "issues");
            CustomAction += action =>
            {
                switch ((string)action)
                {
                    case "copy":
                        DisplayServer.ClipboardSet(SystemInfo);
                        toast(tr("System information copied."));
                        break;
                    case "guide": openGuide(); break;
                    case "github": openWeb(AP_Atlas.Core.AtlasVersion.RepoUrl); break;
                    case "releases": openWeb(AP_Atlas.Core.AtlasVersion.RepoUrl + "/releases"); break;
                    case "issues": openWeb(AP_Atlas.Core.AtlasVersion.RepoUrl + "/issues"); break;
                }
            };
            Confirmed += QueueFree;
            Canceled += QueueFree;
        }

        /// <summary>What Copy System Info copies: the versions of Atlas, Godot, .NET and Windows, the renderer and the engine. No names, paths or addresses.</summary>
        public string SystemInfo { get; }

        /// <summary>The dialog's text as shown, without markup (for tests).</summary>
        public string ShownText => _text.GetParsedText();

        /// <summary>The system information (versions, the renderer, the locale; nothing that names the PC), as About shows it and the problem report bundles it.</summary>
        internal static string BuildSystemInfo(string engineLine, string commit)
        {
            var lines = new StringBuilder();
            lines.Append("The Archipelago Atlas ").Append(AP_Atlas.Core.AtlasVersion.Display).Append(commit.Length > 0 ? " (" + commit + ")" : "").Append('\n');
            lines.Append("Godot ").Append(Godot.Engine.GetVersionInfo()["string"].AsString()).Append('\n');
            lines.Append(".NET: ").Append(RuntimeInformation.FrameworkDescription).Append('\n');
            lines.Append(OS.GetName()).Append(' ').Append(OS.GetVersion()).Append(" (").Append(RuntimeInformation.OSArchitecture).Append(")\n");
            lines.Append("Renderer: ").Append(RenderingServer.GetCurrentRenderingMethod()).Append(", ").Append(RenderingServer.GetCurrentRenderingDriverName()).Append('\n');
            lines.Append("Logic engine: ").Append(engineLine).Append('\n');
            lines.Append("Locale: ").Append(OS.GetLocale());
            return lines.ToString();
        }

        private string Markdown(Func<string, string> tr, string commit)
        {
            return "# " + tr("The Archipelago Atlas") + " " + AP_Atlas.Core.AtlasVersion.Display + (commit.Length > 0 ? " (" + commit + ")" : "") + "\n\n" +
                tr("An unofficial tracker for Archipelago multiworlds. Not affiliated with or endorsed by the Archipelago project.") + "\n\n" +
                tr("Designed, directed and tested by Lt-Tuttle. Most of its code was written with an AI assistant (Anthropic's Claude). Atlas's icon is AI-generated, drawn in the style of Archipelago's logo. Open source under the MIT licence; the credits and every licence are under Help → Credits & Disclaimer.") + "\n\n" +
                "## " + tr("What sets Atlas apart") + "\n\n" +
                "- " + tr("**Asks first:** nothing outside Atlas's own folder, and nothing new online, without your yes. Settings → Privacy & permissions lists every answer and takes any back.") + "\n" +
                "- " + tr("**Easy on the servers:** polite, capped and backed-off traffic to archipelago.gg and every other site; a closed room is never kept busy.") + "\n" +
                "- " + tr("**Logic on your PC:** the Atlas Engine runs Archipelago's own logic here, so the map and the Logic Tracker know what you can reach, with no extra load on the server.") + "\n" +
                "- " + tr("**Guides built in:** Help → Guide explains every tool and page, with nothing loaded online.") + "\n" +
                "- " + tr("**Yours to shape:** every action is a command, in the menus, the palette (Ctrl+Shift+P) and keys you can change; any part of the window can be hidden, and F9 leaves the content alone.") + "\n" +
                "- " + tr("**Honest in races:** race mode keeps to what a race allows, by itself in race rooms.") + "\n" +
                "- " + tr("**Your group's trackers:** Cheese Tracker and spheretracker.de, read politely and written only as you allow.") + "\n" +
                "- " + tr("**Packs looked over:** the Pack Doctor checks a map pack against the game's real names and fixes what it can, locally.") + "\n\n" +
                "## " + tr("Where Atlas goes online") + "\n\n" + tr(AP_Atlas.Core.Permissions.WhereAtlasGoesOnline) + "\n\n" +
                "## " + tr("System") + "\n\n```\n" + SystemInfo + "\n```\n";
        }
    }
}
