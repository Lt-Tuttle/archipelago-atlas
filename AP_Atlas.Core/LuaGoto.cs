#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace AP_Atlas.Core;

/// <summary>
/// Lua's "continue" idiom made acceptable to MoonSharp: a loop body that declares a local, then ends with a label its
/// <c>goto</c>s jump to (<c>for … do local x … goto continue … ::continue:: end</c>). Lua 5.2 and later allow the jump
/// (a label at the very end of a block is outside the locals' scope), but MoonSharp refuses it ("jumps into the scope of
/// local 'x'"), so the whole file failed to compile (Donkey Kong 64's, Anodyne's logic, Cat Quest's autotracking). The
/// body up to the label is wrapped in <c>do … end</c>, so the locals end before the label and the jump is plain.
/// </summary>
/// <remarks>
/// Line breaks are kept (the inserted words sit on existing lines), so errors name the same lines. Code without such a
/// label is returned as it was, character for character. Never throws: source that can't be tokenized (unbalanced
/// brackets) is returned unchanged for the compiler to report.
/// </remarks>
public static class LuaGoto
{
    private static readonly HashSet<string> Closers = new(StringComparer.Ordinal) { "end", "until", "else", "elseif" };

    /// <summary>The source with each block that ends in a jumped-to label wrapped so the jump is legal; the same string when there is none.</summary>
    public static string Rewrite(string source) => Rewrite(source, out _);

    /// <summary>The source with each block that ends in a jumped-to label wrapped so the jump is legal, and how many blocks were.</summary>
    public static string Rewrite(string source, out int wrapped)
    {
        wrapped = 0;
        if (string.IsNullOrEmpty(source) || !source.Contains("::", StringComparison.Ordinal) || !source.Contains("goto", StringComparison.Ordinal)) return source;
        List<Lua53Operators.Token> tokens;
        string trailing;
        try { (tokens, trailing) = Lua53Operators.Tokenize(source); }
        catch (FormatException) { return source; }

        var after = new Dictionary<int, string>();
        var before = new Dictionary<int, string>();
        var blocks = new Stack<Block>();
        blocks.Push(new Block(-1, "chunk"));
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == Lua53Operators.Kind.Keyword)
            {
                switch (t.Text)
                {
                    case "function":
                        // The body starts after the parameter list's closing bracket.
                        int open = i + 1;
                        while (open < tokens.Count && !(tokens[open].Kind == Lua53Operators.Kind.Open && tokens[open].Text == "(")) open++;
                        if (open >= tokens.Count || tokens[open].Match < 0) return source;
                        blocks.Push(new Block(tokens[open].Match, "function"));
                        i = tokens[open].Match;
                        break;
                    case "do":
                    case "then":
                    case "repeat":
                        blocks.Push(new Block(i, t.Text));
                        break;
                    case "else":
                        if (blocks.Count > 1) blocks.Pop();
                        blocks.Push(new Block(i, "else"));
                        break;
                    case "elseif":
                    case "end":
                    case "until":
                        if (blocks.Count > 1) blocks.Pop();
                        break;
                    case "local":
                        blocks.Peek().HasLocal = true;
                        break;
                    case "goto":
                        if (i + 1 < tokens.Count && tokens[i + 1].Kind == Lua53Operators.Kind.Name)
                            foreach (var block in blocks) block.Gotos.Add(tokens[i + 1].Text);
                        break;
                }
                continue;
            }
            if (t.Kind != Lua53Operators.Kind.Symbol || t.Text != "::" || i + 2 >= tokens.Count
                || tokens[i + 1].Kind != Lua53Operators.Kind.Name || !(tokens[i + 2].Kind == Lua53Operators.Kind.Symbol && tokens[i + 2].Text == "::"))
                continue;
            string label = tokens[i + 1].Text;
            var next = i + 3 < tokens.Count ? tokens[i + 3] : null;
            bool atEnd = next == null || (next.Kind == Lua53Operators.Kind.Keyword && Closers.Contains(next.Text));
            var current = blocks.Peek();
            // A repeat's condition can see the body's locals, so its body can't be wrapped; the chunk has no opener.
            if (atEnd && current.HasLocal && current.Opener >= 0 && current.Kind != "repeat" && current.Gotos.Contains(label) && !after.ContainsKey(current.Opener))
            {
                after[current.Opener] = " do";
                before[i] = "end ";
                wrapped++;
            }
            i += 2;
        }
        if (wrapped == 0) return source;

        var sb = new StringBuilder(source.Length + wrapped * 8);
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            sb.Append(t.Lead);
            if (before.TryGetValue(i, out string? b)) sb.Append(b);
            sb.Append(t.Text);
            if (after.TryGetValue(i, out string? a)) sb.Append(a);
        }
        return sb.Append(trailing).ToString();
    }

    private sealed class Block
    {
        public readonly int Opener;
        public readonly string Kind;
        public bool HasLocal;
        public readonly HashSet<string> Gotos = new(StringComparer.Ordinal);

        public Block(int opener, string kind)
        {
            Opener = opener;
            Kind = kind;
        }
    }
}
