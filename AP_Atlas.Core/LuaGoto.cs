#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace AP_Atlas.Core;

/// <summary>
/// Lua's "continue" idioms made acceptable to MoonSharp.
/// <para>A loop body that declares a local, then ends with a label its <c>goto</c>s jump to (<c>for … do local x … goto
/// continue … ::continue:: end</c>): Lua 5.2 and later allow the jump (a label at the very end of a block is outside the
/// locals' scope), but MoonSharp refuses it ("jumps into the scope of local 'x'"), so the whole file failed to compile
/// (Donkey Kong 64's, Anodyne's logic, Cat Quest's autotracking). The body up to the label is wrapped in <c>do … end</c>,
/// so the locals end before the label and the jump is plain.</para>
/// <para>A <c>goto</c> that leaves a loop for the label right after it (<c>for … do … goto found … end ::found::</c>):
/// MoonSharp runs it, but leaves the loop's own values on its stack, and the enclosing loop's next step then fails with
/// "Can't assign number to type Tuple" (Oracle of Seasons' dungeon images). Such a jump is a <c>break</c>, and is written so.</para>
/// </summary>
/// <remarks>
/// Line breaks are kept (the inserted words sit on existing lines), so errors name the same lines. Code without such a
/// label is returned as it was, character for character. Never throws: source that can't be tokenized (unbalanced
/// brackets) is returned unchanged for the compiler to report.
/// </remarks>
public static class LuaGoto
{
    private static readonly HashSet<string> Closers = new(StringComparer.Ordinal) { "end", "until", "else", "elseif" };

    /// <summary>The source with each block that ends in a jumped-to label wrapped so the jump is legal, and each goto that leaves a loop for the label after it as a break; the same string when there is none.</summary>
    public static string Rewrite(string source) => Rewrite(source, out _, out _);

    /// <summary>As <see cref="Rewrite(string)"/>, and how many blocks were wrapped.</summary>
    public static string Rewrite(string source, out int wrapped) => Rewrite(source, out wrapped, out _);

    /// <summary>As <see cref="Rewrite(string)"/>, and how many blocks were wrapped and how many gotos became breaks.</summary>
    public static string Rewrite(string source, out int wrapped, out int breaks)
    {
        wrapped = 0;
        breaks = 0;
        if (string.IsNullOrEmpty(source) || !source.Contains("::", StringComparison.Ordinal) || !source.Contains("goto", StringComparison.Ordinal)) return source;
        List<Lua53Operators.Token> tokens;
        string trailing;
        try { (tokens, trailing) = Lua53Operators.Tokenize(source); }
        catch (FormatException) { return source; }

        var after = new Dictionary<int, string>();
        var before = new Dictionary<int, string>();
        var replace = new Dictionary<int, string>();
        var blocks = new Stack<Block>();
        blocks.Push(new Block(-1, "chunk"));
        bool loopHeader = false; // between "for"/"while" and its "do"
        Block? lastClosedLoop = null;
        int lastClosedLoopEnd = -1;
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
                    case "for":
                    case "while":
                        loopHeader = true;
                        break;
                    case "do":
                        blocks.Push(new Block(i, "do") { IsLoop = loopHeader });
                        loopHeader = false;
                        break;
                    case "then":
                        blocks.Push(new Block(i, t.Text));
                        break;
                    case "repeat":
                        blocks.Push(new Block(i, t.Text) { IsLoop = true });
                        break;
                    case "else":
                        if (blocks.Count > 1) blocks.Pop();
                        blocks.Push(new Block(i, "else"));
                        break;
                    case "elseif":
                    case "end":
                    case "until":
                        if (blocks.Count > 1)
                        {
                            var closed = blocks.Pop();
                            // A label right after a loop's "end" is where its gotos may leave it (a repeat's "until" has its condition after it).
                            if (closed.IsLoop && t.Text == "end") { lastClosedLoop = closed; lastClosedLoopEnd = i; }
                        }
                        break;
                    case "local":
                        blocks.Peek().HasLocal = true;
                        break;
                    case "goto":
                        if (i + 1 < tokens.Count && tokens[i + 1].Kind == Lua53Operators.Kind.Name)
                        {
                            foreach (var block in blocks) block.Gotos.Add(tokens[i + 1].Text);
                            // The loop this goto is directly in (nested ifs don't matter; a nested loop or function does).
                            foreach (var block in blocks)
                            {
                                if (block.Kind == "function") break;
                                if (block.IsLoop) { block.GotoTokens.Add(i); break; }
                            }
                        }
                        break;
                }
                continue;
            }
            if (t.Kind != Lua53Operators.Kind.Symbol || t.Text != "::" || i + 2 >= tokens.Count
                || tokens[i + 1].Kind != Lua53Operators.Kind.Name || !(tokens[i + 2].Kind == Lua53Operators.Kind.Symbol && tokens[i + 2].Text == "::"))
                continue;
            string label = tokens[i + 1].Text;
            // The label right after a loop: the gotos to it from directly inside that loop are breaks.
            if (lastClosedLoop != null && lastClosedLoopEnd == i - 1)
            {
                foreach (int g in lastClosedLoop.GotoTokens)
                {
                    if (tokens[g + 1].Text != label) continue;
                    replace[g] = "break";
                    replace[g + 1] = "";
                    breaks++;
                }
            }
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
        if (wrapped == 0 && breaks == 0) return source;

        var sb = new StringBuilder(source.Length + wrapped * 8);
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (replace.TryGetValue(i, out string? r))
            {
                // "goto name" becomes "break": the name's own lead and text go, so the line reads as one word.
                if (r.Length > 0) sb.Append(t.Lead).Append(r);
                continue;
            }
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
        public bool IsLoop;
        public readonly HashSet<string> Gotos = new(StringComparer.Ordinal);
        public readonly List<int> GotoTokens = new();

        public Block(int opener, string kind)
        {
            Opener = opener;
            Kind = kind;
        }
    }
}
