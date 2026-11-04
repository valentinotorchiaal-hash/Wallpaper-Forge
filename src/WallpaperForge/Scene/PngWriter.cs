using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WallpaperForge.Scene;

/// <summary>Codificador PNG RGBA mínimo (sin dependencias de System.Drawing).</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bits por canal
        ihdr[9] = 6;  // RGBA
        WriteChunk(ms, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                int stride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0); // filtro None
                    z.Write(rgba, y * stride, stride);
                }
            }
            WriteChunk(ms, "IDAT", raw.ToArray());
        }
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        crc = Update(crc, typeBytes);
        crc = Update(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc ^ 0xFFFFFFFF);
        s.Write(len);
    }

    private static uint Update(uint crc, byte[] data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
