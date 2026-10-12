#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace AP_Atlas.Core;

/// <summary>
/// Lua 5.3's floor division and bitwise operators (<c>a // b</c>, <c>a &amp; b</c>, <c>a | b</c>, <c>a ~ b</c>,
/// <c>a &lt;&lt; b</c>, <c>a &gt;&gt; b</c>, <c>~a</c>) written as calls of functions Atlas gives a pack's scripts, so that
/// MoonSharp, which compiles Lua 5.2, can run them. PopTracker runs Lua 5.4, and packs use these operators: a file that
/// uses one didn't compile at all, so everything in it was lost (Ship of Harkinian's whole logic, A Link to the Past's
/// helpers, the Mega Man X packs' autotracking).
/// </summary>
/// <remarks>
/// Each operator's operands are found from Lua's precedence (an operand runs to the next operator that binds less tightly,
/// over brackets, calls, fields and unary operators), the innermost brackets first and the tighter operators first, so
/// <c>(a | b) &amp; c &lt;&lt; 2</c> becomes <c>band(bor(a, b), shl(c, 2))</c>. Code without these operators is returned
/// as it was, character for character; line breaks are kept, so errors name the same lines. Linear in the source's length
/// (a doubly linked token list), and never throws: an operator whose operands can't be told is left as written (the
/// compiler then reports it, as before), and source with an absurd number of them is returned unchanged.
/// </remarks>
public static class Lua53Operators
{
    /// <summary>The functions the rewritten code calls (globals of the pack's scripts).</summary>
    public const string FloorDivide = "__atlas_idiv", And = "__atlas_band", Or = "__atlas_bor", Xor = "__atlas_bxor",
        ShiftLeft = "__atlas_shl", ShiftRight = "__atlas_shr", Not = "__atlas_bnot";

    /// <summary>More operators than this in one file and the file is left as it is (real packs have a few dozen).</summary>
    public const int MaxRewrites = 20000;

    private enum Kind { Name, Keyword, Number, String, Symbol, Open, Close, Operand }

    private sealed class Token
    {
        public Kind Kind;
        public string Text = "";
        public string Lead = ""; // the whitespace and comments before it
        public int Depth;
        public int Match = -1; // a bracket's partner
        public List<Part>? Parts; // a rewritten operand: its pieces, written out once at the end (so nesting costs nothing)
    }

