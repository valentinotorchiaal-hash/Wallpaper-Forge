using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WallpaperForge.Core;

namespace WallpaperForge.Scene;

/// <summary>
/// Convierte una escena de Wallpaper Engine (scene.pkg / scene.json) en capas PNG + un archivo de datos
/// que dibuja el runtime web (wwwroot/runtime/scene.html).
///
/// Soportado (versión 1, "mejor esfuerzo"): capas de imagen con posición, escala, rotación, opacidad,
/// visibilidad (incluida la ligada a propiedades del usuario), jerarquía de padres, modos de mezcla básicos,
/// texturas animadas (sprite sheets) y objetos de sonido en loop.
/// No soportado todavía: efectos/shaders, partículas, texto, puppet warp, parallax, scripts SceneScript.
/// </summary>
public sealed class SceneConverter
{
    public const int CacheVersion = 3;

    public sealed class Result
    {
        public required string DataScriptPath { get; init; }
        public required List<string> Warnings { get; init; }
    }

    private readonly string _folder;
    private readonly string? _assetsDir;
    private readonly PkgArchive? _pkg;
    private readonly List<string> _warnings = new();
    private readonly JsonObject _projectProperties;

    public SceneConverter(string folder, string? wallpaperEngineInstall, JsonObject? project)
    {
        _folder = folder;
        var assets = wallpaperEngineInstall is null ? null : Path.Combine(wallpaperEngineInstall, "assets");
        _assetsDir = assets != null && Directory.Exists(assets) ? assets : null;
        var pkgPath = Path.Combine(folder, "scene.pkg");
        if (File.Exists(pkgPath)) _pkg = PkgArchive.Open(pkgPath);
        else
        {
            var anyPkg = Directory.EnumerateFiles(folder, "*.pkg").FirstOrDefault();
            if (anyPkg != null) _pkg = PkgArchive.Open(anyPkg);
        }
        _projectProperties = project?["general"]?["properties"] as JsonObject ?? new JsonObject();
    }

