using System;

namespace AP_Atlas.Core;

/// <summary>
/// How much memory a map pack's images may take once decoded, image by image: one larger than Atlas shows, or one that
/// would take the pack past its total, is refused before it's decoded (it shows blank, and the pack says why). Sizes come
/// from <see cref="ImageHeader"/>. Not thread-safe: one per decoding of a pack.
/// </summary>
public sealed class ImageBudget
{
    /// <summary>
    /// The longest side Atlas decodes: the largest texture most graphics cards take. The longest in 43 real map packs
    /// (6,750 images) is 12,560.
    /// </summary>
    public const int MaxSide = 16384;

    /// <summary>A pack's decoded images together (4 bytes a pixel). The most in 43 real map packs is 821 MB.</summary>
    public const long DefaultTotal = 2L << 30;

    private readonly long _total;
    private long _used;

    public ImageBudget(long total = DefaultTotal) => _total = total;

    /// <summary>
    /// Takes room for an image file's pixels, sized from its header (see <see cref="ImageHeader"/>), or says why it can't
    /// be decoded (null: it fits). A file whose size can't be read isn't decoded either: its decoder could find one.
    /// </summary>
    public string? Take(ReadOnlySpan<byte> file) =>
        ImageHeader.Size(file) is { } size ? Take(size.Width, size.Height) : "it isn't a PNG, JPEG or WebP image whose size Atlas can read";

    /// <summary>Takes room for an image this size, or says why it can't be decoded (null: it fits).</summary>
    public string? Take(int width, int height)
    {
        if (width <= 0 || height <= 0) return "it has no size";
        if (width > MaxSide || height > MaxSide) return $"it's {width:N0} × {height:N0} pixels, larger than Atlas shows ({MaxSide:N0} a side)";
        long bytes = (long)width * height * 4;
        if (_used + bytes > _total) return $"the pack's images together come to more than Atlas decodes ({SafeZip.Size(_total)})";
        _used += bytes;
        return null;
    }
}
