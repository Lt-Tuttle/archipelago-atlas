using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AP_Atlas.Core.PopTracker;

/// <summary>A pin section as fix by example sees it: its finding's key, the pin's path and name, the section's name ("" for none).</summary>
public sealed record PatternRow(string Key, string PinPath, string PinName, string Section);

/// <summary>A row the user linked by hand, and the name of the location they chose.</summary>
public sealed record PatternExample(PatternRow Row, string Target);

/// <summary>A row a pattern links: its key, and the location it names.</summary>
public sealed record PatternMatch(string Key, long Id, string Name);

/// <summary>
/// A rule that explains how the user's examples were named, and the other rows it links: the location's name built from
/// the row's own names and fixed text ("the pin's name, " - ", the section's name").
/// </summary>
public sealed class Pattern
{
    /// <summary>The rule in words: <c>pin's name + " - " + section's name</c>.</summary>
    public string Description { get; init; } = "";

    /// <summary>A stable form of the rule (two patterns with the same one are the same).</summary>
    public string Template { get; init; } = "";

    /// <summary>The user's links it explains.</summary>
    public IReadOnlyList<PatternExample> Examples { get; init; } = Array.Empty<PatternExample>();

    /// <summary>The other rows it links, each to a location of the game that nothing else takes.</summary>
    public IReadOnlyList<PatternMatch> Matches { get; init; } = Array.Empty<PatternMatch>();
}

/// <summary>
/// Fix by example: from two rows the user linked by hand, finds how the game names a pack's sections (the location's name
/// made of the pin's name, the section's, a parent's or the area's, a number, and fixed text between them) and offers the
/// same rule for the other rows. General by design: it reads only the rows' own names and the game's, never a list of
/// packs. A rule is offered only when it reproduces both examples exactly; a row is linked only when the name it makes is
/// one of the game's, isn't shown by another check, isn't made for another row too, and no other rule that fits the
/// examples names it differently.
/// </summary>
public static class PackPatterns
{
    /// <summary>The hand-made links a rule needs before it's offered (the owner, 2026-10-13).</summary>
    public const int MinExamples = 2;

    /// <summary>The most recent hand-made links looked at for rules.</summary>
    public const int ExamplesLooked = 8;

    private enum Part { Section, Pin, Parent, Area, Number }

    private readonly record struct Token(string? Literal, Part Part)
    {
        public bool IsLiteral => Literal != null;
    }

    /// <summary>
    /// The rules the user's hand-made links (newest first) explain, best first: each from two of them, with the rows it
    /// links. <paramref name="taken"/> are locations shown already (a rule never takes one).
    /// </summary>
    public static List<Pattern> FromExamples(IReadOnlyList<PatternExample> examples, IEnumerable<PatternRow> rows, IReadOnlyDictionary<long, string> names, ISet<long>? taken = null, int most = 5)
    {
        var found = new Dictionary<string, Pattern>(StringComparer.Ordinal);
        if (examples == null || examples.Count < MinExamples || names == null || names.Count == 0) return new List<Pattern>();
        var recent = examples.Take(ExamplesLooked).ToList();
        var rowList = rows as IReadOnlyList<PatternRow> ?? rows.ToList();
        var lookup = Lookup(names);
        for (int i = 0; i < recent.Count; i++)
            for (int j = i + 1; j < recent.Count; j++)
            {
                var pattern = Infer(recent[i], recent[j], rowList, lookup, taken);
                if (pattern == null || pattern.Matches.Count == 0) continue;
                if (!found.TryGetValue(pattern.Template, out var known) || known.Matches.Count < pattern.Matches.Count) found[pattern.Template] = pattern;
            }
        return found.Values.OrderByDescending(p => p.Matches.Count).ThenBy(p => p.Description, StringComparer.Ordinal).Take(most).ToList();
    }

    /// <summary>The rule two examples share, with the rows it links, or null when they share none.</summary>
    public static Pattern? Infer(PatternExample a, PatternExample b, IEnumerable<PatternRow> rows, IReadOnlyDictionary<long, string> names, ISet<long>? taken = null) =>
        Infer(a, b, rows as IReadOnlyList<PatternRow> ?? rows.ToList(), Lookup(names), taken);

