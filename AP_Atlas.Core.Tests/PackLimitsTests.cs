using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AP_Atlas.Core.Testing;

namespace AP_Atlas.Core.Tests;

public class PackLimitsTests
{
    // Small limits, so the tests needn't make files of hundreds of megabytes: 1 MB a file, 2.5 MB of each kind in all.
    private static readonly SafeZip.Limits Small = new(Text: 1 << 20, TextTotal: 5 << 19, Image: 1 << 20, ImageTotal: 5 << 19);

    private static byte[] ZipBytes(CompressionLevel level, params (string Name, byte[] Content)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var entry = zip.CreateEntry(name, level).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static SafeZip Open(byte[] zip, SafeZip.Limits? limits = null) =>
        SafeZip.Open(new MemoryStream(zip), "test.zip", limits ?? SafeZip.Limits.Default);

    [Fact]
    public void A_file_within_its_limits_is_read_whole()
    {
        byte[] json = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"a\": \"é\"}")).ToArray();
        using var zip = Open(ZipBytes(CompressionLevel.Optimal, ("items.json", json), ("images/a.png", FakeMapPack.Png(4, 4))));
        Assert.Equal("{\"a\": \"é\"}", zip.ReadText(zip.GetEntry("items.json")!));
        Assert.Equal(FakeMapPack.Png(4, 4), zip.ReadImage(zip.GetEntry("images/a.png")!));
    }

    [Fact]
    public void A_file_bigger_than_its_limit_is_refused()
    {
        using var zip = Open(ZipBytes(CompressionLevel.Optimal, ("big.json", new byte[3 << 20])), Small); // a few KB zipped
        var refused = Assert.Throws<InvalidDataException>(() => zip.ReadText(zip.Entries[0]));
        Assert.Equal("'big.json' unpacks to more than 1 MB, more than Atlas reads from one file", refused.Message);
    }

    // The zip with its headers saying one of its files unpacks to 10 bytes, whatever it really does.
    private static byte[] LyingAbout(byte[] zip, string name)
    {
        var bytes = (byte[])zip.Clone();
        byte[] wanted = Encoding.ASCII.GetBytes(name);
        bool Named(int at, int lengthAt) =>
            at + wanted.Length <= bytes.Length && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(lengthAt)) == wanted.Length && bytes.AsSpan(at, wanted.Length).SequenceEqual(wanted);
        for (int i = 0; i + 46 <= bytes.Length; i++)
        {
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4));
            if (signature == 0x04034b50 && Named(i + 30, i + 26)) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22, 4), 10); // local header
            else if (signature == 0x02014b50 && Named(i + 46, i + 28)) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24, 4), 10); // central directory
        }
        return bytes;
    }

    // A zip of one 3 MB file whose headers say it unpacks to 10 bytes.
    private static SafeZip Lying(CompressionLevel level)
    {
        var lying = Open(LyingAbout(ZipBytes(level, ("bomb.json", new byte[3 << 20])), "bomb.json"), Small);
        Assert.Equal(10, lying.Entries[0].Length);
        return lying;
    }

    [Fact]
    public void A_compressed_file_that_lies_about_its_size_gives_no_more_than_it_says()
    {
        // .NET's decompressor stops at the size the headers give, so the lie only cuts the file short.
        using var lying = Lying(CompressionLevel.Optimal);
        Assert.Equal(10, lying.ReadTextBytes(lying.Entries[0]).Length);
    }

    [Fact]
    public void A_stored_file_that_lies_about_its_size_is_refused_by_what_it_unpacks_to()
    {
        // A stored (uncompressed) file is read to the end of its bytes in the zip, whatever its size says.
        using var lying = Lying(CompressionLevel.NoCompression);
        var refused = Assert.Throws<InvalidDataException>(() => lying.ReadTextBytes(lying.Entries[0]));
        Assert.Contains("unpacks to more than 1 MB", refused.Message);
    }

    [Fact]
    public void What_is_read_from_one_zip_is_limited_in_total_for_each_kind()
    {
        var mb = new byte[1 << 20];
        using var zip = Open(ZipBytes(CompressionLevel.Optimal,
            ("a.lua", mb), ("b.lua", mb), ("c.lua", mb), ("images/a.png", mb), ("images/b.png", mb)), Small);
        zip.ReadTextBytes(zip.GetEntry("a.lua")!);
        zip.ReadText(zip.GetEntry("b.lua")!);
        var refused = Assert.Throws<InvalidDataException>(() => zip.ReadTextBytes(zip.GetEntry("c.lua")!));
        Assert.Equal("'test.zip' holds more than 2 MB of text, more than Atlas reads from one zip", refused.Message);
        // Images are counted apart from text.
        zip.ReadImage(zip.GetEntry("images/a.png")!);
        zip.ReadImage(zip.GetEntry("images/b.png")!);
    }

    [Fact]
    public void A_file_that_lies_about_its_size_is_counted_toward_the_total_as_it_unpacks()
    {
        // Within its own limit, but its headers hide that it takes the zip past its total.
        var mb = new byte[1 << 20];
        byte[] bytes = LyingAbout(ZipBytes(CompressionLevel.NoCompression, ("a.lua", mb), ("b.lua", mb), ("c.lua", new byte[900 << 10])), "c.lua");
        using var zip = Open(bytes, Small);
        Assert.Equal(10, zip.GetEntry("c.lua")!.Length);
        zip.ReadTextBytes(zip.GetEntry("a.lua")!);
        zip.ReadTextBytes(zip.GetEntry("b.lua")!);
        var refused = Assert.Throws<InvalidDataException>(() => zip.ReadTextBytes(zip.GetEntry("c.lua")!));
        Assert.Contains("holds more than 2 MB of text", refused.Message);
    }

    [Fact]
    public void A_file_from_another_zip_is_refused()
    {
        byte[] bytes = ZipBytes(CompressionLevel.Optimal, ("a.json", new byte[10]));
        using var one = Open(bytes);
        using var other = Open(bytes, Small);
        Assert.Throws<ArgumentException>(() => other.ReadText(one.Entries[0]));
    }

    [Fact]
    public void A_zip64_zip_is_refused_before_its_files_are_listed()
    {
        // .NET writes a zip64 zip for more than 65,534 files (as other tools do), and lists as many files as a zip64 record says.
        var many = new MemoryStream();
        using (var zip = new ZipArchive(many, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < 65_535; i++) zip.CreateEntry("e" + i, CompressionLevel.NoCompression);
        var refused = Assert.Throws<InvalidDataException>(() => Open(many.ToArray()));
        Assert.Equal("'test.zip' is a zip64 zip (made for more than 65,534 files, or over 4 GB), which Atlas doesn't read", refused.Message);

        // Each of the end record's zip64 marks is enough.
        byte[] small = ZipBytes(CompressionLevel.Optimal, ("a.json", new byte[10]));
        int end = small.Length - 22;
        foreach (var (offset, size) in new[] { (4, 2), (10, 2), (16, 4) })
        {
            var marked = (byte[])small.Clone();
            marked.AsSpan(end + offset, size).Fill(0xFF);
            Assert.Contains("zip64", Assert.Throws<InvalidDataException>(() => Open(marked)).Message);
        }
        using var unmarked = Open(small);
        Assert.Single(unmarked.Entries);
    }

    [Fact]
    public void Something_that_isnt_a_zip_is_refused()
    {
        var random = new byte[5000];
        new Random(3).NextBytes(random);
        random.AsSpan().Replace((byte)0x50, (byte)0x51); // no "PK" anywhere
        Assert.Equal("'test.zip' isn't a zip file", Assert.Throws<InvalidDataException>(() => Open(random)).Message);
        Assert.Throws<InvalidDataException>(() => Open([]));
        // An end record cut short: its signature is too near the end for the record to fit.
        byte[] zip = ZipBytes(CompressionLevel.Optimal, ("a.json", new byte[10]));
        Assert.Throws<InvalidDataException>(() => Open(zip.AsSpan(0, zip.Length - 1).ToArray()));
    }

    [Fact]
    public void A_zip_with_a_comment_opens()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.Comment = "made by a pack author's tool";
            using var entry = zip.CreateEntry("a.json").Open();
            entry.Write("{}"u8);
        }
        using var opened = Open(stream.ToArray());
        Assert.Equal("{}", opened.ReadText(opened.Entries[0]));
    }

    // Image headers, as each format writes them.
    private static byte[] PngHeader(int width, int height)
    {
        var data = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), height);
        return data;
    }

    private static byte[] Jpeg(int width, int height)
    {
        var data = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        data.AddRange("JFIF\0"u8.ToArray());
        data.AddRange(new byte[9]); // the rest of the APP0 segment, skipped by its length
        data.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03 });
        data.AddRange(new byte[9]);
        return data.ToArray();
    }

    private static byte[] WebP(string chunk, params byte[] body)
    {
        var data = new List<byte>();
        data.AddRange("RIFF"u8.ToArray());
        data.AddRange(new byte[4]);
        data.AddRange("WEBP"u8.ToArray());
        data.AddRange(Encoding.ASCII.GetBytes(chunk));
        data.AddRange(new byte[4]);
        data.AddRange(body);
        return data.ToArray();
    }

    private static byte[] WebPExtended(int width, int height) => WebP("VP8X",
        0, 0, 0, 0, (byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16), (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16));

    private static byte[] WebPLossless(int width, int height)
    {
        uint bits = (uint)(width - 1) | (uint)(height - 1) << 14;
        return WebP("VP8L", 0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24));
    }

    private static byte[] WebPLossy(int width, int height) => WebP("VP8 ",
        0, 0, 0, 0x9D, 0x01, 0x2A, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8));

    [Fact]
    public void Image_sizes_are_read_from_their_headers()
    {
        Assert.Equal((64, 32), ImageHeader.Size(FakeMapPack.Png(64, 32)));
        Assert.Equal((20000, 30000), ImageHeader.Size(PngHeader(20000, 30000)));
        Assert.Equal((640, 480), ImageHeader.Size(Jpeg(640, 480)));
        Assert.Equal((300, 200), ImageHeader.Size(WebPExtended(300, 200)));
        Assert.Equal((300, 200), ImageHeader.Size(WebPLossless(300, 200)));
        Assert.Equal((300, 200), ImageHeader.Size(WebPLossy(300, 200)));
    }

    [Fact]
    public void A_JPEG_frame_is_found_where_a_decoder_finds_it()
    {
        // Decoders skip stray bytes before a marker and don't take 0xFF 0x00 as one. Read strictly by segment lengths,
        // "FF 00 00 14" would look like a 20-byte segment that hides the real (huge) frame behind a small decoy.
        var hidden = new List<byte> { 0xFF, 0xD8, 0xFF, 0x00, 0x00, 0x14 };
        hidden.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x4E, 0x20, 0x4E, 0x20, 0x03 }); // 20,000 × 20,000
        while (hidden.Count < 24) hidden.Add(0);
        hidden.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x0A, 0x00, 0x0A, 0x03 }); // the decoy, 10 × 10
        Assert.Equal((20000, 20000), ImageHeader.Size(hidden.ToArray()));

        // Stray bytes between segments, and fill bytes before a marker.
        var stray = Jpeg(640, 480).ToList();
        stray.InsertRange(20, new byte[] { 0x12, 0x34, 0xFF, 0xFF });
        Assert.Equal((640, 480), ImageHeader.Size(stray.ToArray()));

        // A frame after the image data starts is never read: decoders stop at the image data.
        var late = new List<byte> { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02 };
        late.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x0A, 0x00, 0x0A, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Null(ImageHeader.Size(late.ToArray()));
    }

    [Fact]
    public void Anything_else_has_no_size_and_never_fails()
    {
        Assert.Null(ImageHeader.Size(ReadOnlySpan<byte>.Empty));
        Assert.Null(ImageHeader.Size("not an image"u8));
        Assert.Null(ImageHeader.Size(PngHeader(0, 10)));
        Assert.Null(ImageHeader.Size(FakeMapPack.Png(8, 8).AsSpan(0, 20))); // cut short
        Assert.Null(ImageHeader.Size(Jpeg(640, 480).AsSpan(0, 27))); // the frame's size cut off
        var random = new Random(9);
        for (int i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 64)];
            random.NextBytes(bytes);
            // Some start like a JPEG or a WebP, with garbage after.
            if (bytes.Length > 2 && i % 3 == 0) { bytes[0] = 0xFF; bytes[1] = 0xD8; }
            if (bytes.Length > 16 && i % 3 == 1) { "RIFF"u8.CopyTo(bytes); "WEBP"u8.CopyTo(bytes.AsSpan(8)); }
            ImageHeader.Size(bytes);
        }
    }

    [Fact]
    public void The_image_budget_refuses_images_too_large_and_a_pack_past_its_total()
    {
        var budget = new ImageBudget(total: 3 * 100 * 100 * 4); // room for three 100 × 100 images
        Assert.Equal("it's 16,385 × 10 pixels, larger than Atlas shows (16,384 a side)", budget.Take(ImageBudget.MaxSide + 1, 10));
        Assert.Contains("no size", budget.Take(0, 10));
        Assert.Contains("whose size Atlas can read", budget.Take("not an image"u8));
        Assert.Null(budget.Take(FakeMapPack.Png(100, 100)));
        for (int i = 0; i < 2; i++) Assert.Null(budget.Take(100, 100));
        Assert.Equal("the pack's images together come to more than Atlas decodes (120,000 bytes)", budget.Take(1, 1));
        // The real packs' longest side (12,560) fits, and so does their largest total (821 MB).
        var real = new ImageBudget();
        Assert.Null(real.Take(2769, 12560));
        for (int i = 0; i < 3; i++) Assert.Null(real.Take(8192, 8192));
    }
}
