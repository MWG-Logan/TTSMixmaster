using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TTSMixmaster.Core.Tts;

/// <summary>
/// A 256×256 PNG (the size TTS uses for Saved Objects thumbnails): a vinyl record on a dark tile.
/// Encoded here so Core needs no image library.
/// </summary>
internal static class PlaceholderThumbnail
{
    private const int Size = 256;
    private static readonly Lazy<byte[]> Image = new(Create);
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Png => Image.Value;

    public static bool IsPngOrJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]) ||
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);

    private static byte[] Create()
    {
        // Raw scanlines: filter byte 0, then RGB.
        var raw = new byte[Size * (1 + Size * 3)];
        for (var y = 0; y < Size; y++)
        {
            var row = y * (1 + Size * 3);
            for (var x = 0; x < Size; x++)
            {
                var r = Math.Sqrt(Math.Pow(x - 127.5, 2) + Math.Pow(y - 127.5, 2));
                (byte R, byte G, byte B) color = r switch
                {
                    < 5 => (0x25, 0x25, 0x32),                                   // spindle hole
                    < 38 => (0xE0, 0x5A, 0x2B),                                  // label
                    < 112 when (int)r % 9 == 0 => (0x2C, 0x2C, 0x34),               // groove highlight
                    < 112 => (0x14, 0x14, 0x18),                                 // vinyl
                    _ => (0x25, 0x25, 0x32),                                     // tile
                };
                var i = row + 1 + x * 3;
                (raw[i], raw[i + 1], raw[i + 2]) = color;
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Size);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: truecolour RGB
        WriteChunk(png, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            WriteChunk(png, "IDAT", compressed.ToArray());
        }
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data))
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFFu);
        png.Write(number);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
