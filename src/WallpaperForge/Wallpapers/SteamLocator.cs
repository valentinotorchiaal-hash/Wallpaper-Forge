using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WallpaperForge.Wallpapers;

/// <summary>Encuentra bibliotecas de Steam y las carpetas de Wallpaper Engine (app 431960).</summary>
public static partial class SteamLocator
{
    public const string WallpaperEngineAppId = "431960";

    public static List<string> GetLibraries()
    {
        var libs = new List<string>();
        var steam = GetSteamPath();
        if (steam == null) return libs;
        libs.Add(steam);

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                foreach (Match m in PathRegex().Matches(File.ReadAllText(vdf)))
                {
                    var p = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(p)) libs.Add(p);
                }
            }
            catch (Exception ex) { Core.Log.Warn("No se pudo leer libraryfolders.vdf: " + ex.Message); }
        }
        return libs.Select(p => Path.GetFullPath(p).TrimEnd('\\'))
                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? GetSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return p.Replace('/', '\\');
        }
        catch { }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key?.GetValue("InstallPath") is string p && Directory.Exists(p)) return p;
        }
        catch { }
        var def = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(def) ? def : null;
    }

    /// <summary>Carpetas content\431960 de todas las bibliotecas (wallpapers suscriptos en Workshop).</summary>
    public static IEnumerable<string> WorkshopFolders()
    {
        foreach (var lib in GetLibraries())
        {
            var p = Path.Combine(lib, "steamapps", "workshop", "content", WallpaperEngineAppId);
            if (Directory.Exists(p)) yield return p;
        }
    }

    /// <summary>Carpeta de instalación de Wallpaper Engine, si está instalado.</summary>
    public static string? WallpaperEngineInstall()
    {
        foreach (var lib in GetLibraries())
        {
            var p = Path.Combine(lib, "steamapps", "common", "wallpaper_engine");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>projects\myprojects y projects\defaultprojects de Wallpaper Engine.</summary>
    public static IEnumerable<string> ProjectFolders(string? install)
    {
        if (install == null) yield break;
        foreach (var sub in new[] { "myprojects", "defaultprojects" })
        {
            var p = Path.Combine(install, "projects", sub);
            if (Directory.Exists(p)) yield return p;
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();
}
