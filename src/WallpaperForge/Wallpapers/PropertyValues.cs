using System.Text.Json.Nodes;
using WallpaperForge.Core;

namespace WallpaperForge.Wallpapers;

/// <summary>Combina los valores por defecto del project.json con los que cambió el usuario.</summary>
public static class PropertyValues
{
    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg" };
    private static readonly string[] MediaExt = ImageExt.Concat(new[] { ".mp4", ".webm", ".mp3", ".ogg", ".wav", ".m4a" }).ToArray();

    public static JsonNode? Effective(WallpaperProject p, WallpaperProperty prop, AppSettings s)
    {
        if (s.PropertyValues.TryGetValue(p.Location, out var map) && map.TryGetValue(prop.Key, out var v))
            return v?.DeepClone();
        return prop.DefaultValue?.DeepClone();
    }

    /// <summary>Objeto con el formato que espera wallpaperPropertyListener.applyUserProperties.</summary>
    public static JsonObject BuildUserProperties(WallpaperProject p, AppSettings s, IEnumerable<string>? onlyKeys = null)
    {
        var filter = onlyKeys?.ToHashSet(StringComparer.Ordinal);
        var result = new JsonObject();
        foreach (var prop in p.Properties)
        {
            if (filter != null && !filter.Contains(prop.Key)) continue;
            if (prop.Type is "group" or "text" && prop.DefaultValue is null) continue;
            var value = Effective(p, prop, s);
            if (prop.Type is "file" or "directory" && value is JsonValue jv && jv.TryGetValue(out string? path) && !string.IsNullOrEmpty(path))
                value = path.Replace('\\', '/');
            result[prop.Key] = new JsonObject { ["type"] = prop.Type, ["value"] = value };
        }
        return result;
    }

    /// <summary>Archivos de cada propiedad "directory" (para userDirectoryFilesAddedOrChanged y archivos aleatorios).</summary>
    public static JsonObject BuildDirectories(WallpaperProject p, AppSettings s)
    {
        var result = new JsonObject();
        foreach (var prop in p.Properties.Where(x => x.Type == "directory"))
        {
            if (Effective(p, prop, s) is not JsonValue v || !v.TryGetValue(out string? dir) || !Directory.Exists(dir)) continue;
            var ext = prop.FileType == "video" ? MediaExt : ImageExt;
            var files = new JsonArray();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir).Where(f => ext.Contains(Path.GetExtension(f).ToLowerInvariant())).Take(5000))
                    files.Add(f.Replace('\\', '/'));
            }
            catch (Exception ex) { Log.Warn($"No se pudo listar {dir}: {ex.Message}"); }
            result[prop.Key] = new JsonObject { ["mode"] = prop.Mode ?? "fetchall", ["files"] = files };
        }
        return result;
    }

    public static void Set(AppSettings s, WallpaperProject p, string key, JsonNode? value)
    {
        if (!s.PropertyValues.TryGetValue(p.Location, out var map))
            s.PropertyValues[p.Location] = map = new Dictionary<string, JsonNode?>();
        map[key] = value?.DeepClone();
    }

    public static void Reset(AppSettings s, WallpaperProject p) => s.PropertyValues.Remove(p.Location);

    public static PlaybackOptions Playback(AppSettings s, WallpaperProject p)
    {
        if (!s.Playback.TryGetValue(p.Location, out var o)) s.Playback[p.Location] = o = new PlaybackOptions();
        return o;
    }
}
