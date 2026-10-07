using System;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AP_Atlas.Core;

/// <summary>
/// A zip from outside Atlas (a map pack, an apworld), opened and read without trusting what it claims. A "zip bomb" (a
/// small file that unpacks to gigabytes, or that lists millions of files) would otherwise fill Atlas's memory, so:
/// <list type="bullet">
/// <item>A zip64 zip is refused before .NET lists its files. Only zip64 can list more than 65,534 files (.NET lists no
/// more than a zip says it holds, at about 600 bytes each), and no map pack or apworld needs it: it's for zips with more
/// files than that, or over 4 GB.</item>
/// <item>Each file's bytes are counted as they're unpacked, and one that comes to more than its limit is refused, however
/// small its header says it is.</item>
/// <item>So is one that takes what has been read from the zip past its total (text and images are counted apart).</item>
/// </list>
/// Refusals are <see cref="InvalidDataException"/>s, like .NET's own for a damaged zip, with a message for the user.
/// Not thread-safe: one per use of a zip.
/// </summary>
public sealed class SafeZip : IDisposable
{
    /// <summary>The most one text file (JSON, Lua, a manifest) may unpack to. The largest in 43 real map packs is 2.2 MB.</summary>
    public const long TextLimit = 64L << 20;

    /// <summary>The most the text read from one zip may come to. The most in 43 real map packs is 3.7 MB.</summary>
    public const long TextTotal = 128L << 20;

    /// <summary>The most one image file may unpack to. The largest in 43 real map packs is 64 MB.</summary>
    public const long ImageLimit = 256L << 20;

    /// <summary>The most the image files read from one zip may come to. The most in 43 real map packs is 210 MB.</summary>
    public const long ImageTotal = 1L << 30;

    /// <summary>The limits a zip is read under: <see cref="Default"/>, or smaller ones in tests.</summary>
    internal readonly record struct Limits(long Text, long TextTotal, long Image, long ImageTotal)
    {
        public static Limits Default => new(SafeZip.TextLimit, SafeZip.TextTotal, ImageLimit, SafeZip.ImageTotal);
    }

    private readonly ZipArchive _zip;
    private readonly string _name;
    private readonly Limits _limits;
    private long _text;
    private long _images;

    private SafeZip(ZipArchive zip, string name, Limits limits)
    {
        _zip = zip;
        _name = name;
        _limits = limits;
    }