    public static string CacheFolderFor(string folder)
    {
        var stamp = new StringBuilder(Path.GetFullPath(folder).ToLowerInvariant());
        foreach (var f in new[] { "scene.pkg", "scene.json", "project.json" })
        {
            var p = Path.Combine(folder, f);
            if (File.Exists(p)) stamp.Append('|').Append(new FileInfo(p).Length).Append('|').Append(File.GetLastWriteTimeUtc(p).Ticks);
        }
        stamp.Append("|v").Append(CacheVersion);
        var hash = System.Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(stamp.ToString())), 0, 10).ToLowerInvariant();
        return Path.Combine(AppPaths.SceneCache, hash);
    }

    /// <summary>Convierte (o reutiliza la caché) y devuelve la ruta del scene-data.js.</summary>
    public static Result ConvertCached(string folder, string? weInstall, JsonObject? project, string sceneFile = "scene.json")
    {
        var cache = CacheFolderFor(folder);
        var dataPath = Path.Combine(cache, "scene-data.js");
        if (File.Exists(dataPath))
            return new Result { DataScriptPath = dataPath, Warnings = new() };

        var conv = new SceneConverter(folder, weInstall, project);
        var tmp = cache + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        conv.Convert(tmp, sceneFile);
        if (Directory.Exists(cache)) Directory.Delete(cache, true);
        Directory.Move(tmp, cache);
        foreach (var w in conv._warnings) Log.Warn($"Escena {folder}: {w}");
        return new Result { DataScriptPath = dataPath, Warnings = conv._warnings };
    }

    public void Convert(string outDir, string sceneFile)
    {
        var sceneBytes = ReadFile(sceneFile) ?? throw new FileNotFoundException("No se encontró " + sceneFile);
        var scene = ParseJson(sceneBytes)?.AsObject() ?? throw new InvalidDataException("scene.json inválido");

        var general = scene["general"] as JsonObject ?? new JsonObject();
        var ortho = general["orthogonalprojection"] as JsonObject;
        double width = Num(ortho?["width"]) ?? 0, height = Num(ortho?["height"]) ?? 0;
        bool autoSize = ortho?["auto"] is JsonValue av && av.TryGetValue(out bool ab) && ab;

        var objects = (scene["objects"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var byId = new Dictionary<int, JsonObject>();
        foreach (var o in objects) if (Num(o["id"]) is double id) byId[(int)id] = o;

        var layers = new JsonArray();
        var sounds = new JsonArray();
        int texCounter = 0;
        var texCache = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);

        foreach (var obj in objects)
        {
            try
            {
                if (obj["sound"] is JsonArray snd)
                {
                    foreach (var s in snd.Select(Str).Where(s => s != null))
                    {
                        var soundBytes = ReadFile(s!);
                        if (soundBytes == null) { _warnings.Add($"sonido no encontrado: {s}"); continue; }
                        var name = $"snd{sounds.Count}{Path.GetExtension(s)}";
                        File.WriteAllBytes(Path.Combine(outDir, name), soundBytes);
                        sounds.Add(new JsonObject
                        {
                            ["src"] = name,
                            ["volume"] = Num(Resolve(obj["volume"])) ?? 1.0,
                            ["visibleUser"] = UserKey(obj["visible"]),
                        });
                    }
                    continue;
                }

                var imagePath = Str(obj["image"]);
                if (imagePath == null) continue; // partículas, texto, cámaras, etc.

                var model = ReadJson(imagePath);
                if (model == null) { _warnings.Add($"modelo no encontrado: {imagePath}"); continue; }
                bool fullscreen = model["fullscreen"] is JsonValue fv && fv.TryGetValue(out bool fb) && fb;
                bool passthrough = model["passthrough"] is JsonValue pv && pv.TryGetValue(out bool pb) && pb;
                if (passthrough) continue; // capas de composición/efectos

                var materialPath = Str(model["material"]);
                var material = materialPath != null ? ReadJson(materialPath) : null;
                var pass = (material?["passes"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                var texName = (pass?["textures"] as JsonArray)?.Select(Str).FirstOrDefault();
                // "instance.textures" de la escena reemplaza la textura del material.
                if (obj["instance"]?["textures"] is JsonArray inst && inst.Select(Str).FirstOrDefault() is string it) texName = it;
                if (texName == null || texName.StartsWith("_rt_", StringComparison.Ordinal))
                {
                    if (!fullscreen) _warnings.Add($"capa sin textura (efecto): {Str(obj["name"])}");
                    continue;
                }

                if (!texCache.TryGetValue(texName, out var texInfo))
                {
                    texInfo = ExportTexture(texName, outDir, ref texCounter);
                    texCache[texName] = texInfo;
                }
                if (texInfo == null) continue;

                double tw = Num(texInfo["w"]) ?? 0, th = Num(texInfo["h"]) ?? 0;
                var size = Vec(Resolve(obj["size"]), 2);
                double w = size[0] > 0 ? size[0] : Num(model["width"]) ?? tw;
                double h = size[1] > 0 ? size[1] : Num(model["height"]) ?? th;

                if (autoSize && width <= 0 && w > 0) { width = w; height = h; }

                var (origin, scale, angle) = ResolveTransform(obj, byId);

                var layer = new JsonObject
                {
                    ["name"] = Str(obj["name"]) ?? "",
                    ["tex"] = texInfo.DeepClone(),
                    ["fullscreen"] = fullscreen,
                    ["w"] = w,
                    ["h"] = h,
                    ["x"] = origin[0],
                    ["y"] = origin[1],       // coordenadas de la escena: Y hacia arriba
                    ["sx"] = scale[0],
                    ["sy"] = scale[1],
                    ["angle"] = angle,      // radianes, antihorario
                    ["alpha"] = Num(Resolve(obj["alpha"])) ?? 1.0,
                    ["color"] = new JsonArray(Vec(Resolve(obj["color"]), 3, 1.0).Select(v => (JsonNode)v).ToArray()),
                    ["blend"] = (Str(pass?["blending"]) ?? "translucent").ToLowerInvariant(),
                    ["visible"] = Bool(Resolve(obj["visible"]), true),
                    ["visibleUser"] = UserKey(obj["visible"]),
                    ["alphaUser"] = UserKey(obj["alpha"]),
                };
                if (obj["effects"] is JsonArray fx && fx.Count > 0) layer["effects"] = fx.Count;
                layers.Add(layer);
            }
            catch (Exception ex)
            {
                _warnings.Add($"objeto '{Str(obj["name"])}': {ex.Message}");
            }
        }

        if (width <= 0 || height <= 0) { width = 1920; height = 1080; }

        var data = new JsonObject
        {
            ["version"] = CacheVersion,
            ["width"] = width,
            ["height"] = height,
            ["clearColor"] = new JsonArray(Vec(Resolve(general["clearcolor"]), 3, 0.0).Select(v => (JsonNode)v).ToArray()),
            ["layers"] = layers,
            ["sounds"] = sounds,
            ["warnings"] = new JsonArray(_warnings.Select(w => (JsonNode)w).ToArray()),
        };
        File.WriteAllText(Path.Combine(outDir, "scene-data.js"),
            "window.__WF_SCENE = " + data.ToJsonString() + ";\n", Encoding.UTF8);
    }

    /// <summary>Extrae materials/{name}.tex a PNG (o al formato embebido). Devuelve info para el runtime.</summary>
    private JsonObject? ExportTexture(string name, string outDir, ref int counter)
    {
        var path = name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ? name : $"materials/{name}.tex";
        var bytes = ReadFile(path);
        if (bytes == null)
        {
            _warnings.Add($"textura no encontrada: {path}" + (_assetsDir == null ? " (¿falta la carpeta assets de Wallpaper Engine?)" : ""));
            return null;
        }
        TexFile tex;
        try { tex = TexFile.Read(bytes); }
        catch (Exception ex) { _warnings.Add($"textura {name}: {ex.Message}"); return null; }
        if (tex.Images.Count == 0) return null;

        var sheets = new JsonArray();
        foreach (var img in tex.Images)
        {
            string file;
            if (img.Encoded != null)
            {
                file = $"t{counter++}{img.EncodedExtension}";
                File.WriteAllBytes(Path.Combine(outDir, file), img.Encoded);
            }
            else
            {
                file = $"t{counter++}.png";
                File.WriteAllBytes(Path.Combine(outDir, file), PngWriter.Encode(img.Rgba!, img.Width, img.Height));
            }
            sheets.Add(file);
        }

        var info = new JsonObject
        {
            ["src"] = sheets[0]!.GetValue<string>(),
            ["w"] = tex.ImageWidth > 0 ? tex.ImageWidth : tex.Images[0].Width,
            ["h"] = tex.ImageHeight > 0 ? tex.ImageHeight : tex.Images[0].Height,
            ["video"] = tex.IsVideo,
        };
        if (tex.IsAnimated)
        {
            info["sheets"] = sheets;
            info["w"] = tex.GifWidth;
            info["h"] = tex.GifHeight;
            info["frames"] = new JsonArray(tex.Frames.Select(f => (JsonNode)new JsonObject
            {
                ["i"] = f.ImageId, ["t"] = f.FrameTime,
                ["x"] = f.X, ["y"] = f.Y, ["w"] = Math.Abs(f.Width), ["h"] = Math.Abs(f.Height),
            }).ToArray());
        }
        return info;
    }

    private (double[] origin, double[] scale, double angle) ResolveTransform(JsonObject obj, Dictionary<int, JsonObject> byId)
    {
        var chain = new List<JsonObject> { obj };
        var cur = obj;
        for (int depth = 0; depth < 32 && Num(cur["parent"]) is double pid && byId.TryGetValue((int)pid, out var parent); depth++)
        {
            chain.Add(parent);
            cur = parent;
        }

        double[] origin = Vec(Resolve(chain[^1]["origin"]), 3);
        double[] scale = Vec(Resolve(chain[^1]["scale"]), 3, 1.0);
        double angle = Vec(Resolve(chain[^1]["angles"]), 3)[2];
        for (int i = chain.Count - 2; i >= 0; i--)
        {
            var lo = Vec(Resolve(chain[i]["origin"]), 3);
            var ls = Vec(Resolve(chain[i]["scale"]), 3, 1.0);
            var la = Vec(Resolve(chain[i]["angles"]), 3)[2];
            double ox = lo[0] * scale[0], oy = lo[1] * scale[1];
            double c = Math.Cos(angle), s = Math.Sin(angle);
            origin = new[] { origin[0] + ox * c - oy * s, origin[1] + ox * s + oy * c, origin[2] + lo[2] * scale[2] };
            scale = new[] { ls[0] * scale[0], ls[1] * scale[1], ls[2] * scale[2] };
            angle += la;
        }
        return (origin, scale, angle);
    }

    // ---------- Lectura de archivos: .pkg → carpeta → assets de Wallpaper Engine ----------

    private byte[]? ReadFile(string relative)
    {
        relative = relative.Replace('\\', '/').TrimStart('/');
        if (_pkg?.Find(relative) is { } e) return _pkg.Read(e);
        var loose = Path.Combine(_folder, relative);
        if (File.Exists(loose)) return File.ReadAllBytes(loose);
        if (_assetsDir != null)
        {
            var asset = Path.Combine(_assetsDir, relative);
            if (File.Exists(asset)) return File.ReadAllBytes(asset);
        }
        return null;
    }

    private JsonObject? ReadJson(string relative)
    {
        var b = ReadFile(relative);
        return b == null ? null : ParseJson(b) as JsonObject;
    }

    private static JsonNode? ParseJson(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
    }

    // ---------- Valores: pueden ser literales, "x y z", o {"user": "prop", "value": ...} ----------

    private JsonNode? Resolve(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            if (o["user"] is JsonNode userNode)
            {
                var key = Str(userNode) ?? Str(userNode["name"]);
                if (key != null && _projectProperties[key]?["value"] is JsonNode pv) return pv;
            }
            return o["value"];
        }
        return n;
    }

    private static string? UserKey(JsonNode? n)
    {
        if (n is JsonObject o && o["user"] is JsonNode u) return Str(u) ?? Str(u["name"]);
        return null;
    }

    private static double[] Vec(JsonNode? n, int count, double fallback = 0)
    {
        var r = Enumerable.Repeat(fallback, count).ToArray();
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out string? s) && s != null)
            {
                var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < Math.Min(count, parts.Length); i++)
                    if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) r[i] = d;
                // Escala/valor único → se aplica a todos los ejes.
                if (parts.Length == 1) for (int i = 1; i < count; i++) r[i] = r[0];
            }
            else if (v.TryGetValue(out double d))
            {
                for (int i = 0; i < count; i++) r[i] = d;
            }
        }
        else if (n is JsonArray a)
        {
            for (int i = 0; i < Math.Min(count, a.Count); i++) r[i] = Num(a[i]) ?? fallback;
        }
        return r;
    }

    private static bool Bool(JsonNode? n, bool fallback)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out bool b)) return b;
            if (v.TryGetValue(out double d)) return d != 0;
            if (v.TryGetValue(out string? s)) return s is "true" or "1";
        }
        return fallback;
    }

    private static double? Num(JsonNode? n) => Wallpapers.WallpaperProject.Num(n);
    private static string? Str(JsonNode? n) => Wallpapers.WallpaperProject.Str(n);
}
