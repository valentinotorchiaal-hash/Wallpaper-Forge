using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WallpaperForge.Wallpapers;

public enum WallpaperType { Video, Web, Scene, Image, Application, Unknown }

public enum WallpaperSource { Local, SteamWorkshop, WallpaperEngineProjects }

/// <summary>Propiedad configurable declarada en project.json → general.properties.</summary>
public sealed class WallpaperProperty
{
    public required string Key { get; init; }
    public required string Type { get; init; }       // slider, bool, color, combo, textinput, file, directory, text, group
    public string Label { get; init; } = "";
    public int Order { get; init; }
    public JsonNode? DefaultValue { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public bool Fraction { get; init; }
    public int? Precision { get; init; }
    public string? Condition { get; init; }
    public string? FileType { get; init; }
    public string? Mode { get; init; }               // directory: "fetchall" | "ondemand"
    public List<(string Label, JsonNode? Value)> Options { get; init; } = new();
}

/// <summary>Un wallpaper en disco (una carpeta con project.json, o un video/HTML suelto importado).</summary>
public sealed class WallpaperProject
{
    public required string Id { get; init; }
    public required string Folder { get; init; }
    /// <summary>
    /// Clave única del wallpaper: la carpeta (proyectos con project.json o index.html) o la ruta del archivo
    /// (videos, imágenes y GIF sueltos). Es lo que se guarda en la configuración.
    /// </summary>
    public required string Location { get; init; }
    public required WallpaperType Type { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public string? EntryFile { get; init; }          // index.html / video.mp4 / scene.json
    public string? PreviewFile { get; init; }
    public List<string> Tags { get; init; } = new();
    public string ContentRating { get; init; } = "";
    public string? WorkshopId { get; init; }
    public WallpaperSource Source { get; init; }
    public List<WallpaperProperty> Properties { get; init; } = new();
    public JsonObject? RawProject { get; init; }
    public DateTime Modified { get; init; }
    public long SizeBytes { get; init; }

    public string? EntryPath => EntryFile is null ? null : Path.Combine(Folder, EntryFile);
    public string? PreviewPath => PreviewFile is null ? null : Path.Combine(Folder, PreviewFile);

    public bool IsLooseFile => !string.Equals(Location, Folder, StringComparison.OrdinalIgnoreCase);

    public static string MakeId(string location)
    {
        var norm = Path.GetFullPath(location).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(norm));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static WallpaperType ParseType(string? t) => t?.Trim().ToLowerInvariant() switch
    {
        "video" => WallpaperType.Video,
        "web" => WallpaperType.Web,
        "scene" => WallpaperType.Scene,
        "application" => WallpaperType.Application,
        "image" => WallpaperType.Image,
        _ => WallpaperType.Unknown,
    };

    private static readonly string[] VideoExt = { ".mp4", ".webm", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".ogv" };
    public static bool IsVideoFile(string path) => VideoExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    // .jfif/.jpe/.pjpeg son JPEG con otra extensión (Pinterest y Chrome suelen guardar así).
    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".jfif", ".jpe", ".pjpeg", ".pjp", ".png", ".apng", ".gif", ".webp", ".bmp", ".avif" };
    public static bool IsImageFile(string path) => ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static readonly HashSet<string> PreviewNames = new(StringComparer.OrdinalIgnoreCase)
        { "preview.gif", "preview.jpg", "preview.png", "preview.webp", "preview.jpeg" };

    /// <summary>true si el archivo puede ser un wallpaper por sí solo (video, imagen o GIF que no sea una vista previa).</summary>
    public static bool IsStandaloneMedia(string path)
        => (IsVideoFile(path) || IsImageFile(path)) && !PreviewNames.Contains(Path.GetFileName(path));

    /// <summary>Lee un wallpaper desde una carpeta o desde un archivo suelto. Null si no es reconocible.</summary>
    public static WallpaperProject? Load(string location, WallpaperSource source)
    {
        if (File.Exists(location)) return FromLooseFile(location, source);
        var folder = location;
        var projectFile = Path.Combine(folder, "project.json");
        if (File.Exists(projectFile))
        {
            try { return FromProjectJson(folder, projectFile, source); }
            catch (Exception ex)
            {
                Core.Log.Warn($"project.json inválido en {folder}: {ex.Message}");
                return null;
            }
        }
        return Infer(folder, source);
    }

    private static WallpaperProject FromProjectJson(string folder, string projectFile, WallpaperSource source)
    {
        var json = JsonNode.Parse(File.ReadAllText(projectFile), documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        })?.AsObject() ?? throw new InvalidDataException("project.json vacío");

        string? file = Str(json["file"]);
        var type = ParseType(Str(json["type"]));
        if (type == WallpaperType.Unknown && file != null)
        {
            // Algunos proyectos viejos no tienen "type": se deduce por la extensión.
            var ext = Path.GetExtension(file).ToLowerInvariant();
            type = ext switch
            {
                ".html" or ".htm" => WallpaperType.Web,
                ".json" => WallpaperType.Scene,
                _ when IsImageFile(file) => WallpaperType.Image,
                ".exe" => WallpaperType.Application,
                _ when IsVideoFile(file) => WallpaperType.Video,
                _ => WallpaperType.Unknown,
            };
        }

        var preview = Str(json["preview"]);
        if (preview != null && !File.Exists(Path.Combine(folder, preview))) preview = null;
        preview ??= FindPreview(folder);

        var tags = new List<string>();
        if (json["tags"] is JsonArray ta)
            tags.AddRange(ta.Select(t => Str(t)).Where(t => !string.IsNullOrWhiteSpace(t))!);

