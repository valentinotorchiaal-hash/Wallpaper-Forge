using System.Text.Json;
using System.Text.Json.Nodes;
using WallpaperForge.Core;
using WallpaperForge.Wallpapers;

namespace WallpaperForge.UI;

/// <summary>
/// RPC entre la interfaz HTML y la app.
/// UI → app:   { id, method, params }
/// app → UI:   { id, result } | { id, error }   y eventos { event, data }
/// </summary>
public sealed class Bridge
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly WallpaperManager _m;
    private readonly OtherAppsService _otherApps;
    private readonly MainWindow _window;

    public Bridge(WallpaperManager manager, OtherAppsService otherApps, MainWindow window)
    {
        _m = manager;
        _otherApps = otherApps;
        _window = window;
        _m.StateChanged += () => PostEvent("state", BuildState());
        _m.LoadWarnings += (title, w) => PostEvent("warnings", new { title, warnings = w });
    }

    public bool CanServe(string path) => _m.Library.IsInsideLibrary(path);

    public void PostEvent(string name, object? data)
    {
        var msg = new JsonObject { ["event"] = name, ["data"] = JsonSerializer.SerializeToNode(data, Json) };
        _window.PostJson(msg.ToJsonString());
    }

    public async Task HandleAsync(string raw)
    {
        JsonNode? id = null;
        try
        {
            var msg = JsonNode.Parse(raw)!.AsObject();
            id = msg["id"]?.DeepClone();
            var method = msg["method"]?.GetValue<string>() ?? "";
            var p = msg["params"] as JsonObject ?? new JsonObject();
            var result = await DispatchAsync(method, p);
            Reply(id, JsonSerializer.SerializeToNode(result, Json), null);
        }
        catch (Exception ex)
        {
            Log.Error("Error en llamada de la UI", ex);
            Reply(id, null, ex.Message);
        }
    }

    private void Reply(JsonNode? id, JsonNode? result, string? error)
    {
        var msg = new JsonObject { ["id"] = id };
        if (error != null) msg["error"] = error; else msg["result"] = result;
        _window.PostJson(msg.ToJsonString());
    }

    private async Task<object?> DispatchAsync(string method, JsonObject p)
    {
        switch (method)
        {
            case "getState":
                return BuildState();

            case "getLibrary":
                await _m.LibraryReady;
                if (p["rescan"]?.GetValue<bool>() == true) await Task.Run(() => _m.Library.Scan(_m.Settings));
                return _m.Library.Items.OrderByDescending(x => x.Modified).Select(Summary).ToList();

            case "getWallpaper":
                return Details(Require(p));

            case "apply":
            {
                var project = Require(p);
                var monitor = p["monitor"]?.GetValue<string>();
                if (string.IsNullOrEmpty(monitor) || monitor == "all") await _m.ApplyToAllAsync(project);
                else await _m.ApplyAsync(project, monitor);
                return BuildState();
            }

            case "clear":
                _m.Clear(p["monitor"]!.GetValue<string>());
                return BuildState();

            case "setProperty":
            {
                var project = Require(p);
                var key = p["key"]!.GetValue<string>();
                await _m.SetPropertyAsync(project, key, p["value"]?.DeepClone());
                // Una carpeta nueva requiere volver a listar archivos: se recarga el wallpaper.
                if (project.Properties.FirstOrDefault(x => x.Key == key)?.Type == "directory")
                    await _m.ReloadAsync(project);
                return true;
            }

            case "resetProperties":
            {
                var project = Require(p);
                await _m.ResetPropertiesAsync(project);
                return Details(project);
            }

            case "setPlayback":
            {
                var project = Require(p);
                var o = new PlaybackOptions
                {
                    Volume = Math.Clamp(p["volume"]?.GetValue<int>() ?? 50, 0, 100),
                    Rate = Math.Clamp(p["rate"]?.GetValue<double>() ?? 1.0, 0.1, 4.0),
                    Fit = Enum.TryParse<VideoFit>(p["fit"]?.GetValue<string>(), true, out var f) ? f : VideoFit.Cover,
                };
                await _m.SetPlaybackAsync(project, o);
                return true;
            }

            case "setSettings":
                ApplySettings(p);
                await _m.SettingsChangedAsync();
                return BuildState();

            case "detectOtherApps":
            {
                var r = await _otherApps.DetectAsync();
                return new
                {
                    running = r.Running.Select(x => new { app = x.AppName, process = x.ProcessName, pid = x.Pid }).ToList(),
                    autostart = r.Autostart.Select(x => new { app = x.AppName, description = x.Describe(), enabled = x.Enabled, needsAdmin = x.NeedsAdmin }).ToList(),
                    disabledByUs = _m.Settings.DisabledAutostart.Select(x => new { app = x.AppName, description = x.Describe() }).ToList(),
                    guard = _m.Settings.CloseOtherWallpaperApps,
                };
            }

            case "disableOtherApps":
            {
                var s = await _otherApps.DisableAsync();
                return new { closed = s.Closed, disabled = s.Disabled, errors = s.Errors, imported = s.Imported, elevationDeclined = s.ElevationDeclined };
            }

            case "restoreOtherApps":
            {
                var s = await _otherApps.RestoreAsync();
                return new { restored = s.Disabled, errors = s.Errors, elevationDeclined = s.ElevationDeclined };
            }

            case "importFromWallpaperEngine":
                return await _otherApps.ImportWallpaperEngineSelectionAsync();

            case "setMuted":
                _m.SetMuted(p["muted"]!.GetValue<bool>());
                return BuildState();

            case "setPaused":
                await _m.SetPausedByUserAsync(p["paused"]!.GetValue<bool>());
                return BuildState();

            case "pickFile":
                return PickFile(p["kind"]?.GetValue<string>() ?? "image");

            case "pickFolder":
                return PickFolder(p["title"]?.GetValue<string>() ?? "Elegí una carpeta");

            case "addLibraryFolder":
            {
                var folder = PickFolder("Elegí una carpeta con wallpapers");
                if (folder == null) return null;
                if (!_m.Settings.LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    _m.Settings.LibraryFolders.Add(folder);
                    SettingsStore.Save(_m.Settings);
                }
                await Task.Run(() => _m.Library.Scan(_m.Settings));
                return BuildState();
            }

            case "removeLibraryFolder":
            {
                var folder = p["folder"]!.GetValue<string>();
                _m.Settings.LibraryFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
                SettingsStore.Save(_m.Settings);
                await Task.Run(() => _m.Library.Scan(_m.Settings));
                return BuildState();
            }

            case "importVideo":
            {
                var file = PickFile("video");
                if (file == null) return null;
                var folder = await Task.Run(() => Library.ImportVideo(file));
                await Task.Run(() => _m.Library.Scan(_m.Settings));
                return _m.Library.FindByFolder(folder)?.Id;
            }

            case "importWeb":
            {
                var src = PickFolder("Elegí la carpeta del wallpaper web (con index.html)");
                if (src == null) return null;
                if (!File.Exists(Path.Combine(src, "index.html")) && !File.Exists(Path.Combine(src, "project.json")))
                    throw new InvalidOperationException("La carpeta no tiene index.html ni project.json.");
                var folder = await Task.Run(() => Library.ImportWebFolder(src));
                await Task.Run(() => _m.Library.Scan(_m.Settings));
                return _m.Library.FindByFolder(folder)?.Id;
            }

            case "openFolder":
            {
                var project = Require(p);
                System.Diagnostics.Process.Start("explorer.exe", $"\"{project.Folder}\"");
                return true;
            }

            case "openLogs":
                System.Diagnostics.Process.Start("explorer.exe", $"\"{Log.Folder}\"");
                return true;

            case "devTools":
                _m.OpenDevTools(p["monitor"]!.GetValue<string>());
                return true;

            default:
                throw new InvalidOperationException($"Método desconocido: {method}");
        }
    }

    private void ApplySettings(JsonObject p)
    {
        var s = _m.Settings;
        if (p["pauseMode"]?.GetValue<string>() is string pm && Enum.TryParse<PauseMode>(pm, true, out var mode)) s.PauseMode = mode;
        if (p["startWithWindows"] is JsonValue sw)
        {
            s.StartWithWindows = sw.GetValue<bool>();
            StartupManager.Apply(s.StartWithWindows);
        }
        if (p["startMinimized"] is JsonValue sm) s.StartMinimized = sm.GetValue<bool>();
        if (p["pauseOnBattery"] is JsonValue pb) s.PauseOnBattery = pb.GetValue<bool>();
        if (p["closeOtherWallpaperApps"] is JsonValue co)
        {
            s.CloseOtherWallpaperApps = co.GetValue<bool>();
            if (s.CloseOtherWallpaperApps) _otherApps.StartGuard();
        }
        if (p["scanSteamWorkshop"] is JsonValue ss) s.ScanSteamWorkshop = ss.GetValue<bool>();
        if (p["fpsLimit"] is JsonValue fl) s.FpsLimit = Math.Clamp(fl.GetValue<int>(), 5, 240);
        if (p["wallpaperEngineInstallPath"] is JsonValue wp) s.WallpaperEngineInstallPath = wp.GetValue<string>() ?? "";
        if (p["pauseIgnoreProcesses"] is JsonArray ig)
            s.PauseIgnoreProcesses = ig.Select(x => x?.GetValue<string>()?.Trim()).Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x).ToList();
        SettingsStore.Save(s);
        if (p["scanSteamWorkshop"] != null || p["wallpaperEngineInstallPath"] != null) _m.Library.Scan(s);
    }

    private WallpaperProject Require(JsonObject p)
    {
        var id = p["id"]?.GetValue<string>() ?? throw new ArgumentException("Falta id");
        return _m.Library.Get(id) ?? throw new InvalidOperationException("Ese wallpaper ya no está en la biblioteca.");
    }

    private object BuildState()
    {
        var s = _m.Settings;
        return new
        {
            version = typeof(Bridge).Assembly.GetName().Version?.ToString(3),
            monitors = _m.CurrentMonitors.Select(mon => new
            {
                id = mon.Id,
                index = mon.Index,
                primary = mon.IsPrimary,
                x = mon.Bounds.X, y = mon.Bounds.Y, width = mon.Bounds.Width, height = mon.Bounds.Height,
                scale = mon.Scale,
                wallpaperId = _m.GetAssigned(mon.Id)?.Id,
                wallpaperTitle = _m.GetAssigned(mon.Id)?.Title,
                covered = _m.CoveredMonitors.Contains(mon.Id),
            }).ToList(),
            paused = _m.PausedByUser,
            batteryPause = _m.OnBatteryPause,
            settings = new
            {
                pauseMode = s.PauseMode.ToString(),
                muted = s.Muted,
                startWithWindows = StartupManager.IsEnabled(),
                startMinimized = s.StartMinimized,
                pauseOnBattery = s.PauseOnBattery,
                closeOtherWallpaperApps = s.CloseOtherWallpaperApps,
                scanSteamWorkshop = s.ScanSteamWorkshop,
                fpsLimit = s.FpsLimit,
                wallpaperEngineInstallPath = s.WallpaperEngineInstallPath,
                detectedWallpaperEngine = SteamLocator.WallpaperEngineInstall(),
                libraryFolders = s.LibraryFolders,
                defaultLibrary = AppPaths.DefaultLibrary,
                pauseIgnoreProcesses = s.PauseIgnoreProcesses,
            },
        };
    }

    private static string MediaUrl(string? path)
        => path == null ? "" : $"https://{MainWindow.MediaHost}/?path={Uri.EscapeDataString(path)}";

    private static object Summary(WallpaperProject p) => new
    {
        id = p.Id,
        title = p.Title,
        type = p.Type.ToString().ToLowerInvariant(),
        preview = MediaUrl(p.PreviewPath),
        tags = p.Tags,
        rating = p.ContentRating,
        source = p.Source.ToString(),
        hasProperties = p.Properties.Count > 0,
        modified = p.Modified,
        size = p.SizeBytes,
        supported = p.Type is WallpaperType.Web or WallpaperType.Video or WallpaperType.Scene,
    };

    private object Details(WallpaperProject p)
    {
        var playback = PropertyValues.Playback(_m.Settings, p);
        return new
        {
            id = p.Id,
            title = p.Title,
            description = p.Description,
            type = p.Type.ToString().ToLowerInvariant(),
            preview = MediaUrl(p.PreviewPath),
            previewIsVideo = p.PreviewPath != null && WallpaperProject.IsVideoFile(p.PreviewPath),
            folder = p.Folder,
            file = p.EntryFile,
            tags = p.Tags,
            rating = p.ContentRating,
            workshopId = p.WorkshopId,
            source = p.Source.ToString(),
            size = p.SizeBytes,
            playback = new { volume = playback.Volume, rate = playback.Rate, fit = playback.Fit.ToString().ToLowerInvariant() },
            properties = p.Properties.Select(prop => new
            {
                key = prop.Key,
                type = prop.Type,
                label = prop.Label,
                order = prop.Order,
                value = PropertyValues.Effective(p, prop, _m.Settings),
                defaultValue = prop.DefaultValue,
                min = prop.Min, max = prop.Max, step = prop.Step,
                fraction = prop.Fraction, precision = prop.Precision,
                condition = prop.Condition,
                fileType = prop.FileType,
                options = prop.Options.Select(o => new { label = o.Label, value = o.Value }).ToList(),
            }).ToList(),
        };
    }

    private string? PickFile(string kind)
    {
        using var dlg = new OpenFileDialog
        {
            Title = kind == "video" ? "Elegí un video" : "Elegí un archivo",
            Filter = kind switch
            {
                "video" => "Videos|*.mp4;*.webm;*.m4v;*.mov;*.mkv|Todos los archivos|*.*",
                "image" => "Imágenes|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp|Todos los archivos|*.*",
                _ => "Todos los archivos|*.*",
            },
        };
        return dlg.ShowDialog(_window) == DialogResult.OK ? dlg.FileName : null;
    }

    private string? PickFolder(string title)
    {
        using var dlg = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = false };
        return dlg.ShowDialog(_window) == DialogResult.OK ? dlg.SelectedPath : null;
    }
}
