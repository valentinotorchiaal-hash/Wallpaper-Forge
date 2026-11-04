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

    public WallpaperProject? Get(string id) { lock (_gate) return _items.GetValueOrDefault(id); }

    public WallpaperProject? FindByFolder(string folder) => Get(WallpaperProject.MakeId(folder));

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
                roots.AddRange(SteamLocator.ProjectFolders(install).Select(f => (f, WallpaperSource.WallpaperEngineProjects)));
            }
            catch (Exception ex) { Log.Warn("Error buscando Steam: " + ex.Message); }
        }

        var found = new Dictionary<string, WallpaperProject>();
        foreach (var (root, src) in roots)
        {
            // La raíz puede ser un wallpaper en sí misma (carpeta agregada a mano).
            TryAdd(found, root, src);
            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(root); }
            catch { continue; }
            foreach (var dir in subdirs) TryAdd(found, dir, src);
        }

        lock (_gate)
        {
            _items = found;
            Roots = roots.Select(r => Path.GetFullPath(r.path)).ToList();
        }
        Log.Info($"Biblioteca: {found.Count} wallpapers en {roots.Count} carpetas");
    }

    private static void TryAdd(Dictionary<string, WallpaperProject> found, string dir, WallpaperSource src)
    {
        var p = WallpaperProject.Load(dir, src);
        if (p != null && p.Type != WallpaperType.Unknown) found[p.Id] = p;
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

    /// <summary>Copia un video a la biblioteca propia creando un project.json compatible.</summary>
    public static string ImportVideo(string videoPath)
    {
        var name = Path.GetFileNameWithoutExtension(videoPath);
        var folder = UniqueFolder(Path.Combine(AppPaths.DefaultLibrary, Sanitize(name)));
        Directory.CreateDirectory(folder);
        var fileName = Path.GetFileName(videoPath);
        File.Copy(videoPath, Path.Combine(folder, fileName));
        WriteProject(folder, "video", fileName, name);
        return folder;
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
