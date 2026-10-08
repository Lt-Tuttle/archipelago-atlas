#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Reads the location lists of an Archipelago player YAML (exclude_locations, and any other list option by name).
    /// The server never sends these to clients, so a local copy of the YAML is the only source.
    /// Handles the subset of YAML player files use: block maps and lists, flow lists, quotes, comments and
    /// several documents ("---") per file.
    /// </summary>
    public static class YamlExclusions
    {
        public class Result
        {
            public string File { get; set; }
            public string PlayerName { get; set; }
            public List<string> Names { get; set; } = new();
            public string Error { get; set; }
        }

        /// <summary>The exclude_locations a YAML file gives a game's player (the document whose name matches slotName, if several).</summary>
        public static Result Read(string path, string game, string slotName, string option = "exclude_locations")
        {
            var result = new Result { File = path };
            string text;
            try { text = System.IO.File.ReadAllText(path); }
            catch (Exception ex) { result.Error = ex.Message; return result; }

            var docs = new List<Dictionary<string, object>>();
            foreach (var docText in SplitDocuments(text))
            {
                try
                {
                    if (Parse(docText) is Dictionary<string, object> map) docs.Add(map);
                }
                catch (Exception ex) { Logger.LogWarning($"Couldn't read part of {Path.GetFileName(path)}: {ex.Message}"); }
            }

            var forGame = docs.Where(d => PlaysGame(d, game)).ToList();
            if (forGame.Count == 0) { result.Error = $"No player in this file plays {game}."; return result; }
            var doc = forGame.FirstOrDefault(d => NameMatches(Str(Get(d, "name")), slotName)) ?? (forGame.Count == 1 ? forGame[0] : null);
            if (doc == null) { result.Error = $"Several {game} players in this file, none named {slotName}."; return result; }

            result.PlayerName = Str(Get(doc, "name"));
            var section = Get(doc, game) as Dictionary<string, object>;
            var value = section != null ? Get(section, option) : null;
            result.Names = Names(value);
            return result;
        }

        /// <summary>
        /// The players a YAML file's text holds: each document's name and the games it may roll (a weighted game map counts
        /// every game weighted above 0). Documents that name no game are left out.
        /// </summary>
        public static List<(string Name, List<string> Games)> Players(string text)
        {
            var players = new List<(string, List<string>)>();
            foreach (var docText in SplitDocuments(text ?? ""))
            {
                Dictionary<string, object> doc;
                try { doc = Parse(docText) as Dictionary<string, object>; }
                catch (Exception ex)
                {
                    Logger.LogDebug("Part of a YAML couldn't be read: " + ex.Message);
                    continue;
                }
                if (doc == null) continue;
                var games = new List<string>();
                switch (Get(doc, "game"))
                {
                    case string one when one.Trim().Length > 0:
                        games.Add(one.Trim());
                        break;
                    case Dictionary<string, object> weights:
                        games.AddRange(weights.Where(kv => kv.Key.Trim().Length > 0 && Str(kv.Value) is string w && w.Trim() != "0").Select(kv => kv.Key.Trim()));
                        break;
                }
                if (games.Count > 0) players.Add((Str(Get(doc, "name")) ?? "", games));
            }
            return players;
        }

        /// <summary>Whether a YAML name (which may hold {player} / {number} placeholders) can produce this slot name.</summary>
        public static bool NameMatches(string yamlName, string slotName)
        {
            if (string.IsNullOrEmpty(yamlName) || string.IsNullOrEmpty(slotName)) return false;
            if (string.Equals(yamlName, slotName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!yamlName.Contains('{')) return false;
            string pattern = "^" + Regex.Replace(Regex.Escape(yamlName), @"\\\{[A-Za-z]+\}", ".*") + "$";
            try { return Regex.IsMatch(slotName, pattern, RegexOptions.IgnoreCase); }
            catch { return false; }
        }

        private static bool PlaysGame(Dictionary<string, object> doc, string game)
        {
            var g = Get(doc, "game");
            if (g is string s) return string.Equals(s, game, StringComparison.OrdinalIgnoreCase);
            // Weighted: { "Game A": 50, "Game B": 0 }
            if (g is Dictionary<string, object> weights)
                return weights.Any(kv => string.Equals(kv.Key, game, StringComparison.OrdinalIgnoreCase) && Str(kv.Value) != "0");
            return false;
        }

        private static List<string> Names(object value)
        {
            var names = new List<string>();
            switch (value)
            {
                case List<object> list:
                    foreach (var v in list)
                    {
                        if (Str(v) is string n && n.Length > 0) names.Add(n);
                        // An unquoted "- FS: Coiled Sword" reads as a one-entry map; put the name back together.
                        else if (v is Dictionary<string, object> one && one.Count == 1 && Str(one.First().Value) is string tail)
                            names.Add(one.First().Key + ": " + tail);
                    }
                    break;
                case Dictionary<string, object> map: // a set written as a map
                    names.AddRange(map.Keys.Where(k => k.Length > 0));
                    break;
                case string one when one.Length > 0:
                    names.Add(one);
                    break;
            }
            return names.Distinct().ToList();
        }

        private static object Get(Dictionary<string, object> map, string key) =>
            map.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        private static string Str(object o) => o as string;

        // =====================================================================
        // Minimal YAML
        // =====================================================================

        private static IEnumerable<string> SplitDocuments(string text)
        {
            var current = new StringBuilder();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.TrimEnd() == "---" || raw.StartsWith("--- "))
                {
                    if (current.Length > 0) yield return current.ToString();
                    current.Clear();
                    continue;
                }
                if (raw.TrimEnd() == "...") continue;
                current.Append(raw).Append('\n');
            }
            if (current.Length > 0) yield return current.ToString();
        }

        private record Line(int Indent, string Text);

        private static object Parse(string doc)
        {
            var lines = new List<Line>();
            foreach (var raw in doc.Split('\n'))
            {
                string noComment = StripComment(raw.Replace("\t", "  ")).TrimEnd();
                if (noComment.Trim().Length == 0) continue;
                int indent = noComment.Length - noComment.TrimStart().Length;
                lines.Add(new Line(indent, noComment.Trim()));
            }
            if (lines.Count == 0) return null;
            int i = 0;
            return ParseBlock(lines, ref i, lines[0].Indent);
        }

        private static bool IsSeqItem(string t) => t == "-" || t.StartsWith("- ");

        private static object ParseBlock(List<Line> lines, ref int i, int indent)
        {
            if (i >= lines.Count) return null;
            return IsSeqItem(lines[i].Text) ? ParseSequence(lines, ref i, indent) : ParseMap(lines, ref i, indent);
        }

        private static List<object> ParseSequence(List<Line> lines, ref int i, int indent)
        {
            var list = new List<object>();
            while (i < lines.Count && lines[i].Indent == indent && IsSeqItem(lines[i].Text))
            {
                string item = lines[i].Text.Length > 1 ? lines[i].Text.Substring(2).Trim() : "";
                i++;
                if (item.Length == 0)
                {
                    list.Add(i < lines.Count && lines[i].Indent > indent ? ParseBlock(lines, ref i, lines[i].Indent) : null);
                }
                else if (SplitKey(item) is (string key, string rest) && !item.StartsWith("\"") && !item.StartsWith("'") && !item.StartsWith("[") && !item.StartsWith("{"))
                {
                    // "- key: value" starts a map inside the list.
                    var map = new Dictionary<string, object> { [key] = rest.Length > 0 ? Inline(rest) : null };
                    if (i < lines.Count && lines[i].Indent > indent && !IsSeqItem(lines[i].Text))
                    {
                        if (ParseMap(lines, ref i, lines[i].Indent) is Dictionary<string, object> more)
                            foreach (var kv in more) map[kv.Key] = kv.Value;
                    }
                    list.Add(map);
                }
                else list.Add(Inline(item));
                SkipDeeper(lines, ref i, indent);
            }
            return list;
        }

        private static Dictionary<string, object> ParseMap(List<Line> lines, ref int i, int indent)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (i < lines.Count && lines[i].Indent == indent && !IsSeqItem(lines[i].Text))
            {
                var split = SplitKey(lines[i].Text);
                i++;
                if (split == null) { SkipDeeper(lines, ref i, indent); continue; }
                var (key, rest) = split.Value;
                object value;
                if (rest.Length > 0 && rest != "|" && rest != ">" && !rest.StartsWith("|") && !rest.StartsWith(">"))
                {
                    value = Inline(rest);
                    SkipDeeper(lines, ref i, indent);
                }
                else if (rest.Length > 0)
                {
                    // Block text: not needed here.
                    value = "";
                    SkipDeeper(lines, ref i, indent);
                }
                else if (i < lines.Count && lines[i].Indent > indent) value = ParseBlock(lines, ref i, lines[i].Indent);
                else if (i < lines.Count && lines[i].Indent == indent && IsSeqItem(lines[i].Text)) value = ParseSequence(lines, ref i, indent);
                else value = null;
                map[key] = value;
            }
            return map;
        }

        private static void SkipDeeper(List<Line> lines, ref int i, int indent)
        {
            while (i < lines.Count && lines[i].Indent > indent) i++;
        }

        /// <summary>"key: rest" (key may be quoted). Null when the line isn't a key.</summary>
        private static (string Key, string Value)? SplitKey(string text)
        {
            int start = 0;
            string key;
            int after;
            if (text.StartsWith("\"") || text.StartsWith("'"))
            {
                int end = FindClosingQuote(text, 0);
                if (end < 0) return null;
                key = Unquote(text.Substring(0, end + 1));
                after = end + 1;
                if (after >= text.Length || text[after] != ':') return null;
            }
            else
            {
                int colon = -1;
                for (int k = start; k < text.Length; k++)
                {
                    if (text[k] == ':' && (k == text.Length - 1 || text[k + 1] == ' ')) { colon = k; break; }
                }
                if (colon <= 0) return null;
                key = text.Substring(0, colon).Trim();
                after = colon;
            }
            return (key, text.Substring(after + 1).Trim());
        }

        private static object Inline(string v)
        {
            v = v.Trim();
            if (v.StartsWith("[") && v.EndsWith("]")) return SplitFlow(v.Substring(1, v.Length - 2)).Select(x => (object)Unquote(x)).ToList();
            if (v.StartsWith("{") && v.EndsWith("}"))
            {
                var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in SplitFlow(v.Substring(1, v.Length - 2)))
                {
                    var kv = SplitKey(part.Trim());
                    if (kv != null) map[kv.Value.Key] = Unquote(kv.Value.Value);
                    else if (Unquote(part) is string bare) map[bare] = null;
                }
                return map;
            }
            return Unquote(v);
        }

        private static List<string> SplitFlow(string body)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            char quote = '\0';
            int depth = 0;
            foreach (char c in body)
            {
                if (quote != '\0')
                {
                    cur.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; cur.Append(c); continue; }
                if (c == '[' || c == '{') depth++;
                if (c == ']' || c == '}') depth--;
                if (c == ',' && depth == 0)
                {
                    if (cur.ToString().Trim().Length > 0) parts.Add(cur.ToString().Trim());
                    cur.Clear();
                    continue;
                }
                cur.Append(c);
            }
            if (cur.ToString().Trim().Length > 0) parts.Add(cur.ToString().Trim());
            return parts;
        }

        private static int FindClosingQuote(string s, int open)
        {
            char q = s[open];
            for (int k = open + 1; k < s.Length; k++)
            {
                if (q == '"' && s[k] == '\\') { k++; continue; }
                if (s[k] == q)
                {
                    if (q == '\'' && k + 1 < s.Length && s[k + 1] == '\'') { k++; continue; } // '' escape
                    return k;
                }
            }
            return -1;
        }

        private static string Unquote(string v)
        {
            v = v.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
                return Regex.Unescape(v.Substring(1, v.Length - 2));
            if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'')
                return v.Substring(1, v.Length - 2).Replace("''", "'");
            if (v == "~" || v == "null") return null;
            return v;
        }

        private static string StripComment(string line)
        {
            char quote = '\0';
            for (int k = 0; k < line.Length; k++)
            {
                char c = line[k];
                if (quote != '\0')
                {
                    if (quote == '"' && c == '\\') { k++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    // A quote only opens a string at the start of a value, not inside a word like "Siegward's".
                    if (k == 0 || " :[{,-".IndexOf(line[k - 1]) >= 0) quote = c;
                    continue;
                }
                if (c == '#' && (k == 0 || line[k - 1] == ' ')) return line.Substring(0, k);
            }
            return line;
        }
    }
}
