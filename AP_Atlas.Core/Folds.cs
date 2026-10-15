using System.Collections.Generic;

namespace AP_Atlas.Core;

/// <summary>
/// Remembering which folding sections (Properties' sections, the parts of the Games and Engine pages) the user opened
/// or closed. A section starts as its default (a long list or technical detail starts closed); the user's own choice
/// is kept only where it differs from that default, so a section whose default changes later follows the new one
/// unless the user chose otherwise. Keys are shared by every view that uses the same one ("Advanced").
/// </summary>
public static class Folds
{
    /// <summary>Whether the section shows closed: the user's choice when there is one, else its default.</summary>
    public static bool IsCollapsed(string key, bool collapsedByDefault, ICollection<string> collapsed, ICollection<string> expanded)
    {
        if (collapsed.Contains(key)) return true;
        if (expanded.Contains(key)) return false;
        return collapsedByDefault;
    }

    /// <summary>Records the user closing or opening the section, against its default.</summary>
    public static void Remember(string key, bool nowCollapsed, bool collapsedByDefault, ICollection<string> collapsed, ICollection<string> expanded)
    {
        if (nowCollapsed)
        {
            expanded.Remove(key);
            if (!collapsedByDefault && !collapsed.Contains(key)) collapsed.Add(key);
        }
        else
        {
            collapsed.Remove(key);
            if (collapsedByDefault && !expanded.Contains(key)) expanded.Add(key);
        }
    }
}