    /// <summary>Opens a zip file. Refused (<see cref="InvalidDataException"/>) if it isn't a zip, or is a zip64 zip.</summary>
    public static SafeZip Open(string path) =>
        Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), Path.GetFileName(path), Limits.Default);

    /// <summary>Opens a zip held in a seekable stream, which it then owns; <paramref name="name"/> names it in refusals.</summary>
    internal static SafeZip Open(Stream stream, string name, Limits limits)
    {
        try
        {
            if (Refusal(stream) is string why) throw new InvalidDataException($"'{name}' {why}");
            stream.Position = 0;
            return new SafeZip(new ZipArchive(stream, ZipArchiveMode.Read), name, limits);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The zip's files (listing them unpacks nothing).</summary>
    public ReadOnlyCollection<ZipArchiveEntry> Entries => _zip.Entries;

    /// <summary>The file at exactly this path in the zip, or null.</summary>
    public ZipArchiveEntry? GetEntry(string path) => _zip.GetEntry(path);

    /// <summary>A text file (UTF-8 unless a byte order mark says otherwise), within <see cref="TextLimit"/> and <see cref="TextTotal"/>.</summary>
    public string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(new MemoryStream(ReadTextBytes(entry)), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>A text file's bytes, for a caller that decodes them itself; limited as <see cref="ReadText"/> is.</summary>
    public byte[] ReadTextBytes(ZipArchiveEntry entry) => Read(entry, _limits.Text, _limits.TextTotal, ref _text, "text");

    /// <summary>An image file's bytes, within <see cref="ImageLimit"/> and <see cref="ImageTotal"/>.</summary>
    public byte[] ReadImage(ZipArchiveEntry entry) => Read(entry, _limits.Image, _limits.ImageTotal, ref _images, "images");

    public void Dispose() => _zip.Dispose();

    /// <summary>The most one file of a release of Atlas may unpack to (its program is about 105 MB).</summary>
    public const long ProgramLimit = 1L << 30;

    /// <summary>The most a release of Atlas may unpack to in all (about 200 MB today).</summary>
    public const long ProgramTotal = 2L << 30;

    /// <summary>
    /// Unpacks a zip into a folder with the same care as reading one: refused as a zip64 zip, each file's bytes counted as
    /// they're written (no file past <paramref name="fileLimit"/>, nothing past <paramref name="totalLimit"/> in all), and
    /// no entry allowed to land outside the folder. For Atlas's own releases, after their hash was checked; a refusal
    /// leaves what was unpacked for the caller to remove. <paramref name="include"/> (given an entry's name in the zip)
    /// leaves entries out, such as a package's test folders whose paths would pass Windows' limit.
    /// </summary>
    public static void UnpackTo(string zipPath, string folder, long fileLimit, long totalLimit, Func<string, bool>? include = null)
    {
        string root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        using var zip = Open(zipPath);
        long total = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            if (include != null && !include(entry.FullName)) continue;
            string full = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"'{entry.FullName}' would land outside the folder");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(full);
                continue;
            }
            if (entry.Length > fileLimit) throw TooBig(entry, fileLimit);
            if (total + entry.Length > totalLimit) throw TooMuchInAll(zipPath, totalLimit);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var source = OpenEntry(entry);
            using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long count = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                count += read;
                if (count > fileLimit) throw TooBig(entry, fileLimit);
                if (total + count > totalLimit) throw TooMuchInAll(zipPath, totalLimit);
                output.Write(buffer, 0, read);
            }
            total += count;
        }
    }

    /// <summary>The one place a file is unpacked from: every reader above counts what comes out of it.</summary>
    private static Stream OpenEntry(ZipArchiveEntry entry) => entry.Open();

    private static InvalidDataException TooMuchInAll(string zipPath, long total) =>
        new($"'{Path.GetFileName(zipPath)}' unpacks to more than {Size(total)}, more than Atlas unpacks from one zip");

    private byte[] Read(ZipArchiveEntry entry, long limit, long total, ref long used, string kind)
    {
        if (entry.Archive != _zip) throw new ArgumentException("The file is from another zip.", nameof(entry));
        // What the file claims is checked first (most bombs say so), then what it really unpacks to as it's read: .NET
        // stops a compressed file at the size its headers give, but reads a stored one to the end of its bytes.
        if (entry.Length > limit) throw TooBig(entry, limit);
        if (used + entry.Length > total) throw TooMuch(kind, total);
        using var source = OpenEntry(entry);
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[81920];
        long count = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            count += read;
            if (count > limit) throw TooBig(entry, limit);
            if (used + count > total) throw TooMuch(kind, total);
            output.Write(buffer, 0, read);
        }
        used += count;
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
    }

    private static InvalidDataException TooBig(ZipArchiveEntry entry, long limit) =>
        new($"'{entry.FullName}' unpacks to more than {Size(limit)}, more than Atlas reads from one file");

    private InvalidDataException TooMuch(string kind, long total) =>
        new($"'{_name}' holds more than {Size(total)} of {kind}, more than Atlas reads from one zip");

    /// <summary>A size as the refusals give it: "64 MB", "1 GB".</summary>
    internal static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes >> 30:N0} GB" : bytes >= 1L << 20 ? $"{bytes >> 20:N0} MB" : $"{bytes:N0} bytes";

    /// <summary>
    /// Why the zip can't be opened safely, or null. Its end record is found as .NET finds it (ZipArchive's
    /// ReadEndOfCentralDirectory): the last one in the zip's final 64 KB. A zip64 zip is marked there (a file count of
    /// 0xFFFF, or a disk or an offset of all ones), and .NET then takes the count from the zip64 record, which could say
    /// anything. Without a usable end record .NET refuses the zip too; refusing it here means .NET never searches further
    /// back than this did.
    /// </summary>
    private static string? Refusal(Stream stream)
    {
        const int EndRecord = 22, LongestComment = 65535;
        long length = stream.Length;
        int tail = (int)Math.Min(length, EndRecord + LongestComment);
        var bytes = new byte[tail];
        stream.Position = length - tail;
        stream.ReadExactly(bytes);
        ReadOnlySpan<byte> signature = [0x50, 0x4B, 0x05, 0x06];
        int end = bytes.AsSpan().LastIndexOf(signature);
        if (end < 0 || end > tail - EndRecord) return "isn't a zip file";
        var record = bytes.AsSpan(end);
        bool zip64 = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) == 0xFFFF // this disk
            || BinaryPrimitives.ReadUInt16LittleEndian(record[10..]) == 0xFFFF // how many files
            || BinaryPrimitives.ReadUInt32LittleEndian(record[16..]) == 0xFFFFFFFF; // where their list starts
        return zip64 ? "is a zip64 zip (made for more than 65,534 files, or over 4 GB), which Atlas doesn't read" : null;
    }
}
