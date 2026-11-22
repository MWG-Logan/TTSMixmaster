using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TTSMixmaster.Core.Tts;

/// <summary>
/// Saved-object thumbnails without an image library: a generated 256×256 placeholder PNG (a vinyl record on
/// a dark tile), and a strict check that a downloaded thumbnail really is a decodable 256×256 PNG. Anything
/// else (JPEG, other sizes, truncated or corrupt data) is replaced by the placeholder.
/// </summary>
internal static class PlaceholderThumbnail
{
    public const int Size = 256;
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly Lazy<byte[]> Image = new(CreatePlaceholder);
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Png => Image.Value;

    /// <summary>
    /// True only for a complete PNG of exactly <see cref="Size"/>×<see cref="Size"/>: signature, IHDR first,
    /// every chunk CRC valid, IEND last with nothing after it, non-interlaced, and image data that inflates to
    /// exactly the scanline bytes its header implies (each with a valid filter byte).
    /// </summary>
    public static bool IsValid256Png(byte[] bytes)
    {
        try
        {
            if (bytes.Length < Signature.Length || !bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature))
                return false;
            int width = 0, height = 0, depth = 0, color = -1, interlace = -1;
            using var data = new MemoryStream();
            var position = Signature.Length;
            var first = true;
            var ended = false;
            while (!ended)
            {
                if (position + 12 > bytes.Length)
                    return false;
                var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position));
                if (length < 0 || position + 12L + length > bytes.Length)
                    return false;
                var type = Encoding.ASCII.GetString(bytes, position + 4, 4);
                var body = bytes.AsSpan(position + 8, length);
                if (Crc(bytes.AsSpan(position + 4, 4 + length)) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8 + length)))
                    return false;
                if (first != (type == "IHDR"))
                    return false;
                switch (type)
                {
                    case "IHDR":
                        if (length != 13 || body[10] != 0 || body[11] != 0)
                            return false;
                        width = BinaryPrimitives.ReadInt32BigEndian(body);
                        height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                        (depth, color, interlace) = (body[8], body[9], body[12]);
                        break;
                    case "IDAT":
                        data.Write(body);
                        break;
                    case "IEND":
                        ended = true;
                        break;
                }
                first = false;
                position += 12 + length;
            }
            if (position != bytes.Length || width != Size || height != Size || interlace != 0)
                return false;
            var channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
            var validDepth = color switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => depth is 8 or 16 };
            if (channels == 0 || !validDepth)
                return false;
            var rowBytes = (width * channels * depth + 7) / 8 + 1;
            var pixels = new byte[rowBytes * height + 1];   // one spare byte detects trailing data
            data.Position = 0;
            using var inflater = new ZLibStream(data, CompressionMode.Decompress);
            var total = 0;
            int read;
            while (total < pixels.Length && (read = inflater.Read(pixels, total, pixels.Length - total)) > 0)
                total += read;
            if (total != rowBytes * height)
                return false;
            for (var row = 0; row < height; row++)
                if (pixels[row * rowBytes] > 4)   // PNG filter types are 0..4
                    return false;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Encodes an 8-bit RGB image (<paramref name="rgb"/> is width*height*3 bytes) as a PNG.</summary>
    public static byte[] Encode(int width, int height, byte[] rgb)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
            Array.Copy(rgb, y * width * 3, raw, y * (1 + width * 3) + 1, width * 3);   // filter byte 0 per row

        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
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

    private static byte[] CreatePlaceholder()
    {
        var rgb = new byte[Size * Size * 3];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Math.Sqrt(Math.Pow(x - 127.5, 2) + Math.Pow(y - 127.5, 2));
                (byte R, byte G, byte B) color = r switch
                {
                    < 5 => (0x25, 0x25, 0x32),                       // spindle hole
                    < 38 => (0xE0, 0x5A, 0x2B),                      // label
                    < 112 when (int)r % 9 == 0 => (0x2C, 0x2C, 0x34), // groove highlight
                    < 112 => (0x14, 0x14, 0x18),                     // vinyl
                    _ => (0x25, 0x25, 0x32),                         // tile
                };
                var i = (y * Size + x) * 3;
                (rgb[i], rgb[i + 1], rgb[i + 2]) = color;
            }
        }
        return Encode(Size, Size, rgb);
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var chunk = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, chunk);
        data.CopyTo(chunk, 4);
        png.Write(chunk);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(chunk));
        png.Write(number);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
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
