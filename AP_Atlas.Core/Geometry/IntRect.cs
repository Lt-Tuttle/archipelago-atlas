using System;

namespace AP_Atlas.Core.Geometry;

/// <summary>A rectangle in whole pixels (a window on a screen), and how one is fitted inside another.</summary>
public readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    /// <summary>Whether the point halfway across and down this rectangle lies in <paramref name="other"/>.</summary>
    public bool CentreIn(IntRect other) => other.Contains(X + Width / 2, Y + Height / 2);

    /// <summary>
    /// <paramref name="wanted"/> made to fit inside <paramref name="usable"/>: no wider or taller than it, then moved the
    /// least that puts the whole rectangle inside (so a title bar is never above the screen's top). An empty usable
    /// rectangle (a headless run knows no screen) leaves the wanted one as it is.
    /// </summary>
    public static IntRect Fit(IntRect wanted, IntRect usable)
    {
        if (usable.IsEmpty) return wanted;
        int width = Math.Clamp(wanted.Width, 1, usable.Width);
        int height = Math.Clamp(wanted.Height, 1, usable.Height);
        int x = Math.Clamp(wanted.X, usable.X, usable.Right - width);
        int y = Math.Clamp(wanted.Y, usable.Y, usable.Bottom - height);
        return new IntRect(x, y, width, height);
    }

    /// <summary>A rectangle of <paramref name="width"/> by <paramref name="height"/> (shrunk to fit) in the middle of <paramref name="usable"/>.</summary>
    public static IntRect Centred(int width, int height, IntRect usable)
    {
        if (usable.IsEmpty) return new IntRect(0, 0, width, height);
        width = Math.Clamp(width, 1, usable.Width);
        height = Math.Clamp(height, 1, usable.Height);
        return new IntRect(usable.X + (usable.Width - width) / 2, usable.Y + (usable.Height - height) / 2, width, height);
    }
}
