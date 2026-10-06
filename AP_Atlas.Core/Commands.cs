using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core;

/// <summary>
/// One thing the user can tell Atlas to do, wherever it's asked from: the menu bar, a key, later the command palette.
/// Its title is English; the window translates it as it's shown (tr()).
/// </summary>
public sealed class Command
{
    public Command(string id, string menu, string title, string defaultShortcut, Action run)
    {
        Id = id;
        Menu = menu;
        Title = title;
        DefaultShortcut = defaultShortcut.Length == 0 ? "" :
            Commands.Normalize(defaultShortcut) ?? throw new ArgumentException($"\"{defaultShortcut}\" isn't a shortcut Atlas understands.", nameof(defaultShortcut));
        Run = run;
    }

    /// <summary>Stable, for key rebinding and the palette (e.g. "tool.map-tracker"). Never changes once released.</summary>
    public string Id { get; }

    /// <summary>The menu it appears under ("File", "Tools", …).</summary>
    public string Menu { get; }

    public string Title { get; }

    /// <summary>The key that runs it until the user rebinds it, canonical ("Ctrl+Shift+P"); "" for none.</summary>
    public string DefaultShortcut { get; }

    public Action Run { get; }

    /// <summary>Whether it can run right now (a disabled command is shown grey and its key does nothing).</summary>
    public Func<bool> Enabled { get; init; } = () => true;
}

/// <summary>
/// Every command, with the keys that run them: the defaults, with the user's rebinds on top (in the settings:
/// command id → shortcut, "" to unbind).
/// </summary>
public sealed class Commands
{
    private readonly List<Command> _all = new();
    private readonly Dictionary<string, Command> _byId = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyDictionary<string, string>?> _rebinds;

    /// <param name="rebinds">The user's rebinds, read fresh each time (they can change while Atlas runs).</param>
    public Commands(Func<IReadOnlyDictionary<string, string>?> rebinds) => _rebinds = rebinds;

    public IReadOnlyList<Command> All => _all;

    /// <summary>The command with this id, or null.</summary>
    public Command? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Adds a command. Two commands can't share an id: that's a programming error, caught by the tests.</summary>
    public Command Add(Command command)
    {
        if (!_byId.TryAdd(command.Id, command))
            throw new InvalidOperationException($"Two commands are both \"{command.Id}\".");
        _all.Add(command);
        return command;
    }

    /// <summary>The key that runs a command now: the user's rebind, else its default. "" for none.</summary>
    public string ShortcutOf(string id)
    {
        if (!_byId.TryGetValue(id, out var command)) return "";
        if (_rebinds() is { } rebinds && rebinds.TryGetValue(id, out var rebound))
            return rebound.Length == 0 ? "" : Normalize(rebound) ?? command.DefaultShortcut;
        return command.DefaultShortcut;
    }

    /// <summary>
    /// Runs whatever the key is bound to, skipping commands that are disabled right now. True when one ran
    /// (the key is taken); false when the key means nothing, so whatever else reads keys can have it.
    /// </summary>
    public bool RunShortcut(string? canonical)
    {
        if (string.IsNullOrEmpty(canonical)) return false;
        foreach (var command in _all)
        {
            if (ShortcutOf(command.Id) != canonical || !command.Enabled()) continue;
            RunNow(command);
            return true;
        }
        return false;
    }

    /// <summary>Runs a command by id (a menu item). A failure is reported, never thrown into input handling.</summary>
    public bool Run(string id)
    {
        if (!_byId.TryGetValue(id, out var command) || !command.Enabled()) return false;
        RunNow(command);
        return true;
    }

    private static void RunNow(Command command)
    {
        try
        {
            command.Run();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Async.Report(ex, $"running \"{command.Title}\"");
        }
    }

    /// <summary>Keys bound to more than one enabled command (the first wins; the shortcuts page will show these).</summary>
    public IReadOnlyList<(string Shortcut, IReadOnlyList<Command> Bound)> Conflicts() =>
        _all.GroupBy(c => ShortcutOf(c.Id))
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<Command>)g.ToList()))
            .ToList();

    private static readonly Dictionary<string, string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = "Ctrl",
        ["control"] = "Ctrl",
        ["shift"] = "Shift",
        ["alt"] = "Alt",
        ["win"] = "Win",
        ["meta"] = "Win",
        ["cmd"] = "Win"
    };

    private static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tab"] = "Tab",
        ["escape"] = "Escape",
        ["esc"] = "Escape",
        ["enter"] = "Enter",
        ["return"] = "Enter",
        ["space"] = "Space",
        ["backspace"] = "Backspace",
        ["delete"] = "Delete",
        ["del"] = "Delete",
        ["insert"] = "Insert",
        ["home"] = "Home",
        ["end"] = "End",
        ["pageup"] = "PageUp",
        ["pagedown"] = "PageDown",
        ["up"] = "Up",
        ["down"] = "Down",
        ["left"] = "Left",
        ["right"] = "Right",
        ["minus"] = "-",
        ["plus"] = "+",
        ["comma"] = ",",
        ["period"] = ".",
        ["slash"] = "/",
        ["backslash"] = "\\",
        ["bracketleft"] = "[",
        ["bracketright"] = "]",
        ["semicolon"] = ";",
        ["apostrophe"] = "'",
        ["quoteleft"] = "`"
    };

    /// <summary>
    /// A shortcut as Atlas writes it everywhere: modifiers in the order Ctrl, Shift, Alt, Win, then exactly one key
    /// ("ctrl + shift+p" → "Ctrl+Shift+P"). Null when it isn't one (an unknown key, two keys, only modifiers), so a
    /// mistyped rebind in the settings file falls back to the default instead of binding something unintended.
    /// </summary>
    public static string? Normalize(string shortcut)
    {
        bool ctrl = false, shift = false, alt = false, win = false;
        string? key = null;
        foreach (var raw in shortcut.Split('+'))
        {
            string token = raw.Trim();
            if (token.Length == 0) continue;
            if (Modifiers.TryGetValue(token, out string? modifier))
            {
                if (modifier == "Ctrl") ctrl = true;
                else if (modifier == "Shift") shift = true;
                else if (modifier == "Alt") alt = true;
                else win = true;
                continue;
            }
            if (key != null) return null; // two keys
            if (token.Length == 1) key = char.IsAsciiLetter(token[0]) ? char.ToUpperInvariant(token[0]).ToString() : token; // a letter, a digit, a punctuation key
            else if (token.Length is 2 or 3 && (token[0] is 'f' or 'F') && int.TryParse(token.AsSpan(1), out int f) && f is >= 1 and <= 24) key = "F" + f;
            else if (Keys.TryGetValue(token, out string? named)) key = named;
            else return null;
        }
        if (key == null) return null;
        return (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + (alt ? "Alt+" : "") + (win ? "Win+" : "") + key;
    }
}
