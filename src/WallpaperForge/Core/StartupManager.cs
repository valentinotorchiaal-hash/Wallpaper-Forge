using Microsoft.Win32;

namespace WallpaperForge.Core;

/// <summary>
/// Arranque con Windows mediante HKCU\...\Run (no requiere permisos de administrador).
/// También respeta/limpia el estado "Deshabilitado" que pone el Administrador de tareas (StartupApproved).
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "WallpaperForge";

    public static string Command
    {
        get
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppPaths.AppDir, "WallpaperForge.exe");
            return $"\"{exe}\" --startup";
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, Command);
                // Si estaba deshabilitado desde el Administrador de tareas, se vuelve a habilitar.
                approved.SetValue(ValueName, AutostartMatcher.StartupApprovedValue(true), RegistryValueKind.Binary);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                approved.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            Log.Info("Arranque con Windows: " + (enabled ? "activado → " + Command : "desactivado"));
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo actualizar el arranque con Windows", ex);
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return !AutostartMatcher.IsStartupApprovedDisabled(approved?.GetValue(ValueName) as byte[]);
        }
        catch { return false; }
    }

    /// <summary>Si el arranque está activo pero apunta a otra ubicación del .exe (se movió la carpeta), lo corrige.</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, Command);
                Log.Info("Arranque con Windows: ruta actualizada → " + Command);
            }
        }
        catch (Exception ex) { Log.Warn("No se pudo revisar el arranque con Windows: " + ex.Message); }
    }
}
