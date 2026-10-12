using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AP_Atlas.Core.PopTracker;

/// <summary>
/// Reads the data tables in PopTracker autotracking scripts (item_mapping.lua, location_mapping.lua) without
/// running Lua. Understands the shapes packs use in practice:
///   [100824] = {"banner", "toggle"},              -- id → code, type
///   [102640] = {{"Rosabeth of Melfia", "toggle"}}, -- id → list of {code, type}
///   [100000] = {"@Area/Pin/Section"},              -- id → location path(s)
///   [0x69696A] = {"@Area/Pin/Section"},            -- a hexadecimal id
///   [BASE + 12] = {"@Area/Pin/Section"},           -- arithmetic on numbers the file sets before the table (BASE = 6242624000)
///   {"@Area/Pin/Section"},                         -- unkeyed path list (matched by name)
/// The table read is the first non-empty one whose name ends in MAPPING (LOCATION_MAPPING, AP_ITEM_MAPPING), else the
/// first table the file assigns; code around it is ignored. A mapping the pack's scripts build as they run (a loop over
/// a list) is only known by running them, which the pack's script host does.
/// </summary>
public static class LuaMappingReader
{
    /// <summary>The most unreadable entries listed one by one; the rest are counted in one line.</summary>
    public const int MaxListedProblems = 20;

    public sealed class Entry
    {
        /// <summary>The numeric key, or null for an unkeyed list entry.</summary>
        public long? Id;
        /// <summary>Every string literal in the entry's value, in order (nested tables flattened).</summary>
        public List<string> Strings = new List<string>();
    }

    public sealed class Result
    {
        public List<Entry> Entries = new List<Entry>();
        /// <summary>Problems worth showing the user (unreadable entries, no table found).</summary>
        public List<string> Problems = new List<string>();
        /// <summary>The name the table was assigned to ("LOCATION_MAPPING"), or "" when it had none.</summary>
        public string TableName = "";
    }

    public static Result Read(string text)
    {
        var result = new Result();
        if (string.IsNullOrEmpty(text)) { result.Problems.Add("The file is empty."); return result; }
        var p = new Parser(text);
        var tables = p.FindTableAssignments();
        if (tables.Count == 0) { result.Problems.Add("No table assignment (NAME = { ... }) was found."); return result; }
        Result? first = null;
        foreach (var (name, start) in tables)
        {
            var table = ReadTable(p, start, Constants(text, start));
            table.TableName = name;
            first ??= table;
            if (name.EndsWith("MAPPING", StringComparison.OrdinalIgnoreCase) && table.Entries.Count > 0) return table;
        }
        return first!;
    }

