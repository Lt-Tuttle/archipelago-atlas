using System;
using System.Collections.Generic;
using System.Globalization;

namespace AP_Atlas.Core.Updates;

/// <summary>
/// A version as Semantic Versioning 2.0 reads it: major.minor.patch, an optional pre-release ("-beta.2"), and build
/// metadata ("+abc123") that never counts. Precedence follows the specification: numbers by value, a pre-release below
/// its release, pre-release identifiers one by one (numeric before alphanumeric, numeric by value, the rest by ASCII order,
/// a shorter list of equal identifiers first).
/// </summary>
public readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>The pre-release identifiers ("beta", "2"), none for a release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    public SemVer(int major, int minor, int patch, IReadOnlyList<string>? preRelease = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease ?? Array.Empty<string>();
    }

    public bool IsPreRelease => PreRelease.Count > 0;

    /// <summary>Reads "1.2.3", "v1.2.3", "1.2.3-beta.1" or "1.2.3-beta.1+commit"; anything else is refused.</summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s.Substring(1);
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);
        string[]? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            string tail = s.Substring(dash + 1);
            s = s.Substring(0, dash);
            if (tail.Length == 0) return false;
            pre = tail.Split('.');
            foreach (string id in pre)
            {
                if (id.Length == 0) return false;
                foreach (char c in id)
                    if (!(char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
                // A numeric identifier never has leading zeroes.
                if (IsNumeric(id) && id.Length > 1 && id[0] == '0') return false;
            }
        }
        string[] parts = s.Split('.');
        if (parts.Length != 3) return false;
        int[] numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (!IsNumeric(parts[i]) || (parts[i].Length > 1 && parts[i][0] == '0')) return false;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        version = new SemVer(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public static SemVer Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' isn't a version like 1.2.3 or 0.2.0-beta.1.");

    private static bool IsNumeric(string id)
    {
        if (id.Length == 0) return false;
        foreach (char c in id)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    public int CompareTo(SemVer other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);
        if (!IsPreRelease || !other.IsPreRelease) return IsPreRelease ? -1 : other.IsPreRelease ? 1 : 0;
        int count = Math.Min(PreRelease.Count, other.PreRelease.Count);
        for (int i = 0; i < count; i++)
        {
            string a = PreRelease[i], b = other.PreRelease[i];
            bool aNumeric = IsNumeric(a), bNumeric = IsNumeric(b);
            int result;
            if (aNumeric && bNumeric) result = long.Parse(a, CultureInfo.InvariantCulture).CompareTo(long.Parse(b, CultureInfo.InvariantCulture));
            else if (aNumeric != bNumeric) result = aNumeric ? -1 : 1;
            else result = string.CompareOrdinal(a, b);
            if (result != 0) return result;
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    public bool Equals(SemVer other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVer other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', PreRelease));
    public static bool operator ==(SemVer a, SemVer b) => a.Equals(b);
    public static bool operator !=(SemVer a, SemVer b) => !a.Equals(b);
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        IsPreRelease ? $"{Major}.{Minor}.{Patch}-{string.Join('.', PreRelease)}" : $"{Major}.{Minor}.{Patch}";
}
