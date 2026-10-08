using System;
using Godot;
using AP_Atlas.Core.Updates;

namespace AP_Atlas.UI
{
    /// <summary>
    /// A newer Atlas: its version, when it was released, what the download comes to and what's new (the release's notes,
    /// rendered like Atlas's own documents: https links only, nothing read as markup), with the choice to download and
    /// install it or not. It frees itself when closed.
    /// </summary>
    public sealed partial class UpdateDialog : AcceptDialog
    {
        private readonly SafeRichText _text = new();

        /// <param name="download">Runs when the user chooses to download and install the release.</param>
        public UpdateDialog(ReleaseInfo release, SemVer current, Func<string, string> tr, Action download)
        {
            Title = tr("Update The Archipelago Atlas");
            OkButtonText = tr("Download and install");
            AddCancelButton(tr("Later"));
            MinSize = new Vector2I(760, 540);
            Unresizable = false;
            string size = release.ZipSize > 0 ? $"{release.ZipSize / (1024 * 1024)} MB" : "?";
            string head = $"## The Archipelago Atlas {release.Version}\n\n"
                + (release.PreRelease ? "A pre-release (beta). " : "")
                + (release.PublishedUtc is { } when ? $"Released {when:yyyy-MM-dd}. " : "")
                + $"You have {current}. The download is about {size}; it's checked against the release's SHA-256 before it's used, "
                + "your data folder isn't touched, and the version you have now is kept so you can go back.\n\n### What's new\n\n";
            string notes = release.Notes.Trim().Length > 0 ? release.Notes : "(The release has no notes.)";
            _text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _text.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _text.SelectionEnabled = true;
            _text.Markup = AP_Atlas.Core.Markdown.ToBbcode(head + notes, "#" + AP_Atlas.Core.ThemeColors.Link.ToHtml(false));
            AddChild(_text);
            // Prose in the font the labels use; the rich text's own default is the mono font.
            var labelFont = GetThemeFont("font", "Label");
            if (labelFont != null) _text.AddThemeFontOverride("normal_font", labelFont);
            Confirmed += download;
            Confirmed += QueueFree;
            Canceled += QueueFree;
        }

        /// <summary>The dialog's text as shown, without markup (for tests).</summary>
        public string ShownText => _text.GetParsedText();
    }
}
