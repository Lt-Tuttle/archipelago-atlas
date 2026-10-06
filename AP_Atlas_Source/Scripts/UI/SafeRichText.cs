using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The one kind of rich text in Atlas that reads BBCode. Its markup passes <see cref="Core.Bbcode.Safe"/> before Godot
    /// reads it, so only Atlas's own tags work, never one that loads a file: Godot opens the files named in [img] and
    /// [font] tags, and for a network path that means connecting to another computer. Set the markup with
    /// <see cref="Markup"/> or <see cref="Append"/>, with any text from outside Atlas escaped
    /// (<see cref="Core.Bbcode.Escape"/>, or a helper that does). Its Text can't be used directly: the compiler refuses.
    /// Plain text belongs in a Label, or a RichTextLabel left without BBCode.
    /// It wraps a paragraph longer than <see cref="SmartWrapLimit"/> at spaces only (TextServer.AutowrapMode.Word). Godot's
    /// default, "word smart", also breaks a word too wide for its line (a server's address in a narrow column), but in a
    /// RichTextLabel that takes time growing with the square of a paragraph's length: 8,000 characters of words took 4.9
    /// seconds to lay out, against 0.16 wrapped at spaces, and text from outside can be that long. Safe gives a long run
    /// of text without a space breaks it can wrap at.
    /// </summary>
    public partial class SafeRichText : RichTextLabel
    {
        /// <summary>The longest paragraph laid out "word smart": one this long takes a few milliseconds at worst.</summary>
        public const int SmartWrapLimit = 300;

        public SafeRichText() => BbcodeEnabled = true;

        /// <summary>The label's markup, made safe as it's set.</summary>
        public string Markup
        {
            get => base.Text;
            set
            {
                string safe = Core.Bbcode.Safe(value);
                bool longParagraph = LongestParagraph(safe) > SmartWrapLimit;
                if (longParagraph && AutowrapMode == TextServer.AutowrapMode.WordSmart) AutowrapMode = TextServer.AutowrapMode.Word;
                else if (!longParagraph && AutowrapMode == TextServer.AutowrapMode.Word) AutowrapMode = TextServer.AutowrapMode.WordSmart;
                base.Text = safe;
            }
        }

        /// <summary>Adds markup at the end, made safe (a long paragraph wraps the whole label at spaces from then on).</summary>
        public void Append(string markup)
        {
            string safe = Core.Bbcode.Safe(markup);
            if (LongestParagraph(safe) > SmartWrapLimit && AutowrapMode == TextServer.AutowrapMode.WordSmart) AutowrapMode = TextServer.AutowrapMode.Word;
            AppendText(safe);
        }

        // The most characters between two line breaks.
        private static int LongestParagraph(string text)
        {
            int longest = 0, start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;
                if (i - start > longest) longest = i - start;
                start = i + 1;
            }
            return longest;
        }

        /// <summary>
        /// Hides <see cref="RichTextLabel.Text"/>, so "label.Text = …" doesn't compile: markup goes in through
        /// <see cref="Markup"/>, which makes it safe. A type rather than a property, because Godot's source generators
        /// read and write every property a script declares (an obsolete one too).
        /// </summary>
        public new sealed class Text
        {
            private Text() { }
        }
    }
}
