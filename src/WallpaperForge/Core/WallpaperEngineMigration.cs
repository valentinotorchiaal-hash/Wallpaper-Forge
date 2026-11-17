using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WallpaperForge.Core;

/// <summary>
/// Lee qué wallpaper tenía elegido Wallpaper Engine en cada monitor (wallpaper_engine\config.json),
/// para que al pasarse a Wallpaper Forge el escritorio quede igual. Es "mejor esfuerzo": el formato de
/// config.json no está documentado, así que se buscan de forma tolerante los objetos "selectedwallpapers".
/// Lógica sin dependencias de Windows (se testea en cualquier SO).
/// </summary>
public static partial class WallpaperEngineMigration
{
    public sealed record Selection(string MonitorKey, string WallpaperFolder, int? DisplayNumber);

    public static List<Selection> ParseConfig(string json)
    {
        var result = new List<Selection>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch { return result; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selected in FindObjects(root, "selectedwallpapers"))
        {
            foreach (var (monitorKey, value) in selected)
            {
                var fileNode = value is JsonObject vo ? vo["file"] : value;
                string? file = fileNode is JsonValue fv && fv.TryGetValue(out string? f) ? f : null;
                if (string.IsNullOrWhiteSpace(file)) continue;
                file = file.Replace('/', '\\').TrimEnd('\\');
                // Separación manual (no Path.*) para que se comporte igual en cualquier SO.
                var last = file.LastIndexOf('\\');
                var name = last >= 0 ? file[(last + 1)..] : file;
                var folder = name.Contains('.') && last > 0 ? file[..last] : file;
                if (string.IsNullOrEmpty(folder) || !seen.Add(monitorKey)) continue;
                result.Add(new Selection(monitorKey, folder, DisplayNumberOf(monitorKey)));
            }
        }
        return result;
    }

    /// <summary>"\\.\DISPLAY2", "\\?\DISPLAY#...#DISPLAY2"... → 2. Null si la clave no lo indica.</summary>
    public static int? DisplayNumberOf(string key)
    {
        var m = DisplayRegex().Match(key);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    /// <summary>Asigna selecciones a monitores: por número de DISPLAY si se puede, si no por orden.</summary>
    public static Dictionary<string, string> MapToMonitors(IReadOnlyList<Selection> selections, IReadOnlyList<string> monitorIds)
    {
        var map = new Dictionary<string, string>();
        var pending = new List<Selection>();
        foreach (var s in selections)
        {
            var id = s.DisplayNumber is int n ? monitorIds.FirstOrDefault(m => DisplayNumberOf(m) == n) : null;
            if (id != null && !map.ContainsKey(id)) map[id] = s.WallpaperFolder;
            else pending.Add(s);
        }
        foreach (var s in pending)
        {
            var free = monitorIds.FirstOrDefault(m => !map.ContainsKey(m));
            if (free == null) break;
            map[free] = s.WallpaperFolder;
        }
        return map;
    }

    private static IEnumerable<JsonObject> FindObjects(JsonNode? node, string name)
    {
        if (node is JsonObject o)
        {
            foreach (var (k, v) in o)
            {
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase) && v is JsonObject match) yield return match;
                foreach (var inner in FindObjects(v, name)) yield return inner;
            }
        }
        else if (node is JsonArray a)
        {
            foreach (var v in a)
                foreach (var inner in FindObjects(v, name)) yield return inner;
        }
    }

    [GeneratedRegex(@"DISPLAY(\d+)(?!.*DISPLAY\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayRegex();
}
