using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WallpaperForge.Core;

public enum PauseMode
{
    /// <summary>Pausar el monitor cuando hay una app en pantalla completa encima.</summary>
    Fullscreen,
    /// <summary>Pausar también cuando hay una ventana maximizada.</summary>
    FullscreenOrMaximized,
    /// <summary>No pausar nunca.</summary>
    Never,
}

public enum VideoFit { Cover, Contain, Stretch }

/// <summary>Opciones del reproductor que no vienen del project.json (por wallpaper).</summary>
public sealed class PlaybackOptions
{
    public int Volume { get; set; } = 50;          // 0..100
    public double Rate { get; set; } = 1.0;        // velocidad de video
    public VideoFit Fit { get; set; } = VideoFit.Cover;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>Carpetas extra de wallpapers agregadas por el usuario.</summary>
    public List<string> LibraryFolders { get; set; } = new();

    /// <summary>Buscar automáticamente wallpapers descargados de Steam Workshop (Wallpaper Engine).</summary>
    public bool ScanSteamWorkshop { get; set; } = true;

    /// <summary>Carpeta de instalación de Wallpaper Engine (para assets compartidos de escenas). Vacía = autodetectar.</summary>
    public string WallpaperEngineInstallPath { get; set; } = "";

    /// <summary>Monitor (Id) → carpeta del wallpaper asignado.</summary>
    public Dictionary<string, string> Assignments { get; set; } = new();

    /// <summary>Carpeta del wallpaper → valores de propiedades modificados por el usuario.</summary>
    public Dictionary<string, Dictionary<string, JsonNode?>> PropertyValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Carpeta del wallpaper → opciones de reproducción.</summary>
    public Dictionary<string, PlaybackOptions> Playback { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PauseMode PauseMode { get; set; } = PauseMode.Fullscreen;

    /// <summary>Procesos que nunca pausan el wallpaper (p. ej. overlays). Sin ".exe".</summary>
    public List<string> PauseIgnoreProcesses { get; set; } = new() { "NVIDIA Share", "nvcontainer", "GameBar" };

    public bool Muted { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public int FpsLimit { get; set; } = 60;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options);
                if (s != null)
                {
                    // Los diccionarios deserializados pierden el comparador; se reconstruyen.
                    s.PropertyValues = new(s.PropertyValues, StringComparer.OrdinalIgnoreCase);
                    s.Playback = new(s.Playback, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo leer settings.json, se usan valores por defecto", ex);
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak", true); } catch { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Roaming);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar settings.json", ex);
        }
    }
}
