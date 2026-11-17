namespace WallpaperForge.Core;

/// <summary>Log de texto simple en %LOCALAPPDATA%\WallpaperForge\logs.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static string Folder => Path.Combine(AppPaths.LocalData, "logs");

    public static void Init()
    {
        Directory.CreateDirectory(Folder);
        _file = Path.Combine(Folder, $"wallpaperforge-{DateTime.Now:yyyyMMdd}.log");
        // Borrar logs de más de 7 días.
        foreach (var f in Directory.GetFiles(Folder, "*.log"))
        {
            try { if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f); } catch { }
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_file is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_file, line + Environment.NewLine); } catch { }
        }
    }
}

public static class AppPaths
{
    public const string AppName = "WallpaperForge";

    public static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
    public static string LocalData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
    public static string WebViewData => Path.Combine(LocalData, "WebView2");
    public static string SceneCache => Path.Combine(LocalData, "scene-cache");

    /// <summary>Biblioteca propia: Documentos\Wallpaper Forge\wallpapers.</summary>
    public static string DefaultLibrary => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Wallpaper Forge", "wallpapers");

    public static string AppDir => AppContext.BaseDirectory;
    public static string WwwRoot => Path.Combine(AppDir, "wwwroot");
    public static string RuntimeDir => Path.Combine(WwwRoot, "runtime");
    public static string UiDir => Path.Combine(WwwRoot, "ui");
}

public static class AppVersion
{
    public static string Text => typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "?";
}