        var props = new List<WallpaperProperty>();
        if (json["general"]?["properties"] is JsonObject po)
        {
            foreach (var (key, node) in po)
            {
                if (node is not JsonObject p) continue;
                props.Add(ParseProperty(key, p));
            }
        }
        props.Sort((a, b) => a.Order.CompareTo(b.Order));

        var info = new DirectoryInfo(folder);
        return new WallpaperProject
        {
            Id = MakeId(folder),
            Location = folder,
            Folder = folder,
            Type = type,
            Title = Str(json["title"]) is { Length: > 0 } t ? t : info.Name,
            Description = Str(json["description"]) ?? "",
            EntryFile = file,
            PreviewFile = preview,
            Tags = tags,
            ContentRating = Str(json["contentrating"]) ?? "",
            WorkshopId = Str(json["workshopid"]),
            Source = source,
            Properties = props,
            RawProject = json,
            Modified = info.LastWriteTime,
            SizeBytes = SafeSize(folder),
        };
    }

    private static WallpaperProperty ParseProperty(string key, JsonObject p)
    {
        var options = new List<(string, JsonNode?)>();
        if (p["options"] is JsonArray oa)
        {
            foreach (var o in oa.OfType<JsonObject>())
                options.Add((Str(o["label"]) ?? Str(o["value"]) ?? "", o["value"]?.DeepClone()));
        }
        return new WallpaperProperty
        {
            Key = key,
            Type = (Str(p["type"]) ?? "text").ToLowerInvariant(),
            Label = PropertyLabels.Translate(Str(p["text"]) ?? key),
            Order = (int)(Num(p["order"]) ?? 0),
            DefaultValue = p["value"]?.DeepClone(),
            Min = Num(p["min"]),
            Max = Num(p["max"]),
            Step = Num(p["step"]),
            Fraction = p["fraction"] is JsonValue fv && fv.TryGetValue(out bool fb) && fb,
            Precision = Num(p["precision"]) is double pr ? (int)pr : null,
            Condition = Str(p["condition"]),
            FileType = Str(p["fileType"]) ?? Str(p["filetype"]),
            Mode = Str(p["mode"]),
            Options = options,
        };
    }

    /// <summary>Carpeta sin project.json: es un wallpaper web si tiene index.html.</summary>
    private static WallpaperProject? Infer(string folder, WallpaperSource source)
    {
        if (!File.Exists(Path.Combine(folder, "index.html"))) return null;
        var info = new DirectoryInfo(folder);
        return new WallpaperProject
        {
            Id = MakeId(folder),
            Location = folder,
            Folder = folder,
            Type = WallpaperType.Web,
            Title = info.Name,
            EntryFile = "index.html",
            PreviewFile = FindPreview(folder),
            Source = source,
            Modified = info.LastWriteTime,
            SizeBytes = SafeSize(folder),
        };
    }

    /// <summary>Un video, imagen o GIF suelto: cada archivo es un wallpaper.</summary>
    private static WallpaperProject? FromLooseFile(string file, WallpaperSource source)
    {
        if (!IsVideoFile(file) && !IsImageFile(file)) return null;
        var info = new FileInfo(file);
        bool video = IsVideoFile(file);
        return new WallpaperProject
        {
            Id = MakeId(file),
            Location = file,
            Folder = info.DirectoryName!,
            Type = video ? WallpaperType.Video : WallpaperType.Image,
            Title = Path.GetFileNameWithoutExtension(file),
            EntryFile = info.Name,
            PreviewFile = video ? null : info.Name, // las imágenes y GIF son su propia vista previa
            Tags = new List<string> { info.Directory?.Name ?? "" }.Where(t => t.Length > 0).ToList(),
            Source = source,
            Modified = info.LastWriteTime,
            SizeBytes = info.Length,
        };
    }

    private static string? FindPreview(string folder)
    {
        foreach (var name in new[] { "preview.gif", "preview.jpg", "preview.png", "preview.webp", "preview.jpeg" })
            if (File.Exists(Path.Combine(folder, name))) return name;
        return null;
    }

    private static long SafeSize(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    internal static string? Str(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out string? s)) return s;
            if (v.TryGetValue(out double d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.TryGetValue(out bool b)) return b ? "true" : "false";
        }
        return null;
    }

    internal static double? Num(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out double d)) return d;
            if (v.TryGetValue(out string? s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        }
        return null;
    }
}

/// <summary>Traduce las claves de localización estándar de Wallpaper Engine a textos legibles.</summary>
public static class PropertyLabels
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ui_browse_properties_scheme_color"] = "Color del esquema",
        ["ui_browse_properties_alignment"] = "Alineación",
        ["ui_browse_properties_scaling"] = "Escalado",
        ["ui_browse_properties_play_sound"] = "Reproducir sonido",
        ["ui_browse_properties_volume"] = "Volumen",
        ["ui_browse_properties_rate"] = "Velocidad",
        ["ui_browse_properties_fps"] = "FPS",
        ["ui_browse_properties_background_color"] = "Color de fondo",
        ["ui_browse_properties_image"] = "Imagen",
        ["ui_browse_properties_video"] = "Video",
        ["ui_browse_properties_text"] = "Texto",
        ["ui_browse_properties_clock"] = "Reloj",
        ["ui_browse_properties_audio_responsive"] = "Reacciona al audio",
    };

    public static string Translate(string raw)
    {
        if (Known.TryGetValue(raw.Trim(), out var t)) return t;
        if (raw.StartsWith("ui_", StringComparison.OrdinalIgnoreCase))
        {
            // ui_browse_properties_mi_propiedad → "Mi propiedad"
            var last = raw.Replace("ui_browse_properties_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');
            return last.Length > 0 ? char.ToUpper(last[0]) + last[1..] : raw;
        }
        return raw;
    }
}
