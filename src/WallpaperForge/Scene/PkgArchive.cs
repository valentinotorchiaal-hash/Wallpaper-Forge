using System.Text;

namespace WallpaperForge.Scene;

/// <summary>
/// Lector del contenedor .pkg de escenas de Wallpaper Engine.
/// Estructura (little endian):
///   int32 len + magic ("PKGV0001".."PKGV0023")
///   int32 cantidad de entradas
///   por entrada: int32 len + ruta, int32 offset, int32 tamaño
///   datos (los offsets son relativos al final del encabezado)
/// Formato documentado por la comunidad (RePKG, MIT).
/// </summary>
public sealed class PkgArchive
{
    public sealed record Entry(string Path, int Offset, int Length);

    public string Magic { get; }
    public IReadOnlyList<Entry> Entries { get; }
    private readonly string _file;
    private readonly long _dataStart;

    private PkgArchive(string file, string magic, List<Entry> entries, long dataStart)
    {
        _file = file;
        Magic = magic;
        Entries = entries;
        _dataStart = dataStart;
    }

    public static PkgArchive Open(string file)
    {
        using var fs = File.OpenRead(file);
        using var br = new BinaryReader(fs, Encoding.UTF8);
        var magic = ReadSizedString(br, 32);
        if (!magic.StartsWith("PKGV", StringComparison.Ordinal))
            throw new InvalidDataException($"No es un .pkg válido (magic '{magic}')");

        int count = br.ReadInt32();
        if (count < 0 || count > 100_000) throw new InvalidDataException("Cantidad de entradas inválida");
        var list = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            var path = ReadSizedString(br, 1024).Replace('\\', '/');
            int offset = br.ReadInt32();
            int length = br.ReadInt32();
            list.Add(new Entry(path, offset, length));
        }
        long dataStart = fs.Position;
        foreach (var e in list)
            if (e.Offset < 0 || e.Length < 0 || dataStart + e.Offset + e.Length > fs.Length)
                throw new InvalidDataException($"Entrada fuera de rango: {e.Path}");
        return new PkgArchive(file, magic, list, dataStart);
    }

    public Entry? Find(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        return Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] Read(Entry e)
    {
        using var fs = File.OpenRead(_file);
        fs.Seek(_dataStart + e.Offset, SeekOrigin.Begin);
        var buf = new byte[e.Length];
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>Extrae todo a una carpeta (protegido contra rutas con "..").</summary>
    public void ExtractAll(string destination)
    {
        var root = Path.GetFullPath(destination);
        foreach (var e in Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, e.Path));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, Read(e));
        }
    }

    private static string ReadSizedString(BinaryReader br, int max)
    {
        int len = br.ReadInt32();
        if (len < 0 || len > max) throw new InvalidDataException("Cadena de largo inválido en .pkg");
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }
}
