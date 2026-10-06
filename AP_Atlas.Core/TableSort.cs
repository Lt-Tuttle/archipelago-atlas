using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AP_Atlas.Core;

/// <summary>
/// How every table in Atlas sorts: a column's keys compared as numbers when they are numbers (or numeric text), otherwise
/// as text in natural order (runs of digits by value, so "Slot 2" comes before "Slot 10", ignoring case); nothing (null)
/// last whichever way the column sorts; pinned rows on top; ties kept in the order the rows came (stable).
/// </summary>
public static class TableSort
{
    /// <summary>Orders rows by <paramref name="key"/>; <paramref name="pinned"/> rows come first in any direction; ties keep their order.</summary>
    public static List<T> Order<T>(IEnumerable<T> rows, Func<T, IComparable?> key, bool descending, Func<T, bool>? pinned = null)
    {
        var decorated = rows.Select((row, index) => (Row: row, Index: index, Key: key(row), Pinned: pinned?.Invoke(row) ?? false)).ToList();
        decorated.Sort((a, b) =>
        {
            if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
            int c = Compare(a.Key, b.Key, descending);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        return decorated.Select(d => d.Row).ToList();
    }

    /// <summary>Compares two keys for a column sorting in a direction: nothing (null) comes last either way.</summary>
    public static int Compare(IComparable? a, IComparable? b, bool descending)
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        int c = Compare(a, b);
        return descending ? -c : c;
    }

    /// <summary>Compares two keys: numbers as numbers (numeric text too), text naturally, anything else by its own order.</summary>
    public static int Compare(IComparable a, IComparable b)
    {
        if (a is string sa && b is string sb) return CompareText(sa, sb);
        if (IsNumber(a) && IsNumber(b)) return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
        if (a.GetType() == b.GetType()) return a.CompareTo(b);
        return CompareText(a.ToString() ?? "", b.ToString() ?? "");
    }

    /// <summary>Text as a table sorts it: as numbers when both are numbers ("9.5" before "10"), else in natural order.</summary>
    public static int CompareText(string? a, string? b)
    {
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double da) &&
            double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
            return da.CompareTo(db);
        return NaturalCompare(a, b);
    }

    /// <summary>Compares names with runs of digits as numbers ("Slot 2" before "Slot 10"), ignoring case.</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                // Numbers compare by value: leading zeros skipped, then the longer is larger, then digit by digit
                // (without making strings: big tables compare a lot).
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                while (si < i && a[si] == '0') si++;
                while (sj < j && b[sj] == '0') sj++;
                if (i - si != j - sj) return (i - si).CompareTo(j - sj);
                for (; si < i; si++, sj++)
                    if (a[si] != b[sj]) return a[si].CompareTo(b[sj]);
                continue;
            }
            int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (c != 0) return c;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