    private static Pattern? Infer(PatternExample a, PatternExample b, IReadOnlyList<PatternRow> rows, Dictionary<string, (long Id, string Name)?> lookup, ISet<long>? taken)
    {
        if (a.Row.Key == b.Row.Key || string.IsNullOrWhiteSpace(a.Target) || string.IsNullOrWhiteSpace(b.Target)) return null;
        var fromA = TemplatesFor(a);
        var shared = TemplatesFor(b).Where(t => fromA.ContainsKey(t.Key)).Select(t => t.Value).ToList();
        if (shared.Count == 0) return null;
        // The most specific rule first: more of the row's own names, less fixed text.
        shared = shared.OrderByDescending(t => t.Count(x => !x.IsLiteral)).ThenBy(t => t.Where(x => x.IsLiteral).Sum(x => x.Literal!.Length)).ToList();
        var best = shared[0];
        // The rule reaches only rows like the examples: a name it doesn't use that both examples share (their pin's
        // "Cooked", their group "Circus Park", their sections' "Large Health Energy #") the row must share too. Text the
        // examples have in common is often what told them apart from rows the rule would get wrong.
        var used = best.Where(x => !x.IsLiteral).Select(x => x.Part).ToHashSet();
        var alike = new List<(Part Part, string Shape)>();
        foreach (var part in new[] { Part.Section, Part.Pin, Part.Parent, Part.Area })
        {
            if (used.Contains(part)) continue;
            string shape = Shape(Value(a.Row, part));
            if (shape == Shape(Value(b.Row, part))) alike.Add((part, shape));
        }
        var exampleKeys = new HashSet<string>(new[] { a.Row.Key, b.Row.Key }, StringComparer.Ordinal);
        var picks = new List<PatternMatch>();
        foreach (var row in rows)
        {
            if (exampleKeys.Contains(row.Key)) continue;
            if (alike.Any(x => Shape(Value(row, x.Part)) != x.Shape)) continue;
            var hit = Find(Render(best, row), lookup);
            if (hit == null || taken?.Contains(hit.Value.Id) == true) continue;
            // Another rule that fits the examples as well but names this row differently: the row is ambiguous.
            bool disagreed = shared.Skip(1).Any(other => Find(Render(other, row), lookup) is { } elsewhere && elsewhere.Id != hit.Value.Id);
            if (disagreed) continue;
            picks.Add(new PatternMatch(row.Key, hit.Value.Id, hit.Value.Name));
        }
        // A location the rule makes for two rows is given to neither (which is right can't be told).
        var doubled = picks.GroupBy(p => p.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        picks.RemoveAll(p => doubled.Contains(p.Id));
        return new Pattern
        {
            Template = Key(best),
            Description = Describe(best),
            Examples = new[] { a, b },
            Matches = picks
        };
    }

    // ---- Templates ----

    /// <summary>Every way the example's chosen name can be made from its row's names and fixed text (one to three of the row's names, in order).</summary>
    private static Dictionary<string, List<Token>> TemplatesFor(PatternExample example)
    {
        var templates = new Dictionary<string, List<Token>>(StringComparer.Ordinal);
        string target = Clean(example.Target);
        var spots = new List<(Part Part, int Start, int Length)>();
        foreach (Part part in Enum.GetValues<Part>())
        {
            string value = Value(example.Row, part);
            if (value.Length == 0 || (part != Part.Number && value.Length < 2)) continue;
            for (int at = target.IndexOf(value, StringComparison.Ordinal); at >= 0; at = target.IndexOf(value, at + 1, StringComparison.Ordinal))
            {
                if (part == Part.Number && ((at > 0 && char.IsDigit(target[at - 1])) || (at + value.Length < target.Length && char.IsDigit(target[at + value.Length])))) continue;
                spots.Add((part, at, value.Length));
            }
        }
        spots = spots.OrderBy(s => s.Start).ThenByDescending(s => s.Length).ToList();
        void Walk(int from, int end, List<(Part Part, int Start, int Length)> chosen)
        {
            if (chosen.Count > 0)
            {
                var tokens = new List<Token>();
                int at = 0;
                foreach (var (part, start, length) in chosen)
                {
                    if (start > at) tokens.Add(new Token(target.Substring(at, start - at), default));
                    tokens.Add(new Token(null, part));
                    at = start + length;
                }
                if (at < target.Length) tokens.Add(new Token(target.Substring(at), default));
                // A number in the fixed text is a copy number or an area's: right for the examples, wrong for the next copy.
                if (!tokens.Any(x => x.IsLiteral && x.Literal!.Any(char.IsAsciiDigit))) templates[Key(tokens)] = tokens;
            }
            if (chosen.Count == 3) return;
            for (int i = from; i < spots.Count; i++)
            {
                var spot = spots[i];
                if (spot.Start < end || chosen.Any(c => c.Part == spot.Part)) continue;
                chosen.Add(spot);
                Walk(i + 1, spot.Start + spot.Length, chosen);
                chosen.RemoveAt(chosen.Count - 1);
            }
        }
        Walk(0, 0, new List<(Part, int, int)>());
        return templates;
    }

    private static string Render(List<Token> template, PatternRow row)
    {
        var text = new StringBuilder();
        foreach (var token in template)
        {
            if (token.IsLiteral) text.Append(token.Literal);
            else
            {
                string value = Value(row, token.Part);
                if (value.Length == 0) return "";
                text.Append(value);
            }
        }
        return text.ToString();
    }

    private static string Key(List<Token> template) =>
        string.Join("\u0001", template.Select(t => t.IsLiteral ? "L" + t.Literal : "P" + t.Part));

    private static string Describe(List<Token> template) =>
        string.Join(" + ", template.Select(t => t.IsLiteral ? "\"" + t.Literal + "\"" : t.Part switch
        {
            Part.Section => "the section's name",
            Part.Pin => "the pin's name",
            Part.Parent => "the pin's group",
            Part.Area => "the pin's area",
            _ => "its number",
        }));

    // ---- A row's names ----

    private static string Value(PatternRow row, Part part)
    {
        var path = (row.PinPath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return part switch
        {
            Part.Section => Clean(row.Section),
            Part.Pin => Clean(row.PinName),
            Part.Parent => path.Length >= 2 ? Clean(path[^2]) : "",
            Part.Area => path.Length >= 3 ? Clean(path[0]) : "",
            _ => FirstNumber(row.Section) is { Length: > 0 } n ? n : FirstNumber(row.PinName),
        };
    }

    /// <summary>A name with its numbers as "#": "Large Health Energy 4" and "… 7" have one shape.</summary>
    private static string Shape(string value)
    {
        var shape = new StringBuilder();
        bool inNumber = false;
        foreach (char c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                if (!inNumber) shape.Append('#');
                inNumber = true;
            }
            else
            {
                shape.Append(c);
                inNumber = false;
            }
        }
        return shape.ToString();
    }

    private static string FirstNumber(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i])) { if (start < 0) start = i; }
            else if (start >= 0) return text.Substring(start, i - start);
        }
        return start >= 0 ? text.Substring(start) : "";
    }

    /// <summary>Lower case, one space between words (the text a template is made of and rendered to).</summary>
    private static string Clean(string? text) => string.Join(' ', (text ?? "").ToLower(CultureInfo.InvariantCulture).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---- The game's names ----

    /// <summary>Case and punctuation aside, as a location's name is looked up.</summary>
    public static string Normalize(string? text)
    {
        var builder = new StringBuilder();
        foreach (char c in (text ?? "").ToLower(CultureInfo.InvariantCulture))
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The game's names by their normalized form; a form two locations share finds neither.</summary>
    private static Dictionary<string, (long Id, string Name)?> Lookup(IReadOnlyDictionary<long, string> names)
    {
        var lookup = new Dictionary<string, (long Id, string Name)?>(StringComparer.Ordinal);
        foreach (var (id, name) in names)
        {
            string key = Normalize(name);
            if (key.Length == 0) continue;
            lookup[key] = lookup.TryGetValue(key, out var seen) && seen?.Id != id ? null : (id, name);
        }
        return lookup;
    }

    private static (long Id, string Name)? Find(string rendered, Dictionary<string, (long Id, string Name)?> lookup) =>
        rendered.Length > 0 && lookup.TryGetValue(Normalize(rendered), out var hit) ? hit : null;
}
