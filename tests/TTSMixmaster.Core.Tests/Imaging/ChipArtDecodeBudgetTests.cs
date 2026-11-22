using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;
using TTSMixmaster.Core.Imaging;

namespace TTSMixmaster.Core.Tests.Imaging;

/// <summary>
/// Compressed size says nothing about decoded size: a uniform PNG of 16384² pixels is a few kilobytes but
/// would need 1 GiB as RGBA. Art is checked against a pixel budget from its header before anything is allocated.
/// </summary>
[TestClass]
public sealed class ChipArtDecodeBudgetTests
{
    [TestMethod]
    public void A_tiny_png_with_huge_dimensions_is_refused_before_decoding()
    {
        var png = BlankPng(16384, 16384);
        Assert.IsLessThan(200_000, png.Length, "the bomb itself is small");
        using (var codec = SKCodec.Create(new SKMemoryStream(png)))
            Assert.AreEqual(new SKSizeI(16384, 16384), codec!.Info.Size, "Skia would decode it at full size");

        Assert.IsNull(ChipArt.Decode(png));
        Assert.IsFalse(ChipImageBuilder.IsSupportedArt(png));
        Assert.AreEqual(ChipArtSource.Generated, ChipImageBuilder.RenderFront(png, "T", null).ArtSource);
    }

    [TestMethod]
    public void A_long_thin_png_over_the_pixel_budget_is_refused()
    {
        // 65536 × 300 is under 4096 on neither side but over the 16.7 M pixel budget.
        Assert.IsNull(ChipArt.Decode(BlankPng(65536, 300)));
    }

    [TestMethod]
    public void A_png_within_the_budget_is_decoded_and_kept_at_most_2048_on_its_longest_side()
    {
        // PNG's codec can't decode to a smaller size, so this decodes at 4096² (inside the budget) and is resized.
        using (var codec = SKCodec.Create(new SKMemoryStream(BlankPng(4096, 4096))))
            Assert.AreEqual(new SKSizeI(4096, 4096), codec!.GetScaledDimensions(0.5f));

        using var art = ChipArt.Decode(BlankPng(4096, 2048));

        Assert.IsNotNull(art);
        Assert.AreEqual((ChipArt.MaxDecodeSize, 1024), (art.Width, art.Height));
    }

    /// <summary>A valid all-black 1-bit greyscale PNG: one filter byte plus width/8 zero bytes per row, deflated.</summary>
    private static byte[] BlankPng(int width, int height)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[1 + (width + 7) / 8];
            for (var y = 0; y < height; y++)
                zlib.Write(row);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 1; // bit depth; colour type 0 (greyscale), deflate, adaptive filtering, no interlace
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc32(typed));
        png.Write(length);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
