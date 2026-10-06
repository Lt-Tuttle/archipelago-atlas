using System;
using System.Buffers.Binary;

namespace AP_Atlas.Core;

/// <summary>
/// An image's size, read from its header without decoding it (PNG, JPEG, WebP), so an image too large to decode safely
/// can be refused first: a decoder sets aside width × height × 4 bytes from the header alone, and a file of a few bytes
/// can claim to be gigapixels. Never throws.
/// </summary>
public static class ImageHeader
{
    /// <summary>Width and height in pixels, or null if the data isn't a PNG, JPEG or WebP image whose size can be read.</summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> data)
    {
        if (IsPng(data)) return Png(data);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8) return Jpeg(data);
        if (data.Length >= 16 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return WebP(data);
        return null;
    }

    private static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

    // The first chunk is IHDR: width and height, big-endian, right after its length and type.
    private static (int, int)? Png(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || !data[12..16].SequenceEqual("IHDR"u8)) return null;
        return Positive(BinaryPrimitives.ReadUInt32BigEndian(data[16..20]), BinaryPrimitives.ReadUInt32BigEndian(data[20..24]));
    }

    // The size is in the first start-of-frame segment. Markers are found the way JPEG decoders find them (jpgd's and
    // libjpeg's next_marker): bytes up to a 0xFF are skipped, then repeated 0xFFs, and 0xFF 0x00 isn't a marker. Reading
    // segments strictly by their lengths instead would let a file show this a small frame while the decoder finds a huge
    // one hidden in the bytes between.
    private static (int, int)? Jpeg(ReadOnlySpan<byte> data)
    {
        int i = 2;
        while (true)
        {
            byte marker;
            do
            {
                while (i < data.Length && data[i] != 0xFF) i++;
                while (i < data.Length && data[i] == 0xFF) i++;
                if (i >= data.Length) return null;
                marker = data[i++];
            } while (marker == 0);
            switch (marker)
            {
                case >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC:
                    // Start of frame: length (2), precision (1), height (2), width (2).
                    if (i + 7 > data.Length) return null;
                    return Positive(BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 5, 2)), BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 3, 2)));
                case 0xC8 or 0xD8 or 0xD9 or 0xDA:
                    return null; // decoders stop here (a second start of image, the end, or the image data before any frame)
                case >= 0xD0 and <= 0xD7 or 0x01:
                    break; // markers without a segment
                default:
                    if (i + 2 > data.Length) return null;
                    int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i, 2));
                    if (length < 2) return null;
                    i += length;
                    break;
            }
        }
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> data)
    {
        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
        {
            // The canvas: width - 1 and height - 1, 24 bits each, little-endian.
            if (data.Length < 30) return null;
            uint width = 1 + (uint)(data[24] | data[25] << 8 | data[26] << 16);
            uint height = 1 + (uint)(data[27] | data[28] << 8 | data[29] << 16);
            return Positive(width, height);
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            // After the signature byte: width - 1 and height - 1, 14 bits each.
            if (data.Length < 25 || data[20] != 0x2F) return null;
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..25]);
            return Positive(1 + (bits & 0x3FFF), 1 + ((bits >> 14) & 0x3FFF));
        }
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // After the frame tag and its start code: width and height, 14 bits each.
            if (data.Length < 30 || data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A) return null;
            return Positive((uint)(BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]) & 0x3FFF), (uint)(BinaryPrimitives.ReadUInt16LittleEndian(data[28..30]) & 0x3FFF));
        }
        return null;
    }

    private static (int, int)? Positive(uint width, uint height) =>
        width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue ? ((int)width, (int)height) : null;
}
