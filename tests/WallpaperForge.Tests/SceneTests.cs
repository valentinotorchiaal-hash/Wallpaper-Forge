using System.Text;
using System.Text.Json.Nodes;
using K4os.Compression.LZ4;
using WallpaperForge.Scene;
using WallpaperForge.Wallpapers;
using Xunit;

namespace WallpaperForge.Tests;

/// <summary>Arma paquetes y texturas sintéticas con el formato de Wallpaper Engine y verifica la lectura.</summary>
public class SceneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-tests-" + Guid.NewGuid().ToString("N"));

    public SceneTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------- Escritores de prueba ----------

    internal static byte[] MakeTex(int format, int w, int h, byte[] payload, bool lz4 = false, int imageW = 0, int imageH = 0, int fif = -1)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        void NStr(string s) { bw.Write(Encoding.ASCII.GetBytes(s)); bw.Write((byte)0); }
        NStr("TEXV0005");
        NStr("TEXI0001");
        bw.Write(format); bw.Write(0); bw.Write(w); bw.Write(h);
        bw.Write(imageW == 0 ? w : imageW); bw.Write(imageH == 0 ? h : imageH); bw.Write(0u);
        NStr("TEXB0003");
        bw.Write(1);      // imágenes
        bw.Write(fif);    // FreeImage format
        bw.Write(1);      // mipmaps
        bw.Write(w); bw.Write(h);
        byte[] data = payload;
        if (lz4)
        {
            var buf = new byte[LZ4Codec.MaximumOutputSize(payload.Length)];
            int n = LZ4Codec.Encode(payload, 0, payload.Length, buf, 0, buf.Length);
            data = buf[..n];
        }
        bw.Write(lz4 ? 1 : 0);
        bw.Write(payload.Length);
        bw.Write(data.Length);
        bw.Write(data);
        return ms.ToArray();
    }

    internal static void MakePkg(string path, Dictionary<string, byte[]> files)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        void SStr(string s) { var b = Encoding.UTF8.GetBytes(s); bw.Write(b.Length); bw.Write(b); }
        SStr("PKGV0019");
        bw.Write(files.Count);
        int offset = 0;
        foreach (var (name, data) in files)
        {
            SStr(name); bw.Write(offset); bw.Write(data.Length);
            offset += data.Length;
        }
        foreach (var data in files.Values) bw.Write(data);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // ---------- Tests ----------

    [Fact]
    public void Dxt1_SolidRedBlock()
    {
        // c0 = rojo puro (565), c1 = azul, índices 0 → todo rojo.
        var block = new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0 };
        var rgba = Dxt.Decode(block, 4, 4, Dxt.Kind.Dxt1);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(255, rgba[i * 4]);
            Assert.Equal(0, rgba[i * 4 + 1]);
            Assert.Equal(0, rgba[i * 4 + 2]);
            Assert.Equal(255, rgba[i * 4 + 3]);
        }
    }

    [Fact]
    public void Dxt5_AlphaInterpolation()
    {
        // alpha0=255, alpha1=0, índices 1 → alpha 0; color blanco.
        var block = new byte[16];
        block[0] = 255; block[1] = 0;
        ulong idx = 0; for (int i = 0; i < 16; i++) idx |= 1UL << (3 * i);
        for (int i = 0; i < 6; i++) block[2 + i] = (byte)(idx >> (8 * i));
        block[8] = 0xFF; block[9] = 0xFF; block[10] = 0xFF; block[11] = 0xFF;
        var rgba = Dxt.Decode(block, 4, 4, Dxt.Kind.Dxt5);
        Assert.Equal(255, rgba[0]);
        Assert.Equal(0, rgba[3]);
    }

    [Fact]
    public void Tex_Rgba_Lz4_IsDecodedAndCropped()
    {
        int w = 8, h = 4;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = (byte)i; px[i * 4 + 3] = 255; }
        var tex = TexFile.Read(MakeTex(0, w, h, px, lz4: true, imageW: 5, imageH: 3));
        Assert.Single(tex.Images);
        Assert.Equal(5, tex.Images[0].Width);
        Assert.Equal(3, tex.Images[0].Height);
        // fila 1, columna 0 de la imagen recortada = píxel 8 del original
        Assert.Equal(8, tex.Images[0].Rgba![5 * 4]);
    }

    [Fact]
    public void Tex_EmbeddedPng_IsPassedThrough()
    {
        var png = PngWriter.Encode(new byte[4 * 4 * 4], 4, 4);
        var tex = TexFile.Read(MakeTex(0, 4, 4, png, fif: 13));
        Assert.Equal(".png", tex.Images[0].EncodedExtension);
        Assert.Equal(png, tex.Images[0].Encoded);
    }

    [Fact]
    public void Png_HasValidSignatureAndChunks()
    {
        var png = PngWriter.Encode(new byte[2 * 2 * 4], 2, 2);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal("IEND", Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    [Fact]
    public void Pkg_RoundTrip()
    {
        var path = Path.Combine(_dir, "scene.pkg");
        MakePkg(path, new() { ["scene.json"] = Utf8("{}"), ["materials/a.tex"] = new byte[] { 1, 2, 3 } });
        var pkg = PkgArchive.Open(path);
        Assert.Equal("PKGV0019", pkg.Magic);
        Assert.Equal(2, pkg.Entries.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, pkg.Read(pkg.Find("materials/a.tex")!));
    }

    [Fact]
    public void Scene_ConvertsLayersTransformsAndUserBindings()
    {
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { px[i * 4 + 1] = 200; px[i * 4 + 3] = 255; }
        var scene = """
        {
          "general": { "orthogonalprojection": { "width": 1920, "height": 1080 }, "clearcolor": "0.1 0.2 0.3" },
          "objects": [
            { "id": 1, "name": "fondo", "image": "models/bg.json", "origin": "960 540 0", "scale": "1 1 1", "angles": "0 0 0", "size": "1920 1080" },
            { "id": 2, "name": "logo", "image": "models/logo.json", "parent": 1, "origin": "100 50 0", "scale": "2 2 1",
              "angles": "0 0 1.5708", "visible": { "user": "showlogo", "value": true }, "alpha": 0.5 },
            { "id": 3, "name": "fx", "image": "models/util/composelayer.json" },
            { "id": 4, "name": "musica", "sound": [ "sounds/loop.mp3" ], "volume": 0.8 }
          ]
        }
        """;
        var files = new Dictionary<string, byte[]>
        {
            ["scene.json"] = Utf8(scene),
            ["models/bg.json"] = Utf8("""{ "material": "materials/bg.json" }"""),
            ["materials/bg.json"] = Utf8("""{ "passes": [ { "textures": [ "bg" ], "blending": "translucent" } ] }"""),
            ["materials/bg.tex"] = MakeTex(0, 4, 4, px),
            ["models/logo.json"] = Utf8("""{ "material": "materials/logo.json", "width": 64, "height": 32 }"""),
            ["materials/logo.json"] = Utf8("""{ "passes": [ { "textures": [ "logo" ], "blending": "additive" } ] }"""),
            ["materials/logo.tex"] = MakeTex(7, 4, 4, new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0 }),
            ["models/util/composelayer.json"] = Utf8("""{ "material": "materials/util/composelayer.json", "passthrough": true }"""),
            ["sounds/loop.mp3"] = new byte[] { 0x49, 0x44, 0x33 },
        };
        MakePkg(Path.Combine(_dir, "scene.pkg"), files);
        var project = JsonNode.Parse("""{ "general": { "properties": { "showlogo": { "type": "bool", "value": false } } } }""")!.AsObject();

        var outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(outDir);
        new SceneConverter(_dir, null, project).Convert(outDir, "scene.json");

        // Para probar el renderizador web a mano o en CI: WF_EXPORT_SCENE=/ruta dotnet test
        if (Environment.GetEnvironmentVariable("WF_EXPORT_SCENE") is { Length: > 0 } export)
        {
            Directory.CreateDirectory(export);
            foreach (var f in Directory.GetFiles(outDir)) File.Copy(f, Path.Combine(export, Path.GetFileName(f)), true);
        }

        var js = File.ReadAllText(Path.Combine(outDir, "scene-data.js"));
        Assert.StartsWith("window.__WF_SCENE = ", js);
        var data = JsonNode.Parse(js["window.__WF_SCENE = ".Length..].TrimEnd().TrimEnd(';'))!;

        Assert.Equal(1920, data["width"]!.GetValue<double>());
        var layers = data["layers"]!.AsArray();
        Assert.Equal(2, layers.Count); // la capa de composición se omite

        var bg = layers[0]!;
        Assert.Equal(1920, bg["w"]!.GetValue<double>());
        Assert.True(File.Exists(Path.Combine(outDir, bg["tex"]!["src"]!.GetValue<string>())));

        var logo = layers[1]!;
        Assert.Equal(64, logo["w"]!.GetValue<double>());
        Assert.Equal("additive", logo["blend"]!.GetValue<string>());
        Assert.Equal("showlogo", logo["visibleUser"]!.GetValue<string>());
        Assert.False(logo["visible"]!.GetValue<bool>()); // tomado de la propiedad del proyecto
        // Hijo de "fondo" (sin rotación ni escala): origen = 960+100, 540+50
        Assert.Equal(1060, logo["x"]!.GetValue<double>(), 3);
        Assert.Equal(590, logo["y"]!.GetValue<double>(), 3);
        Assert.Equal(2, logo["sx"]!.GetValue<double>());
        Assert.Equal(0.5, logo["alpha"]!.GetValue<double>());

        var sounds = data["sounds"]!.AsArray();
        Assert.Single(sounds);
        Assert.Equal(0.8, sounds[0]!["volume"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void Project_ParsesWallpaperEngineProjectJson()
    {
        File.WriteAllText(Path.Combine(_dir, "project.json"), """
        {
          "title": "Prueba", "type": "Web", "file": "index.html", "tags": ["Reloj"],
          "general": { "properties": {
            "schemecolor": { "order": 0, "text": "ui_browse_properties_scheme_color", "type": "color", "value": "1 0 0" },
            "speed": { "order": 2, "text": "Velocidad", "type": "slider", "min": 0, "max": 10, "value": 5, "fraction": true, "precision": 1 },
            "mode": { "order": 1, "text": "Modo", "type": "combo", "value": "b", "options": [ { "label": "A", "value": "a" }, { "label": "B", "value": "b" } ] }
          } }
        }
        """);
        File.WriteAllText(Path.Combine(_dir, "index.html"), "<html></html>");
        var p = WallpaperProject.Load(_dir, WallpaperSource.SteamWorkshop)!;
        Assert.Equal(WallpaperType.Web, p.Type);
        Assert.Equal("Prueba", p.Title);
        Assert.Equal(new[] { "schemecolor", "mode", "speed" }, p.Properties.Select(x => x.Key));
        Assert.Equal("Color del esquema", p.Properties[0].Label);
        Assert.True(p.Properties[2].Fraction);
        Assert.Equal(2, p.Properties[1].Options.Count);
    }

    [Fact]
    public void Project_InfersLooseVideo()
    {
        // Una carpeta sin project.json no es un wallpaper; cada video suelto sí.
        var clip = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(clip, new byte[] { 0 });
        Assert.Null(WallpaperProject.Load(_dir, WallpaperSource.Local));
        var p = WallpaperProject.Load(clip, WallpaperSource.Local)!;
        Assert.Equal(WallpaperType.Video, p.Type);
        Assert.Equal("clip.mp4", p.EntryFile);
    }
}