    /// <summary>A piece of a rewritten operand: some text, or a token (with or without the whitespace before it).</summary>
    private readonly record struct Part(string? Text, Token? Token, bool WithLead);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil", "not",
        "or", "repeat", "return", "then", "true", "until", "while",
    };

    /// <summary>The source with Lua 5.3's operators as calls; the same string when it has none.</summary>
    public static string Rewrite(string source) => Rewrite(source, out _);

    /// <summary>The source with Lua 5.3's operators as calls, and how many were rewritten.</summary>
    public static string Rewrite(string source, out int rewritten)
    {
        rewritten = 0;
        if (string.IsNullOrEmpty(source) || source.IndexOfAny(new[] { '&', '|', '~', '<', '>', '/' }) < 0) return source;
        List<Token> tokens;
        string trailing;
        try { (tokens, trailing) = Tokenize(source); }
        catch (FormatException) { return source; } // unbalanced brackets: the compiler says where
        // The operators to rewrite, by class (tightest first): unary ~, //, << and >>, &, binary ~, |.
        var byClass = new List<int>[6];
        for (int c = 0; c < byClass.Length; c++) byClass[c] = new List<int>();
        int found = 0, deepest = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Depth > deepest) deepest = t.Depth;
            if (t.Kind != Kind.Symbol) continue;
            int cls = t.Text switch
            {
                "~" => i > 0 && EndsOperand(tokens[i - 1]) ? 4 : 0,
                "//" => 1,
                "<<" or ">>" => 2,
                "&" => 3,
                "|" => 5,
                _ => -1
            };
            if (cls < 0) continue;
            byClass[cls].Add(i);
            if (++found > MaxRewrites) return source;
        }
        if (found == 0) return source;

        var list = new TokenList(tokens);
        // Innermost brackets first; at each depth the tightest operators first; unary ~ right to left (~~x), the binary
        // ones left to right (they're left-associative: the left operand holds the earlier ones, already rewritten).
        for (int depth = deepest; depth >= 0; depth--)
        {
            for (int cls = 0; cls < byClass.Length; cls++)
            {
                var ops = byClass[cls];
                if (cls == 0)
                {
                    for (int k = ops.Count - 1; k >= 0; k--)
                        if (tokens[ops[k]].Depth == depth && list.Alive(ops[k]) && list.RewriteUnary(ops[k])) rewritten++;
                }
                else
                {
                    foreach (int op in ops)
                        if (tokens[op].Depth == depth && list.Alive(op) && list.RewriteBinary(op)) rewritten++;
                }
            }
        }
        return rewritten == 0 ? source : list.Text(trailing);
    }

    private static bool EndsOperand(Token t) => t.Kind is Kind.Name or Kind.Number or Kind.String or Kind.Close or Kind.Operand
        || (t.Kind == Kind.Keyword && t.Text is "nil" or "true" or "false") || (t.Kind == Kind.Symbol && t.Text == "...");

    // ---------------------------------------------------------------- tokens

    private static (List<Token> Tokens, string Trailing) Tokenize(string s)
    {
        var tokens = new List<Token>();
        var open = new Stack<int>();
        int i = 0, n = s.Length, leadStart = 0, depth = 0;

        void Add(Kind kind, int start, int end)
        {
            var t = new Token { Kind = kind, Text = s.Substring(start, end - start), Lead = s.Substring(leadStart, start - leadStart), Depth = depth };
            if (kind == Kind.Open)
            {
                open.Push(tokens.Count);
                depth++;
            }
            else if (kind == Kind.Close)
            {
                if (open.Count == 0) throw new FormatException();
                int o = open.Pop();
                depth--;
                t.Depth = depth;
                t.Match = o;
                tokens[o].Match = tokens.Count;
            }
            tokens.Add(t);
            leadStart = end;
        }

        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                i += 2;
                int level = LongBracket(s, i);
                if (level >= 0) i = SkipLong(s, i, level);
                else
                {
                    while (i < n && s[i] != '\n') i++;
                }
                continue;
            }
            int start = i;
            if (c == '[' && LongBracket(s, i) is int lb and >= 0)
            {
                i = SkipLong(s, i, lb);
                Add(Kind.String, start, i);
                continue;
            }
            if (c == '"' || c == '\'')
            {
                i++;
                while (i < n && s[i] != c && s[i] != '\n')
                {
                    if (s[i] == '\\') i++; // an escape: the next character (a line break too) belongs to it
                    i++;
                }
                i = Math.Min(n, i + 1);
                Add(Kind.String, start, i);
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                string word = s.Substring(start, i - start);
                Add(Keywords.Contains(word) ? Kind.Keyword : Kind.Name, start, i);
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                bool hex = c == '0' && i + 1 < n && (s[i + 1] == 'x' || s[i + 1] == 'X');
                i++;
                while (i < n)
                {
                    char d = s[i];
                    if (char.IsLetterOrDigit(d) || d == '_' || d == '.') i++;
                    else if ((d == '+' || d == '-') && (hex ? s[i - 1] is 'p' or 'P' : s[i - 1] is 'e' or 'E')) i++;
                    else break;
                }
                Add(Kind.Number, start, i);
                continue;
            }
            switch (c)
            {
                case '(':
                case '[':
                case '{':
                    i++;
                    Add(Kind.Open, start, i);
                    continue;
                case ')':
                case ']':
                case '}':
                    i++;
                    Add(Kind.Close, start, i);
                    continue;
            }
            if (c == '.' && i + 2 < n && s[i + 1] == '.' && s[i + 2] == '.') i += 3;
            else if (i + 1 < n && IsPair(c, s[i + 1])) i += 2;
            else i++;
            Add(Kind.Symbol, start, i);
        }
        if (open.Count > 0) throw new FormatException();
        return (tokens, s.Substring(leadStart));
    }

    private static bool IsPair(char a, char b) =>
        (a, b) is ('=', '=') or ('~', '=') or ('<', '=') or ('>', '=') or ('/', '/') or (':', ':') or ('<', '<') or ('>', '>') or ('.', '.');

    private static int LongBracket(string s, int at)
    {
        if (at >= s.Length || s[at] != '[') return -1;
        int i = at + 1, level = 0;
        while (i < s.Length && s[i] == '=') { level++; i++; }
        return i < s.Length && s[i] == '[' ? level : -1;
    }

    private static int SkipLong(string s, int at, int level)
    {
        string close = "]" + new string('=', level) + "]";
        int end = s.IndexOf(close, at + level + 2, StringComparison.Ordinal);
        return end < 0 ? s.Length : end + close.Length;
    }

    // ---------------------------------------------------------------- the rewriting

    /// <summary>The tokens as a doubly linked list, so a rewritten operator and its operands become one token at once.</summary>
    private sealed class TokenList
    {
        private readonly List<Token> _t;
        private readonly int[] _next, _prev;
        private readonly bool[] _dead;

        public TokenList(List<Token> tokens)
        {
            _t = tokens;
            int n = tokens.Count;
            _next = new int[n];
            _prev = new int[n];
            _dead = new bool[n];
            for (int i = 0; i < n; i++)
            {
                _next[i] = i + 1 < n ? i + 1 : -1;
                _prev[i] = i - 1;
            }
        }

        public bool Alive(int i) => !_dead[i];

        private int Next(int i) => i < 0 ? -1 : _next[i];
        private int Prev(int i) => i < 0 ? -1 : _prev[i];
        private Token T(int i) => _t[i];

        private bool IsEnd(int i) => i >= 0 && EndsOperand(T(i));

        private bool Is(int i, Kind kind, string text) => i >= 0 && T(i).Kind == kind && T(i).Text == text;

        // A unary operator here: not, #, or - and ~ where no operand ends just before.
        private bool IsUnary(int i)
        {
            if (i < 0) return false;
            var t = T(i);
            if (t.Kind == Kind.Keyword) return t.Text == "not";
            if (t.Kind != Kind.Symbol) return false;
            if (t.Text == "#") return true;
            return (t.Text == "-" || t.Text == "~") && !IsEnd(Prev(i));
        }

        // A binary operator's precedence (Lua 5.3: or 1 … ^ 12), or -1. Right-associative: .. and ^.
        private int Binary(int i, out bool right)
        {
            right = false;
            if (i < 0) return -1;
            var t = T(i);
            if (t.Kind == Kind.Keyword) return t.Text == "or" ? 1 : t.Text == "and" ? 2 : -1;
            if (t.Kind != Kind.Symbol) return -1;
            switch (t.Text)
            {
                case "<": case ">": case "<=": case ">=": case "~=": case "==": return 3;
                case "|": return 4;
                case "~": return IsEnd(Prev(i)) ? 5 : -1;
                case "&": return 6;
                case "<<": case ">>": return 7;
                case "..": right = true; return 8;
                case "+": return 9;
                case "-": return IsEnd(Prev(i)) ? 9 : -1;
                case "*": case "/": case "//": case "%": return 10;
                case "^": right = true; return 12;
                default: return -1;
            }
        }

        // ---- backwards: the start of the operand that ends at j (a primary with its suffixes and unary operators)
        private int OperandStart(int j)
        {
            if (!IsEnd(j)) return -1;
            int p = j;
            while (true)
            {
                var t = T(p);
                if (t.Kind == Kind.Close)
                {
                    int q = t.Match;
                    if (q < 0) return -1;
                    int before = Prev(q);
                    if (IsEnd(before) && T(before).Kind != Kind.Number) { p = before; continue; } // a call or an index
                    p = q;
                    break;
                }
                if (t.Kind == Kind.String)
                {
                    int before = Prev(p);
                    if (before >= 0 && T(before).Kind is Kind.Name or Kind.Close or Kind.Operand) { p = before; continue; } // f "x"
                    break;
                }
                if (t.Kind == Kind.Name)
                {
                    int before = Prev(p);
                    if (Is(before, Kind.Symbol, ".") || Is(before, Kind.Symbol, ":"))
                    {
                        int owner = Prev(before);
                        if (!IsEnd(owner)) return -1;
                        p = owner;
                        continue;
                    }
                    break;
                }
                break; // a number, a literal, an operand already rewritten
            }
            while (IsUnary(Prev(p))) p = Prev(p);
            return p;
        }

        private int LeftStart(int op, int precedence)
        {
            int j = Prev(op);
            while (true)
            {
                int start = OperandStart(j);
                if (start < 0) return -1;
                int k = Prev(start);
                int p = Binary(k, out bool right);
                if (p > precedence || (p == precedence && !right)) { j = Prev(k); continue; }
                return start;
            }
        }

        // ---- forwards: the end of the operand that starts at j
        private int OperandEnd(int j)
        {
            int p = j;
            while (p >= 0 && (T(p).Kind == Kind.Keyword && T(p).Text == "not" || T(p).Kind == Kind.Symbol && T(p).Text is "#" or "-" or "~")) p = Next(p);
            if (p < 0) return -1;
            var t = T(p);
            if (t.Kind == Kind.Open && t.Text is "(" or "{") p = t.Match;
            else if (!(t.Kind is Kind.Name or Kind.Number or Kind.String or Kind.Operand || (t.Kind == Kind.Keyword && t.Text is "nil" or "true" or "false") || Is(p, Kind.Symbol, "...")))
                return -1;
            if (p < 0) return -1;
            if (T(p).Kind is Kind.Number) return p;
            while (true)
            {
                int k = Next(p);
                if (k < 0) break;
                var s = T(k);
                if (s.Kind == Kind.Symbol && s.Text is "." or ":")
                {
                    int name = Next(k);
                    if (name < 0 || T(name).Kind != Kind.Name) return -1;
                    p = name;
                    continue;
                }
                if (s.Kind == Kind.Open)
                {
                    if (s.Match < 0) return -1;
                    p = s.Match;
                    continue;
                }
                if (s.Kind == Kind.String) { p = k; continue; }
                break;
            }
            return p;
        }

        private int RightEnd(int op, int precedence)
        {
            int j = Next(op);
            while (true)
            {
                int end = OperandEnd(j);
                if (end < 0) return -1;
                int k = Next(end);
                // After an operand, - and ~ are binary.
                int p = k < 0 ? -1 : Binary(k, out _);
                if (p > precedence) { j = Next(k); continue; }
                return end;
            }
        }

        public bool RewriteBinary(int op)
        {
            int precedence = Binary(op, out _);
            if (precedence < 0) return false;
            int left = LeftStart(op, precedence), right = RightEnd(op, precedence);
            if (left < 0 || right < 0) return false;
            string name = T(op).Text switch
            {
                "//" => FloorDivide,
                "&" => And,
                "|" => Or,
                "~" => Xor,
                "<<" => ShiftLeft,
                _ => ShiftRight
            };
            var parts = new List<Part> { new(name + "(", null, false), new(null, T(left), false) };
            for (int i = Next(left); i != op; i = Next(i)) parts.Add(new(null, T(i), true));
            parts.Add(new("," + LineBreaks(T(op).Lead), null, false));
            for (int i = Next(op); ; i = Next(i))
            {
                parts.Add(new(null, T(i), true));
                if (i == right) break;
            }
            parts.Add(new(")", null, false));
            Replace(left, right, parts);
            return true;
        }

        public bool RewriteUnary(int op)
        {
            if (!IsUnary(op)) return false;
            int right = RightEnd(op, 11);
            if (right < 0) return false;
            var parts = new List<Part> { new(Not + "(", null, false) };
            for (int i = Next(op); ; i = Next(i))
            {
                if (i == Next(op)) parts.Add(new(LineBreaks(T(i).Lead), null, false));
                parts.Add(new(null, T(i), i != Next(op)));
                if (i == right) break;
            }
            parts.Add(new(")", null, false));
            Replace(op, right, parts);
            return true;
        }

        // The tokens from first to last become one operand (keeping the first's place, lead and depth).
        private void Replace(int first, int last, List<Part> parts)
        {
            var t = T(first);
            _t[first] = new Token { Kind = Kind.Operand, Parts = parts, Lead = t.Lead, Depth = t.Depth };
            int after = Next(last);
            for (int i = Next(first); i >= 0 && i != after; i = Next(i)) _dead[i] = true;
            _next[first] = after;
            if (after >= 0) _prev[after] = first;
        }

        private static string LineBreaks(string lead)
        {
            int count = 0;
            foreach (char c in lead) if (c == '\n') count++;
            return count == 0 ? "" : new string('\n', count);
        }

        public string Text(string trailing)
        {
            var sb = new StringBuilder();
            var pending = new Stack<Part>(); // written out without recursion: operands can nest thousands deep
            for (int i = _t.Count > 0 ? 0 : -1; i >= 0; i = _next[i])
            {
                pending.Push(new(null, _t[i], true));
                while (pending.Count > 0)
                {
                    var part = pending.Pop();
                    if (part.Text != null) { sb.Append(part.Text); continue; }
                    var token = part.Token!;
                    if (part.WithLead) sb.Append(token.Lead);
                    if (token.Parts == null) { sb.Append(token.Text); continue; }
                    for (int k = token.Parts.Count - 1; k >= 0; k--) pending.Push(token.Parts[k]);
                }
            }
            return sb.Append(trailing).ToString();
        }
    }
}
