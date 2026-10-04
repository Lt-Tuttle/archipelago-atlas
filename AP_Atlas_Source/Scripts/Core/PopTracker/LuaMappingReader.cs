using System;
using System.Collections.Generic;
using System.Text;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>
    /// Reads the data tables in PopTracker autotracking scripts (item_mapping.lua, location_mapping.lua) without
    /// running Lua. Understands the shapes packs use in practice:
    ///   [100824] = {"banner", "toggle"},              -- id → code, type
    ///   [102640] = {{"Rosabeth of Melfia", "toggle"}}, -- id → list of {code, type}
    ///   [100000] = {"@Area/Pin/Section"},              -- id → location path(s)
    ///   {"@Area/Pin/Section"},                         -- unkeyed path list (matched by name)
    /// Only the first table assigned in the file is read; code around it is ignored.
    /// </summary>
    public static class LuaMappingReader
    {
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
        }

        public static Result Read(string text)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(text)) { result.Problems.Add("The file is empty."); return result; }
            var p = new Parser(text);
            // Find "NAME = {" and parse that table.
            int start = p.FindTableAssignment();
            if (start < 0) { result.Problems.Add("No table assignment (NAME = { ... }) was found."); return result; }
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
                            string key = p.Peek() == '"' || p.Peek() == '\'' ? p.ReadString() : p.ReadBareword();
                            p.SkipTrivia();
                            p.Expect(']');
                            p.SkipTrivia();
                            p.Expect('=');
                            if (long.TryParse(key, out long id)) entry.Id = id;
                            else result.Problems.Add($"Entry with non-numeric key [{key}] near line {p.LineAt(entryStart)} was read without an id.");
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
                        result.Problems.Add($"Couldn't read the entry near line {p.LineAt(entryStart)}: {ex.Message}");
                        p.Pos = entryStart;
                        p.SkipToNextEntry();
                    }
                    p.SkipTrivia();
                    if (p.Peek() == ',' || p.Peek() == ';') p.Pos++;
                }
            }
            catch (FormatException ex)
            {
                result.Problems.Add("The table is malformed: " + ex.Message);
            }
            return result;
        }

        private sealed class Parser
        {
            private readonly string _s;
            public int Pos;

            public Parser(string s) { _s = s; }

            public bool AtEnd => Pos >= _s.Length;
            public char Peek() => Pos < _s.Length ? _s[Pos] : '\0';

            public int LineAt(int index)
            {
                int line = 1;
                for (int i = 0; i < index && i < _s.Length; i++) if (_s[i] == '\n') line++;
                return line;
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

            public int FindTableAssignment()
            {
                // The first "= {" outside comments and strings.
                Pos = 0;
                while (!AtEnd)
                {
                    SkipTrivia();
                    char c = Peek();
                    if (c == '"' || c == '\'') { ReadString(); continue; }
                    if (c == '=')
                    {
                        Pos++;
                        SkipTrivia();
                        if (Peek() == '{') return Pos;
                        continue;
                    }
                    Pos++;
                }
                return -1;
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
                        char e = _s[Pos++];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                    }
                    else if (c == '\n') throw new FormatException("unterminated string");
                    else sb.Append(c);
                }
                if (AtEnd) throw new FormatException("unterminated string");
                Pos++;
                return sb.ToString();
            }

            public string ReadBareword()
            {
                int start = Pos;
                while (!AtEnd && (char.IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '.' || Peek() == '-')) Pos++;
                if (Pos == start) throw new FormatException($"unexpected '{Peek()}'");
                return _s.Substring(start, Pos - start);
            }

            /// <summary>Reads one value (string, number, bareword or table) and collects every string literal in it.</summary>
            public void ReadValueStrings(List<string> into)
            {
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
                        ReadValueStrings(into);
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
                    if (c == '"' || c == '\'') { try { ReadString(); } catch (FormatException) { Pos++; } continue; }
                    if (c == '{') depth++;
                    else if (c == '}') { if (depth == 0) return; depth--; }
                    else if ((c == ',' || c == '\n') && depth == 0) { Pos++; return; }
                    Pos++;
                }
            }
        }
    }
}
