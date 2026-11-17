namespace WallpaperForge.Core;

/// <summary>Otro programa de fondos animados que puede competir con Wallpaper Forge.</summary>
public sealed record CompetitorApp(string Id, string Name, string[] PathMarkers, string[] ProcessNames);

/// <summary>
/// Lógica pura (sin Windows) para reconocer otros programas de fondos de pantalla
/// en comandos de inicio, rutas de ejecutables y nombres de procesos. Se testea en cualquier SO.
/// </summary>
public static class AutostartMatcher
{
    public static readonly CompetitorApp[] Known =
    {
        new("wallpaper-engine", "Wallpaper Engine",
            new[] { @"\wallpaper_engine\", "wallpaper32.exe", "wallpaper64.exe", "webwallpaper32.exe" },
            new[] { "wallpaper32", "wallpaper64", "webwallpaper32", "ui32", "wallpaperservice32", "wallpaperservice32_c" }),
        new("lively", "Lively Wallpaper",
            new[] { @"\lively wallpaper\", @"\lively\", "livelywpf.exe", "lively.exe", "lively.watchdog.exe", @"\rocksdanister.livelywallpaper" },
            new[] { "Lively", "livelywpf", "Lively.Watchdog", "Lively.UI.WinUI", "Lively.PlayerWebView2", "Lively.PlayerMpv" }),
    };

    /// <summary>Procesos con nombre genérico (p. ej. "launcher") que sólo cuentan si su ruta coincide.</summary>
    public static readonly string[] PathOnlyProcessNames = { "launcher" };

    /// <summary>Devuelve el programa al que pertenece un comando/ruta, o null.</summary>
    public static CompetitorApp? MatchPath(string? commandOrPath)
    {
        if (string.IsNullOrWhiteSpace(commandOrPath)) return null;
        var s = commandOrPath.Replace('/', '\\').ToLowerInvariant();
        if (s.Contains("wallpaperforge")) return null; // nunca a nosotros mismos
        foreach (var app in Known)
            foreach (var marker in app.PathMarkers)
                if (s.Contains(marker.ToLowerInvariant())) return app;
        return null;
    }

    /// <summary>Por nombre de proceso (sin .exe). Si el nombre es genérico, exige que la ruta coincida.</summary>
    public static CompetitorApp? MatchProcess(string processName, string? exePath)
    {
        var byPath = MatchPath(exePath);
        if (PathOnlyProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)) return byPath;
        foreach (var app in Known)
            if (app.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)) return byPath ?? app;
        return byPath;
    }

    /// <summary>
    /// Valor binario de HKCU/HKLM\...\Explorer\StartupApproved\{Run|StartupFolder}: el mismo formato que usa el
    /// Administrador de tareas. Primer byte 02 = habilitado, 03 = deshabilitado; luego 3 bytes en 0 y un FILETIME.
    /// </summary>
    public static byte[] StartupApprovedValue(bool enabled, DateTime? whenUtc = null)
    {
        var data = new byte[12];
        data[0] = (byte)(enabled ? 0x02 : 0x03);
        if (!enabled)
            BitConverter.GetBytes((whenUtc ?? DateTime.UtcNow).ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }

    /// <summary>true si el valor de StartupApproved indica "deshabilitado" (bit 0 del primer byte en 1).</summary>
    public static bool IsStartupApprovedDisabled(byte[]? value) => value is { Length: > 0 } && (value[0] & 0x01) != 0;

    /// <summary>Parte una línea CSV de schtasks (con comillas dobles).</summary>
    public static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    /// <summary>Busca una ruta dentro de un .lnk (texto ANSI o UTF-16) sin usar COM.</summary>
    public static CompetitorApp? MatchShortcutBytes(string fileName, byte[] lnk)
    {
        var byName = MatchPath(fileName.Replace(' ', '_'))
                     ?? (fileName.Contains("Wallpaper Engine", StringComparison.OrdinalIgnoreCase) ? Known[0] : null)
                     ?? (fileName.Contains("Lively", StringComparison.OrdinalIgnoreCase) ? Known[1] : null);
        if (byName != null) return byName;
        var ascii = System.Text.Encoding.Latin1.GetString(lnk);
        var utf16 = System.Text.Encoding.Unicode.GetString(lnk);
        return MatchPath(ascii) ?? MatchPath(utf16);
    }
}
