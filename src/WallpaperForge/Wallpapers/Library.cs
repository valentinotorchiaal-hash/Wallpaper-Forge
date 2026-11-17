using System.Text.Json;
using System.Text.Json.Nodes;
using WallpaperForge.Core;

namespace WallpaperForge.Wallpapers;

/// <summary>Índice de todos los wallpapers disponibles (biblioteca propia, carpetas extra, Steam Workshop).</summary>
public sealed class Library
{
    private readonly object _gate = new();
    private Dictionary<string, WallpaperProject> _items = new();

    public IReadOnlyCollection<WallpaperProject> Items { get { lock (_gate) return _items.Values.ToList(); } }

    /// <summary>Raíces escaneadas en el último escaneo (para validar accesos a archivos desde la UI).</summary>
    public IReadOnlyList<string> Roots { get; private set; } = Array.Empty<string>();

    /// <summary>Resultado del último escaneo: qué carpetas se revisaron y qué archivos no se pudieron usar.</summary>
    public sealed class ScanReport
    {
        public List<(string Path, string Source, int Count, bool Exists)> Roots { get; } = new();
        public Dictionary<string, int> IgnoredExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Hidden { get; set; }
    }

    public ScanReport LastScan { get; private set; } = new();

    // Formatos de imagen/video comunes que todavía no se pueden usar (se informan en la interfaz).
    private static readonly HashSet<string> KnownUnsupported = new(StringComparer.OrdinalIgnoreCase)
        { ".heic", ".heif", ".tif", ".tiff", ".svg", ".ico", ".raw", ".cr2", ".nef", ".psd", ".jxl", ".3gp", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".vob" };

    public WallpaperProject? Get(string id) { lock (_gate) return _items.GetValueOrDefault(id); }

    public WallpaperProject? FindByLocation(string location) => Get(WallpaperProject.MakeId(location));