    private static Result ReadTable(Parser p, int start, IReadOnlyDictionary<string, long> constants)
    {
        var result = new Result();
        int unlisted = 0;
        void Problem(string text)
        {
            if (result.Problems.Count < MaxListedProblems) result.Problems.Add(text);
            else unlisted++;
        }
        p.Pos = start;
        try
        {
            p.Expect('{');
            while (true)
            {
                p.SkipTrivia();
                if (p.Peek() == '}' || p.AtEnd) break;
                int entryStart = p.Pos;
                try
                {
                    var entry = new Entry();
                    if (p.Peek() == '[')
                    {
                        p.Pos++;
                        p.SkipTrivia();
                        if (p.Peek() == '"' || p.Peek() == '\'')
                        {
                            string key = p.ReadString();
                            p.Expect(']');
                            p.Expect('=');
                            if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) entry.Id = id;
                            else Problem($"Entry with non-numeric key [{key}] near line {p.LineAt(entryStart)} was read without an id.");
                        }
                        else
                        {
                            string key = p.ReadKeyText();
                            p.Expect('=');
                            if (EvaluateNumber(key, constants) is long id) entry.Id = id;
                            else Problem($"Entry with non-numeric key [{key.Trim()}] near line {p.LineAt(entryStart)} was read without an id.");
                        }
                    }
                    else if (char.IsLetter(p.Peek()) || p.Peek() == '_')
                    {
                        // name = value (string-keyed field); keep the value's strings, no id.
                        int save = p.Pos;
                        p.ReadBareword();
                        p.SkipTrivia();
                        if (p.Peek() == '=') p.Pos++;
                        else p.Pos = save;
                    }
                    p.SkipTrivia();
                    p.ReadValueStrings(entry.Strings);
                    result.Entries.Add(entry);
                }
                catch (FormatException ex)
                {
                    Problem($"Couldn't read the entry near line {p.LineAt(entryStart)}: {ex.Message}");
                    p.Pos = entryStart;
                    p.SkipToNextEntry();
                }
                p.SkipTrivia();
                if (p.Peek() == ',' || p.Peek() == ';') p.Pos++;
            }
        }
        catch (FormatException ex)
        {
            Problem("The table is malformed: " + ex.Message);
        }
        if (unlisted > 0) result.Problems.Add($"…and {unlisted} more entries that couldn't be read.");
        return result;
    }

    /// <summary>
    /// The numbers a file sets before a table ("BASE = 6242624000", "local OFFSET = BASE + 300"), each line's
    /// arithmetic worked out from the numbers set before it, as Lua would when it builds the table.
    /// </summary>
    internal static Dictionary<string, long> Constants(string text, int end)
    {
        var constants = new Dictionary<string, long>(StringComparer.Ordinal);
        string head = LuaText.StripComments(text.Substring(0, Math.Clamp(end, 0, text.Length)));
        foreach (string raw in head.Split('\n'))
        {
            string line = raw.Trim().TrimEnd(';').Trim();
            if (line.StartsWith("local ", StringComparison.Ordinal)) line = line.Substring(6).TrimStart();
            int eq = line.IndexOf('=');
            if (eq <= 0 || (eq + 1 < line.Length && line[eq + 1] == '=')) continue;
            string name = line.Substring(0, eq).Trim();
            if (!IsIdentifier(name)) continue;
            if (EvaluateNumber(line.Substring(eq + 1), constants) is long value) constants[name] = value;
        }
        return constants;
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || !(IsLetter(s[0]) || s[0] == '_')) return false;
        foreach (char c in s) if (!(IsLetter(c) || IsDigit(c) || c == '_')) return false;
        return true;
    }

    private static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    /// <summary>
    /// A whole number written as Lua arithmetic on integers ("6242624000", "0x69696A", "BASE + 12", "(BASE + 3) * 2",
    /// "-1"), the names taken from <paramref name="constants"/>; null when it's anything else (a variable the file doesn't
    /// set, a string, a call) or doesn't fit in 64 bits.
    /// </summary>
    public static long? EvaluateNumber(string expression, IReadOnlyDictionary<string, long> constants)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 200) return null;
        var arithmetic = new Arithmetic(expression, constants);
        try
        {
            long value = arithmetic.Sum(0);
            arithmetic.SkipSpace();
            return arithmetic.AtEnd ? value : null;
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }

    /// <summary>Sums, differences and products of integers and names, with brackets: nothing else, and never deeper than 16.</summary>
    private sealed class Arithmetic
    {
        private const int MaxDepth = 16;
        private readonly string _s;
        private readonly IReadOnlyDictionary<string, long> _constants;
        private int _pos;

        public Arithmetic(string s, IReadOnlyDictionary<string, long> constants)
        {
            _s = s;
            _constants = constants;
        }

        public bool AtEnd => _pos >= _s.Length;
        private char Peek() => _pos < _s.Length ? _s[_pos] : '\0';

        public void SkipSpace()
        {
            while (!AtEnd && char.IsWhiteSpace(Peek())) _pos++;
        }

        public long Sum(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("nested too deeply");
            long value = Product(depth);
            while (true)
            {
                SkipSpace();
                if (Peek() == '+') { _pos++; value = checked(value + Product(depth)); }
                else if (Peek() == '-' && !(_pos + 1 < _s.Length && _s[_pos + 1] == '-')) { _pos++; value = checked(value - Product(depth)); }
                else return value;
            }
        }

        private long Product(int depth)
        {
            long value = Unary(depth);
            while (true)
            {
                SkipSpace();
                if (Peek() == '*') { _pos++; value = checked(value * Unary(depth)); }
                else return value;
            }
        }

        private long Unary(int depth)
        {
            SkipSpace();
            if (Peek() == '-')
            {
                if (depth > MaxDepth) throw new FormatException("nested too deeply");
                _pos++;
                return checked(-Unary(depth + 1));
            }
            return Atom(depth);
        }

        private long Atom(int depth)
        {
            SkipSpace();
            char c = Peek();
            if (c == '(')
            {
                _pos++;
                long value = Sum(depth + 1);
                SkipSpace();
                if (Peek() != ')') throw new FormatException("expected ')'");
                _pos++;
                return value;
            }
            if (IsDigit(c)) return Number();
            if (IsLetter(c) || c == '_')
            {
                int start = _pos;
                while (!AtEnd && (IsLetter(Peek()) || IsDigit(Peek()) || Peek() == '_')) _pos++;
                string name = _s.Substring(start, _pos - start);
                return _constants.TryGetValue(name, out long value) ? value : throw new FormatException($"'{name}' isn't a number the file sets");
            }
            throw new FormatException($"unexpected '{c}'");
        }

        private long Number()
        {
            if (Peek() == '0' && _pos + 1 < _s.Length && (_s[_pos + 1] == 'x' || _s[_pos + 1] == 'X'))
            {
                _pos += 2;
                int hexStart = _pos;
                while (!AtEnd && Uri.IsHexDigit(Peek())) _pos++;
                if (_pos == hexStart || _pos - hexStart > 16) throw new FormatException("not a hexadecimal number");
                return long.Parse(_s.AsSpan(hexStart, _pos - hexStart), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            }
            int start = _pos;
            while (!AtEnd && IsDigit(Peek())) _pos++;
            // A fraction or an exponent ("12.5", "1e3") isn't a whole number a table is keyed by here.
            if (Peek() == '.' || Peek() == 'e' || Peek() == 'E' || IsLetter(Peek()) || Peek() == '_') throw new FormatException("not a whole number");
            return long.Parse(_s.AsSpan(start, _pos - start), NumberStyles.None, CultureInfo.InvariantCulture);
        }
    }

    private sealed class Parser
    {
        private readonly string _s;
        public int Pos;
        // Where LineAt last counted to, so counting the lines of entry after entry stays linear.
        private int _countedTo;
        private int _countedLines = 1;

        public Parser(string s) { _s = s; }

        public bool AtEnd => Pos >= _s.Length;
        public char Peek() => Pos < _s.Length ? _s[Pos] : '\0';

        public int LineAt(int index)
        {
            index = Math.Clamp(index, 0, _s.Length);
            if (index < _countedTo)
            {
                _countedTo = 0;
                _countedLines = 1;
            }
            for (int i = _countedTo; i < index; i++) if (_s[i] == '\n') _countedLines++;
            _countedTo = index;
            return _countedLines;
        }

        public void Expect(char c)
        {
            SkipTrivia();
            if (Peek() != c) throw new FormatException($"expected '{c}' but found '{(AtEnd ? "end of file" : Peek().ToString())}'");
            Pos++;
        }

        /// <summary>Skips whitespace and Lua comments (-- line and --[[ block ]]).</summary>
        public void SkipTrivia()
        {
            while (!AtEnd)
            {
                char c = Peek();
                if (char.IsWhiteSpace(c)) { Pos++; continue; }
                if (c == '-' && Pos + 1 < _s.Length && _s[Pos + 1] == '-')
                {
                    if (Pos + 3 < _s.Length && _s[Pos + 2] == '[' && _s[Pos + 3] == '[')
                    {
                        int end = _s.IndexOf("]]", Pos + 4, StringComparison.Ordinal);
                        Pos = end < 0 ? _s.Length : end + 2;
                    }
                    else
                    {
                        int end = _s.IndexOf('\n', Pos);
                        Pos = end < 0 ? _s.Length : end + 1;
                    }
                    continue;
                }
                break;
            }
        }

        /// <summary>
        /// Every "NAME = {" outside comments and strings, in order, with the name it's assigned to ("" when there's
        /// none); a table's own contents aren't searched (its entries' "[1] = {" are part of it).
        /// </summary>
        public List<(string Name, int Start)> FindTableAssignments()
        {
            var found = new List<(string, int)>();
            Pos = 0;
            while (!AtEnd && found.Count < 64)
            {
                SkipTrivia();
                if (AtEnd) break;
                char c = Peek();
                if (c == '"' || c == '\'') { SkipString(); continue; }
                if (c == '=' && !(Pos + 1 < _s.Length && _s[Pos + 1] == '=') && !(Pos > 0 && "=~<>".IndexOf(_s[Pos - 1]) >= 0))
                {
                    int eq = Pos;
                    Pos++;
                    SkipTrivia();
                    if (Peek() == '{')
                    {
                        found.Add((NameBefore(eq), Pos));
                        SkipBalanced();
                    }
                    continue;
                }
                Pos++;
            }
            return found;
        }

        // The name an "=" assigns to: the word (with dots, as in "a.b") just before it.
        private string NameBefore(int eq)
        {
            int end = eq;
            while (end > 0 && char.IsWhiteSpace(_s[end - 1])) end--;
            int start = end;
            while (start > 0 && (IsLetter(_s[start - 1]) || IsDigit(_s[start - 1]) || _s[start - 1] == '_' || _s[start - 1] == '.')) start--;
            return _s.Substring(start, end - start);
        }

        // From a "{", past its matching "}" (strings and comments inside left alone).
        private void SkipBalanced()
        {
            int depth = 0;
            while (!AtEnd)
            {
                SkipTrivia();
                if (AtEnd) return;
                char c = Peek();
                if (c == '"' || c == '\'') { SkipString(); continue; }
                Pos++;
                if (c == '{') depth++;
                else if (c == '}' && --depth <= 0) return;
            }
        }

        private void SkipString()
        {
            try { ReadString(); }
            catch (FormatException) { Pos++; }
        }

        public string ReadString()
        {
            char quote = Peek();
            Pos++;
            var sb = new StringBuilder();
            while (!AtEnd && Peek() != quote)
            {
                char c = _s[Pos++];
                if (c == '\\' && !AtEnd)
                {
                    // Lua's escapes: the named ones, \xHH, \ddd, \u{XXXX} (5.3; packs use it for accents), \z (skips
                    // the following whitespace), and a backslash before a line break (the break is kept).
                    char e = _s[Pos++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'a': sb.Append('\a'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'v': sb.Append('\v'); break;
                        case '\n': sb.Append('\n'); break;
                        case 'z': while (!AtEnd && char.IsWhiteSpace(Peek())) Pos++; break;
                        case 'x':
                            if (Pos + 1 < _s.Length && Uri.IsHexDigit(_s[Pos]) && Uri.IsHexDigit(_s[Pos + 1]))
                            {
                                sb.Append((char)Convert.ToInt32(_s.Substring(Pos, 2), 16));
                                Pos += 2;
                            }
                            else sb.Append('x');
                            break;
                        case 'u':
                            if (Pos < _s.Length && _s[Pos] == '{')
                            {
                                int close = _s.IndexOf('}', Pos);
                                if (close > Pos + 1 && close - Pos - 1 <= 8 && int.TryParse(_s.AsSpan(Pos + 1, close - Pos - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int point)
                                    && point <= 0x10FFFF && (point < 0xD800 || point > 0xDFFF))
                                {
                                    sb.Append(char.ConvertFromUtf32(point));
                                    Pos = close + 1;
                                }
                                else sb.Append('u');
                            }
                            else sb.Append('u');
                            break;
                        default:
                            if (IsDigit(e))
                            {
                                int value = e - '0', digits = 1;
                                while (digits < 3 && !AtEnd && IsDigit(Peek())) { value = value * 10 + (_s[Pos++] - '0'); digits++; }
                                sb.Append((char)Math.Min(value, 255));
                            }
                            else sb.Append(e);
                            break;
                    }
                }
                else if (c == '\n') throw new FormatException("unterminated string");
                else sb.Append(c);
            }
            if (AtEnd) throw new FormatException("unterminated string");
            Pos++;
            return sb.ToString();
        }

        /// <summary>The text of a table key up to its closing "]", which is passed.</summary>
        public string ReadKeyText()
        {
            int start = Pos, depth = 0;
            while (!AtEnd && Peek() != '\n' && Pos - start <= 200)
            {
                char c = Peek();
                if (c == '[') depth++;
                else if (c == ']')
                {
                    if (depth == 0)
                    {
                        string key = _s.Substring(start, Pos - start);
                        Pos++;
                        return key;
                    }
                    depth--;
                }
                Pos++;
            }
            throw new FormatException("unclosed '['");
        }

        public string ReadBareword()
        {
            int start = Pos;
            while (!AtEnd && (char.IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '.' || Peek() == '-')) Pos++;
            if (Pos == start) throw new FormatException($"unexpected '{Peek()}'");
            return _s.Substring(start, Pos - start);
        }

        /// <summary>
        /// Reads one value (string, number, bareword or table) and collects every string literal in it. A value nested
        /// deeper than any mapping needs is refused, so a crafted file can't use up the stack.
        /// </summary>
        public void ReadValueStrings(List<string> into, int depth = 0)
        {
            if (depth > 64) throw new FormatException("nested too deeply");
            SkipTrivia();
            char c = Peek();
            if (c == '"' || c == '\'') { into.Add(ReadString()); return; }
            if (c == '{')
            {
                Pos++;
                while (true)
                {
                    SkipTrivia();
                    if (AtEnd) throw new FormatException("unclosed '{'");
                    if (Peek() == '}') { Pos++; return; }
                    if (Peek() == '[')
                    {
                        // [key] = value inside a nested table: skip the key, keep the value.
                        int close = _s.IndexOf(']', Pos);
                        if (close < 0) throw new FormatException("unclosed '['");
                        Pos = close + 1;
                        Expect('=');
                    }
                    ReadValueStrings(into, depth + 1);
                    SkipTrivia();
                    if (Peek() == ',' || Peek() == ';') Pos++;
                }
            }
            // number, bareword (true/false/nil/identifier): skip it.
            ReadBareword();
        }

        /// <summary>Recovers from a bad entry by skipping to the next top-level comma.</summary>
        public void SkipToNextEntry()
        {
            int depth = 0;
            while (!AtEnd)
            {
                char c = Peek();
                if (c == '"' || c == '\'') { SkipString(); continue; }
                if (c == '{') depth++;
                else if (c == '}') { if (depth == 0) return; depth--; }
                else if ((c == ',' || c == '\n') && depth == 0) { Pos++; return; }
                Pos++;
            }
        }
    }
}
