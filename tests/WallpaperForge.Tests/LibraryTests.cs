using WallpaperForge.Wallpapers;
using Xunit;

namespace WallpaperForge.Tests;

public class LibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wf-lib-" + Guid.NewGuid().ToString("N"));
    public LibraryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Touch(string rel, string content = "x")
    {
        // GetFullPath normaliza los separadores (en Windows "/" pasa a "\\"), igual que los que devuelve el escaneo.
        var p = Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private Dictionary<string, WallpaperProject> Scan(params string[] hidden)
    {
        var found = new Dictionary<string, WallpaperProject>();
        Library.ScanDirectory(found, _root, WallpaperSource.Local, 4, new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase));
        return found;
    }

    [Fact]
    public void LooseFilesInSubfolder_AreEachAWallpaper()
    {
        // El caso real: una subcarpeta "verdes" con videos, imágenes y GIF sueltos.
        Touch("verdes/bosque.mp4");
        Touch("verdes/hojas.gif");
        Touch("verdes/lago.jpg");
        Touch("verdes/notas.txt");
        var items = Scan().Values.ToList();
        Assert.Equal(3, items.Count);
        Assert.Contains(items, p => p.Title == "bosque" && p.Type == WallpaperType.Video && p.PreviewFile == null);
        Assert.Contains(items, p => p.Title == "hojas" && p.Type == WallpaperType.Image && p.PreviewFile == "hojas.gif");
        Assert.Contains(items, p => p.Title == "lago" && p.Type == WallpaperType.Image);
        Assert.All(items, p => Assert.True(p.IsLooseFile));
        Assert.All(items, p => Assert.Contains("verdes", p.Tags));
    }

    [Fact]
    public void ProjectFolders_AreNotSplitIntoFiles()
    {
        Touch("proyecto/project.json", """{ "title": "P", "type": "video", "file": "v.mp4" }""");
        Touch("proyecto/v.mp4");
        Touch("proyecto/preview.gif");
        Touch("web/index.html", "<html></html>");
        Touch("web/fondo.png");
        var items = Scan().Values.ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, p => p.Title == "P" && !p.IsLooseFile);
        Assert.Contains(items, p => p.Type == WallpaperType.Web);
    }

    [Fact]
    public void DeepFolders_AndHiddenItems()
    {
        var deep = Touch("a/b/c/d/clip.webm");
        var hidden = Touch("x/oculto.mp4");
        Touch("a/b/c/d/e/f/demasiado.mp4"); // más de 4 niveles
        var items = Scan(hidden).Values.ToList();
        Assert.Single(items);
        Assert.Equal(deep, items[0].Location);
    }

    [Fact]
    public void LooseFile_RoundTripsByLocation()
    {
        var f = Touch("verdes/rio.mp4");
        var p = WallpaperProject.Load(f, WallpaperSource.Local)!;
        Assert.Equal(f, p.Location);
        Assert.Equal(WallpaperProject.MakeId(f), p.Id);
        Assert.Equal(Path.GetDirectoryName(f), p.Folder);
        Assert.Equal(f, p.EntryPath);
    }

    [Fact]
    public void PreviewFilesAreNotWallpapers()
    {
        Assert.False(WallpaperProject.IsStandaloneMedia("preview.gif"));
        Assert.True(WallpaperProject.IsStandaloneMedia("PREVIEW-2.gif"));
        Assert.False(WallpaperProject.IsStandaloneMedia("nota.txt"));
    }
}