    public void Scan(AppSettings settings)
    {
        var roots = new List<(string path, WallpaperSource src)>();
        Directory.CreateDirectory(AppPaths.DefaultLibrary);
        roots.Add((AppPaths.DefaultLibrary, WallpaperSource.Local));
        roots.AddRange(settings.LibraryFolders.Where(Directory.Exists).Select(f => (f, WallpaperSource.Local)));

        if (settings.ScanSteamWorkshop)
        {
            try
            {
                roots.AddRange(SteamLocator.WorkshopFolders().Select(f => (f, WallpaperSource.SteamWorkshop)));
                var install = ResolveWallpaperEngineInstall(settings);
                roots.AddRange(SteamLocator.ProjectFolders(install, settings.IncludeWallpaperEngineSamples).Select(f => (f, WallpaperSource.WallpaperEngineProjects)));
            }
            catch (Exception ex) { Log.Warn("Error buscando Steam: " + ex.Message); }
        }

        var hidden = new HashSet<string>(settings.HiddenWallpapers, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, WallpaperProject>();
        var report = new ScanReport { Hidden = hidden.Count };
        // Las carpetas agregadas que ya no existen también se informan.
        foreach (var missing in settings.LibraryFolders.Where(f => !Directory.Exists(f)))
            report.Roots.Add((missing, "Local", 0, false));
        foreach (var (root, src) in roots)
        {
            // Steam Workshop: una carpeta por wallpaper. Carpetas propias: se busca en subcarpetas (hasta 4 niveles).
            int depth = src == WallpaperSource.Local ? 4 : 1;
            int before = found.Count;
            ScanDirectory(found, root, src, depth, hidden, report.IgnoredExtensions);
            report.Roots.Add((root, src.ToString(), found.Count - before, true));
        }

        lock (_gate)
        {
            _items = found;
            Roots = roots.Select(r => Path.GetFullPath(r.path)).ToList();
            LastScan = report;
        }
        if (report.IgnoredExtensions.Count > 0)
            Log.Info("Archivos no compatibles: " + string.Join(", ", report.IgnoredExtensions.Select(kv => $"{kv.Key} x{kv.Value}")));
        Log.Info($"Biblioteca: {found.Count} wallpapers en {roots.Count} carpetas ({hidden.Count} ocultos)");
    }

    private const int MaxItems = 20_000;

    /// <summary>
    /// Una carpeta con project.json (o index.html) es un wallpaper y no se sigue bajando.
    /// Si no, cada video, imagen o GIF suelto es un wallpaper, y se revisan las subcarpetas.
    /// </summary>
    internal static void ScanDirectory(Dictionary<string, WallpaperProject> found, string dir, WallpaperSource src,
        int depth, HashSet<string> hidden, Dictionary<string, int>? ignored = null)
    {
        if (found.Count >= MaxItems) return;
        try
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return;

            if (File.Exists(Path.Combine(dir, "project.json")) || File.Exists(Path.Combine(dir, "index.html")))
            {
                Add(found, WallpaperProject.Load(dir, src), hidden);
                return;
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (WallpaperProject.IsStandaloneMedia(file))
                    Add(found, WallpaperProject.Load(file, src), hidden);
                else if (ignored != null && KnownUnsupported.Contains(Path.GetExtension(file)))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    ignored[ext] = ignored.GetValueOrDefault(ext) + 1;
                }
            }

            if (depth <= 0) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // Carpetas ocultas o del sistema (p. ej. $RECYCLE.BIN) no se revisan.
                if ((File.GetAttributes(sub) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                ScanDirectory(found, sub, src, depth - 1, hidden, ignored);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Warn($"No se pudo leer {dir}: {ex.Message}");
        }
    }

    private static void Add(Dictionary<string, WallpaperProject> found, WallpaperProject? p, HashSet<string> hidden)
    {
        if (p == null || p.Type == WallpaperType.Unknown || hidden.Contains(p.Location)) return;
        found[p.Id] = p;
    }

    public static string? ResolveWallpaperEngineInstall(AppSettings s)
        => !string.IsNullOrWhiteSpace(s.WallpaperEngineInstallPath) && Directory.Exists(s.WallpaperEngineInstallPath)
            ? s.WallpaperEngineInstallPath
            : SteamLocator.WallpaperEngineInstall();

    /// <summary>true si el path está dentro de alguna raíz de la biblioteca o de la carpeta de un wallpaper conocido.</summary>
    public bool IsInsideLibrary(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        foreach (var r in Roots)
            if (full.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        lock (_gate)
            foreach (var p in _items.Values)
                if (full.StartsWith(p.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Copia videos, imágenes o GIF a la biblioteca propia (carpeta "Importados"). Devuelve las rutas nuevas.</summary>
    public static List<string> ImportMedia(IEnumerable<string> files)
    {
        var target = Path.Combine(AppPaths.DefaultLibrary, "Importados");
        Directory.CreateDirectory(target);
        var result = new List<string>();
        foreach (var f in files.Where(WallpaperProject.IsStandaloneMedia))
        {
            var name = Sanitize(Path.GetFileNameWithoutExtension(f));
            var ext = Path.GetExtension(f);
            var dest = Path.Combine(target, name + ext);
            for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(target, $"{name} ({i}){ext}");
            File.Copy(f, dest);
            result.Add(dest);
        }
        return result;
    }

    /// <summary>Copia una carpeta web (con index.html) a la biblioteca propia.</summary>
    public static string ImportWebFolder(string sourceFolder)
    {
        var name = new DirectoryInfo(sourceFolder).Name;
        var folder = UniqueFolder(Path.Combine(AppPaths.DefaultLibrary, Sanitize(name)));
        CopyDirectory(sourceFolder, folder);
        if (!File.Exists(Path.Combine(folder, "project.json")))
            WriteProject(folder, "web", "index.html", name);
        return folder;
    }

    private static void WriteProject(string folder, string type, string file, string title)
    {
        var project = new JsonObject
        {
            ["title"] = title,
            ["type"] = type,
            ["file"] = file,
            ["description"] = "",
            ["tags"] = new JsonArray(),
            ["general"] = new JsonObject { ["properties"] = new JsonObject() },
        };
        File.WriteAllText(Path.Combine(folder, "project.json"),
            project.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string UniqueFolder(string basePath)
    {
        var p = basePath;
        for (int i = 2; Directory.Exists(p); i++) p = $"{basePath} ({i})";
        return p;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "wallpaper" : name.Trim();
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
