using System;
using System.Collections.Generic;

namespace AP_Atlas.Core;

/// <summary>
/// How deeply Lua source nests, read from its tokens without compiling it. Brackets and blocks open levels, and each
/// operator or suffix in a run (<c>a + b + c</c>, <c>t.x.y</c>, <c>f()()</c>, <c>not not x</c>) adds to the
/// expression at its level. MoonSharp's compiler recurses about once per level, so source nested far deeper than real
/// code (Lua itself refuses nesting over 200) would overflow the stack while compiling, and .NET can't catch a stack
/// overflow: it ends the process. Strings and comments don't count. Linear in the source's length, and never throws.
/// </summary>
/// <remarks>
/// The count is an upper bound on the compiler's recursion: a level's operator run counts whether or not MoonSharp
/// recurses for each operator, and runs are added up along the levels still open. A new statement (a name right after
/// an expression ends) starts a new run, so a long list of statements doesn't add up.
/// </remarks>
public static class LuaNesting
{
    /// <summary>The deepest point of the source: the levels open there, plus the operator runs in them.</summary>
    public static int Depth(string source)
    {
        if (string.IsNullOrEmpty(source)) return 0;
        var saved = new Stack<int>(); // each open level's run, saved when the level opened
        int savedTotal = 0, run = 0, deepest = 0;
        bool afterOperand = false; // the last token ended an expression (a name, literal, or closing bracket)
        int i = 0, n = source.Length;

        void Note()
        {
            int now = savedTotal + saved.Count + run;
            if (now > deepest) deepest = now;
        }
        void Open()
        {
            saved.Push(run);
            savedTotal += run;
            run = 0;
            afterOperand = false;
            Note();
        }
        void Close()
        {
            if (saved.Count > 0)
            {
                run = saved.Pop();
                savedTotal -= run;
            }
            else run = 0; // unbalanced: the compiler reports it
            afterOperand = true;
        }
        void Operator()
        {
            run++;
            afterOperand = false;
            Note();
        }
        void Separator()
        {
            run = 0;
            afterOperand = false;
        }
        // An operand right after another one is a call with a string or table argument (f "x", f {...}) when it's a
        // literal, or a new statement when it's a name.
        void Operand(bool literal)
        {
            if (afterOperand)
            {
                if (literal) Operator();
                else Separator();
            }
            afterOperand = true;
        }

        while (i < n)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            // Comments: --[[ long ]] or to the end of the line.
            if (c == '-' && i + 1 < n && source[i + 1] == '-')
            {
                i += 2;
                int level = LongBracket(source, i);
                if (level >= 0) i = SkipLong(source, i, level);
                else
                {
                    while (i < n && source[i] != '\n') i++;
                }
                continue;
            }
            // Long strings: [[ ... ]], [==[ ... ]==].
            if (c == '[')
            {
                int level = LongBracket(source, i);
                if (level >= 0)
                {
                    i = SkipLong(source, i, level);
                    Operand(literal: true);
                    continue;
                }
            }
            if (c == '"' || c == '\'')
            {
                i++;
                while (i < n && source[i] != c && source[i] != '\n')
                {
                    if (source[i] == '\\') i++; // an escape: the next character is part of it
                    i++;
                }
                i++;
                Operand(literal: true);
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                Word(source.AsSpan(start, i - start));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(source[i + 1])))
            {
                bool hex = c == '0' && i + 1 < n && (source[i + 1] == 'x' || source[i + 1] == 'X');
                i++;
                while (i < n)
                {
                    char d = source[i];
                    if (char.IsLetterOrDigit(d) || d == '_' || d == '.') i++;
                    // An exponent's sign belongs to the number (1e-5, 0x1p+4).
                    else if ((d == '+' || d == '-') && (hex ? source[i - 1] is 'p' or 'P' : source[i - 1] is 'e' or 'E')) i++;
                    else break;
                }
                Operand(literal: true);
                continue;
            }
            switch (c)
            {
                case '(':
                case '{':
                case '[':
                    // Right after an operand it's a call or an index: one more link in the run, then a level of its own.
                    if (afterOperand) run++;
                    i++;
                    Open();
                    continue;
                case ')':
                case '}':
                case ']':
                    i++;
                    Close();
                    continue;
                case ',':
                case ';':
                    i++;
                    Separator();
                    continue;
                case '=':
                    if (i + 1 < n && source[i + 1] == '=') { i += 2; Operator(); }
                    else { i++; Separator(); }
                    continue;
                case ':':
                    if (i + 1 < n && source[i + 1] == ':') { i += 2; Separator(); } // a label
                    else { i++; Operator(); } // a method call
                    continue;
                case '.':
                    if (i + 2 < n && source[i + 1] == '.' && source[i + 2] == '.')
                    {
                        i += 3; // varargs
                        Operand(literal: true);
                        continue;
                    }
                    i += i + 1 < n && source[i + 1] == '.' ? 2 : 1; // concatenation (..) or a field (.)
                    Operator();
                    continue;
                default:
                    // Any other symbol is an operator (+ - * / % ^ # & | ~ < > and their pairs); a pair counts once.
                    i++;
                    if (i < n && IsOperatorPair(c, source[i])) i++;
                    Operator();
                    continue;
            }
        }
        return deepest;

        void Word(ReadOnlySpan<char> word)
        {
            switch (word)
            {
                case "function":
                case "if":
                case "do":
                case "repeat":
                    Open();
                    return;
                case "end":
                case "until":
                    Close();
                    if (word is "until") afterOperand = false; // its condition follows
                    return;
                case "and":
                case "or":
                case "not":
                    Operator();
                    return;
                case "nil":
                case "true":
                case "false":
                    Operand(literal: false);
                    return;
                case "then":
                case "else":
                case "elseif":
                case "return":
                case "local":
                case "in":
                case "break":
                case "goto":
                case "while":
                case "for":
                    Separator();
                    return;
                default:
                    Operand(literal: false);
                    return;
            }
        }
    }

    private static bool IsOperatorPair(char first, char second) =>
        (first, second) is ('<', '=') or ('>', '=') or ('~', '=') or ('<', '<') or ('>', '>') or ('/', '/');

    /// <summary>The level of a long bracket opening at <paramref name="at"/> ([[ is 0, [==[ is 2), or -1 if there isn't one.</summary>
    private static int LongBracket(string source, int at)
    {
        if (at >= source.Length || source[at] != '[') return -1;
        int i = at + 1, level = 0;
        while (i < source.Length && source[i] == '=') { level++; i++; }
        return i < source.Length && source[i] == '[' ? level : -1;
    }

    /// <summary>Skips a long string or comment opening at <paramref name="at"/>; returns where it ends (the end of the source if it never closes).</summary>
    private static int SkipLong(string source, int at, int level)
    {
        string close = "]" + new string('=', level) + "]";
        int end = source.IndexOf(close, at + level + 2, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + close.Length;
    }
}
