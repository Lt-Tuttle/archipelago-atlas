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
    /// </summary>
    public partial class SafeRichText : RichTextLabel
    {
        public SafeRichText() => BbcodeEnabled = true;

        /// <summary>The label's markup, made safe as it's set.</summary>
        public string Markup
        {
            get => base.Text;
            set => base.Text = Core.Bbcode.Safe(value);
        }

        /// <summary>Adds markup at the end, made safe.</summary>
        public void Append(string markup) => AppendText(Core.Bbcode.Safe(markup));

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
