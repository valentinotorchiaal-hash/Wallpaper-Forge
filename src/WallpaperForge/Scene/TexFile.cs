using System.Text;
using K4os.Compression.LZ4;

namespace WallpaperForge.Scene;

/// <summary>
/// Lector de texturas .tex de Wallpaper Engine (TEXV0005 / TEXI0001 / TEXB0001..0004 / TEXS0001..0003).
/// Formato documentado por la comunidad (RePKG, MIT). Implementación propia.
/// </summary>
public sealed class TexFile
{
    public enum TexFormat { RGBA8888 = 0, DXT5 = 4, DXT3 = 6, DXT1 = 7, RG88 = 8, R8 = 9 }

    [Flags]
    public enum TexFlags { None = 0, NoInterpolation = 1, ClampUVs = 2, IsGif = 4, IsVideoTexture = 32 }

    public sealed record Frame(int ImageId, float FrameTime, float X, float Y, float Width, float Height);

    /// <summary>Imagen ya decodificada: o RGBA crudo, o bytes de un formato estándar (png/jpg/...).</summary>
    public sealed class Image
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[]? Rgba { get; init; }
        public byte[]? Encoded { get; init; }
        public string? EncodedExtension { get; init; }
    }

    public TexFormat Format { get; private set; }
    public TexFlags Flags { get; private set; }
    public int TextureWidth { get; private set; }
    public int TextureHeight { get; private set; }
    /// <summary>Tamaño real de la imagen (la textura puede estar rellenada a potencia de 2).</summary>
    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }
    public bool IsVideo { get; private set; }
    public List<Image> Images { get; } = new();
    public List<Frame> Frames { get; } = new();
    public int GifWidth { get; private set; }
    public int GifHeight { get; private set; }

    public bool IsAnimated => (Flags & TexFlags.IsGif) != 0 && Frames.Count > 0;

    public static TexFile Read(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.ASCII);
        var tex = new TexFile();

        Expect(ReadNString(br), "TEXV0005");
        Expect(ReadNString(br), "TEXI0001");

        tex.Format = (TexFormat)br.ReadInt32();
        tex.Flags = (TexFlags)br.ReadInt32();
        tex.TextureWidth = br.ReadInt32();
        tex.TextureHeight = br.ReadInt32();
        tex.ImageWidth = br.ReadInt32();
        tex.ImageHeight = br.ReadInt32();
        br.ReadUInt32(); // desconocido

        var container = ReadNString(br);
        int imageCount = br.ReadInt32();
        if (imageCount is < 0 or > 1024) throw new InvalidDataException("Cantidad de imágenes inválida");

        int freeImageFormat = -1;
        int version;
        switch (container)
        {
            case "TEXB0001": version = 1; break;
            case "TEXB0002": version = 2; break;
            case "TEXB0003": version = 3; freeImageFormat = br.ReadInt32(); break;
            case "TEXB0004":
                freeImageFormat = br.ReadInt32();
                tex.IsVideo = br.ReadInt32() == 1;
                version = tex.IsVideo ? 4 : 3;
                break;
            default: throw new InvalidDataException($"Contenedor de textura desconocido '{container}'");
        }

        for (int i = 0; i < imageCount; i++)
        {
            int mipCount = br.ReadInt32();
            if (mipCount is < 0 or > 64) throw new InvalidDataException("Cantidad de mipmaps inválida");
            Image? first = null;
            for (int m = 0; m < mipCount; m++)
            {
                if (version == 4)
                {
                    // Parámetros del editor en texturas de video; se ignoran.
                    br.ReadInt32(); br.ReadInt32(); ReadNString(br); br.ReadInt32();
                }
                int w = br.ReadInt32();
                int h = br.ReadInt32();
                bool lz4 = false;
                int decompressed = 0;
                if (version >= 2)
                {
                    lz4 = br.ReadInt32() == 1;
                    decompressed = br.ReadInt32();
                }
                int byteCount = br.ReadInt32();
                if (byteCount < 0 || ms.Position + byteCount > ms.Length)
                    throw new InvalidDataException("Mipmap fuera de rango");
                var bytes = br.ReadBytes(byteCount);

                if (m != 0) continue; // sólo interesa el mipmap de mayor resolución
                if (lz4)
                {
                    var outBuf = new byte[decompressed];
                    int n = LZ4Codec.Decode(bytes, 0, bytes.Length, outBuf, 0, outBuf.Length);
                    if (n < 0) throw new InvalidDataException("LZ4 inválido");
                    bytes = outBuf;
                }
                first = tex.Decode(bytes, w, h, freeImageFormat);
            }
            if (first != null) tex.Images.Add(first);
        }

        if ((tex.Flags & TexFlags.IsGif) != 0 && ms.Position < ms.Length)
            tex.ReadFrames(br);

        return tex;
    }

    private Image Decode(byte[] bytes, int w, int h, int freeImageFormat)
    {
        if (IsVideo)
            return new Image { Width = w, Height = h, Encoded = bytes, EncodedExtension = ".mp4" };

        if (freeImageFormat != -1)
            return new Image { Width = w, Height = h, Encoded = bytes, EncodedExtension = ExtensionFor(freeImageFormat) };

        byte[] rgba = Format switch
        {
            TexFormat.RGBA8888 => bytes,
            TexFormat.DXT1 => Dxt.Decode(bytes, w, h, Dxt.Kind.Dxt1),
            TexFormat.DXT3 => Dxt.Decode(bytes, w, h, Dxt.Kind.Dxt3),
            TexFormat.DXT5 => Dxt.Decode(bytes, w, h, Dxt.Kind.Dxt5),
            TexFormat.RG88 => ExpandRg88(bytes, w, h),
            TexFormat.R8 => ExpandR8(bytes, w, h),
            _ => throw new InvalidDataException($"Formato de textura no soportado: {Format}"),
        };

        // Recortar al tamaño real de la imagen (sin el relleno a potencia de 2).
        int iw = ImageWidth > 0 && ImageWidth <= w ? ImageWidth : w;
        int ih = ImageHeight > 0 && ImageHeight <= h ? ImageHeight : h;
        if ((iw != w || ih != h) && !((Flags & TexFlags.IsGif) != 0))
        {
            rgba = Crop(rgba, w, iw, ih);
            w = iw; h = ih;
        }
        return new Image { Width = w, Height = h, Rgba = rgba };
    }

    private void ReadFrames(BinaryReader br)
    {
        var magic = ReadNString(br);
        int count = br.ReadInt32();
        if (count is < 0 or > 100_000) return;
        if (magic == "TEXS0003")
        {
            GifWidth = br.ReadInt32();
            GifHeight = br.ReadInt32();
        }
        for (int i = 0; i < count; i++)
        {
            int imageId = br.ReadInt32();
            float ft = br.ReadSingle();
            float x, y, fw, fh;
            if (magic == "TEXS0001")
            {
                x = br.ReadInt32(); y = br.ReadInt32(); fw = br.ReadInt32(); br.ReadInt32(); br.ReadInt32(); fh = br.ReadInt32();
            }
            else
            {
                x = br.ReadSingle(); y = br.ReadSingle(); fw = br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); fh = br.ReadSingle();
            }
            Frames.Add(new Frame(imageId, ft, x, y, fw, fh));
        }
        if ((GifWidth == 0 || GifHeight == 0) && Frames.Count > 0)
        {
            GifWidth = (int)Math.Abs(Frames[0].Width);
            GifHeight = (int)Math.Abs(Frames[0].Height);
        }
    }

    private static byte[] Crop(byte[] rgba, int srcW, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(rgba, y * srcW * 4, dst, y * w * 4, w * 4);
        return dst;
    }

    private static byte[] ExpandRg88(byte[] src, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int i = 0, n = Math.Min(w * h, src.Length / 2); i < n; i++)
        {
            byte l = src[i * 2], a = src[i * 2 + 1];
            dst[i * 4] = l; dst[i * 4 + 1] = l; dst[i * 4 + 2] = l; dst[i * 4 + 3] = a;
        }
        return dst;
    }

    private static byte[] ExpandR8(byte[] src, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int i = 0, n = Math.Min(w * h, src.Length); i < n; i++)
        {
            byte l = src[i];
            dst[i * 4] = l; dst[i * 4 + 1] = l; dst[i * 4 + 2] = l; dst[i * 4 + 3] = 255;
        }
        return dst;
    }

    private static string ExtensionFor(int fif) => fif switch
    {
        0 => ".bmp", 2 => ".jpg", 13 => ".png", 17 => ".tga", 18 => ".tiff", 25 => ".gif", 35 => ".mp4",
        _ => ".bin",
    };

    private static string ReadNString(BinaryReader br, int max = 32)
    {
        var sb = new StringBuilder();
        while (true)
        {
            byte b = br.ReadByte();
            if (b == 0) break;
            if (sb.Length < max) sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static void Expect(string actual, string expected)
    {
        if (actual != expected) throw new InvalidDataException($"Se esperaba '{expected}' y se encontró '{actual}'");
    }
}
