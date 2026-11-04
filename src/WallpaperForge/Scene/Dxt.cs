namespace WallpaperForge.Scene;

/// <summary>Decodificador BC1/BC2/BC3 (DXT1/DXT3/DXT5) a RGBA8888.</summary>
public static class Dxt
{
    public enum Kind { Dxt1, Dxt3, Dxt5 }

    public static byte[] Decode(byte[] src, int width, int height, Kind kind)
    {
        var dst = new byte[width * height * 4];
        int blockSize = kind == Kind.Dxt1 ? 8 : 16;
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[16];
        int offset = 0;

        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            if (offset + blockSize > src.Length) return dst;
            int colorOffset = offset;
            if (kind == Kind.Dxt3) { DecodeExplicitAlpha(src, offset, alpha); colorOffset += 8; }
            else if (kind == Kind.Dxt5) { DecodeInterpolatedAlpha(src, offset, alpha); colorOffset += 8; }
            else alpha.Fill(255);

            DecodeColorBlock(src, colorOffset, colors, kind == Kind.Dxt1, out var palette);
            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx * 4 + px, y = by * 4 + py;
                if (x >= width || y >= height) continue;
                int i = py * 4 + px;
                int ci = colors[i];
                int d = (y * width + x) * 4;
                dst[d] = palette[ci * 4];
                dst[d + 1] = palette[ci * 4 + 1];
                dst[d + 2] = palette[ci * 4 + 2];
                byte a = palette[ci * 4 + 3];
                dst[d + 3] = kind == Kind.Dxt1 ? a : alpha[i];
            }
            offset += blockSize;
        }
        return dst;
    }

    private static void DecodeColorBlock(byte[] s, int o, Span<byte> indices, bool dxt1, out byte[] palette)
    {
        ushort c0 = (ushort)(s[o] | s[o + 1] << 8);
        ushort c1 = (ushort)(s[o + 2] | s[o + 3] << 8);
        palette = new byte[16];
        Unpack565(c0, palette, 0);
        Unpack565(c1, palette, 4);
        palette[3] = palette[7] = 255;
        if (c0 > c1 || !dxt1)
        {
            for (int k = 0; k < 3; k++)
            {
                palette[8 + k] = (byte)((2 * palette[k] + palette[4 + k]) / 3);
                palette[12 + k] = (byte)((palette[k] + 2 * palette[4 + k]) / 3);
            }
            palette[11] = palette[15] = 255;
        }
        else
        {
            for (int k = 0; k < 3; k++) palette[8 + k] = (byte)((palette[k] + palette[4 + k]) / 2);
            palette[11] = 255;
            palette[12] = palette[13] = palette[14] = 0;
            palette[15] = 0; // transparente
        }
        uint bits = (uint)(s[o + 4] | s[o + 5] << 8 | s[o + 6] << 16 | s[o + 7] << 24);
        for (int i = 0; i < 16; i++) indices[i] = (byte)((bits >> (2 * i)) & 3);
    }

    private static void Unpack565(ushort c, byte[] p, int o)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        p[o] = (byte)((r << 3) | (r >> 2));
        p[o + 1] = (byte)((g << 2) | (g >> 4));
        p[o + 2] = (byte)((b << 3) | (b >> 2));
    }

    private static void DecodeExplicitAlpha(byte[] s, int o, Span<byte> alpha)
    {
        for (int i = 0; i < 8; i++)
        {
            byte v = s[o + i];
            alpha[i * 2] = (byte)((v & 0x0F) * 17);
            alpha[i * 2 + 1] = (byte)((v >> 4) * 17);
        }
    }

    private static void DecodeInterpolatedAlpha(byte[] s, int o, Span<byte> alpha)
    {
        int a0 = s[o], a1 = s[o + 1];
        Span<int> pal = stackalloc int[8];
        pal[0] = a0; pal[1] = a1;
        if (a0 > a1)
            for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            pal[6] = 0; pal[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)s[o + 2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) alpha[i] = (byte)pal[(int)((bits >> (3 * i)) & 7)];
    }
}
