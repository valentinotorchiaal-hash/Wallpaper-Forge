using System.Diagnostics;
using WallpaperForge.Wallpapers;

namespace WallpaperForge.Core;

/// <summary>Flujo completo para dejar de usar otro programa de fondos y restaurarlo si hace falta.</summary>
public sealed class OtherAppsService : IDisposable
{
    private readonly WallpaperManager _m;
    private readonly System.Windows.Forms.Timer _guard = new() { Interval = 10_000 };

    public OtherAppsService(WallpaperManager manager)
    {
        _m = manager;
        _guard.Tick += (_, _) => CloseIfRunning();
    }

    public void StartGuard() => _guard.Start();

    public sealed class Summary
    {
        public List<string> Closed { get; } = new();
        public List<string> Disabled { get; } = new();
        public List<string> Errors { get; } = new();
        public int Imported { get; set; }
        public bool ElevationDeclined { get; set; }
    }

    public Task<CompetitorReport> DetectAsync() => Task.Run(OtherWallpaperApps.Detect);

    /// <summary>Cierra los otros programas, desactiva su inicio automático y copia sus wallpapers elegidos.</summary>
    public async Task<Summary> DisableAsync()
    {
        var summary = new Summary();
        var report = await DetectAsync();

        // 1) Copiar la selección de Wallpaper Engine antes de cerrarlo (sólo en monitores sin wallpaper propio).
        summary.Imported = await ImportWallpaperEngineSelectionAsync();

        // 2) Cerrar procesos (los que tienen permisos elevados se cierran en el lote de administrador).
        var failed = await Task.Run(() => OtherWallpaperApps.CloseRunning(report.Running));
        summary.Closed.AddRange(report.Running.Except(failed).Select(r => $"{r.AppName} ({r.ProcessName})").Distinct());

        // 3) Desactivar el arranque automático.
        var toDisable = report.Autostart.Where(a => a.Enabled).ToList();
        if (toDisable.Count > 0 || failed.Count > 0)
        {
            var res = await Task.Run(() => OtherWallpaperApps.SetAutostart(toDisable, enable: false, alsoKill: failed));
            summary.ElevationDeclined = res.ElevationDeclined;
            summary.Errors.AddRange(res.Errors);
            foreach (var e in res.Changed)
            {
                summary.Disabled.Add($"{e.AppName} — {e.Describe()}");
                _m.Settings.DisabledAutostart.RemoveAll(x => x.Key == e.Key);
                _m.Settings.DisabledAutostart.Add(e);
            }
        }

        _m.Settings.CloseOtherWallpaperApps = true;
        _m.Settings.CompetitorsPromptShown = true;
        SettingsStore.Save(_m.Settings);
        StartGuard();
        Log.Info($"Otros programas: cerrados {summary.Closed.Count}, inicio desactivado {summary.Disabled.Count}, importados {summary.Imported}");
        return summary;
    }

    /// <summary>Vuelve a habilitar todo lo que Wallpaper Forge desactivó.</summary>
    public async Task<Summary> RestoreAsync()
    {
        var summary = new Summary();
        _guard.Stop();
        _m.Settings.CloseOtherWallpaperApps = false;
        var entries = _m.Settings.DisabledAutostart.ToList();
        if (entries.Count > 0)
        {
            var res = await Task.Run(() => OtherWallpaperApps.SetAutostart(entries, enable: true));
            summary.ElevationDeclined = res.ElevationDeclined;
            summary.Errors.AddRange(res.Errors);
            foreach (var e in res.Changed)
            {
                summary.Disabled.Add($"{e.AppName} — {e.Describe()}");
                _m.Settings.DisabledAutostart.RemoveAll(x => x.Key == e.Key);
            }
        }
        SettingsStore.Save(_m.Settings);
        return summary;
    }

    /// <summary>Si Wallpaper Engine/Lively se abren (p. ej. desde Steam) mientras Forge funciona, se cierran.</summary>
    private void CloseIfRunning()
    {
        if (!_m.Settings.CloseOtherWallpaperApps) return;
        var running = new List<RunningCompetitor>();
        foreach (var app in AutostartMatcher.Known)
        {
            foreach (var name in app.ProcessNames)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    using (p) running.Add(new RunningCompetitor(app.Id, app.Name, p.Id, p.ProcessName, null));
                }
            }
        }
        if (running.Count == 0) return;
        Log.Info("Se abrió otro programa de fondos; cerrándolo: " + string.Join(", ", running.Select(r => r.ProcessName)));
        OtherWallpaperApps.CloseRunning(running);
    }

    /// <summary>Asigna en Forge los wallpapers que Wallpaper Engine tenía en cada monitor (si ese monitor no tiene uno).</summary>
    public async Task<int> ImportWallpaperEngineSelectionAsync()
    {
        try
        {
            var install = Library.ResolveWallpaperEngineInstall(_m.Settings);
            var config = install == null ? null : Path.Combine(install, "config.json");
            if (config == null || !File.Exists(config)) return 0;

            var selections = WallpaperEngineMigration.ParseConfig(await File.ReadAllTextAsync(config));
            var map = WallpaperEngineMigration.MapToMonitors(selections, _m.CurrentMonitors.Select(m => m.Id).ToList());
            int imported = 0;
            foreach (var (monitorId, folder) in map)
            {
                if (_m.Settings.Assignments.ContainsKey(monitorId) || !Directory.Exists(folder)) continue;
                var project = WallpaperProject.Load(folder, WallpaperSource.SteamWorkshop);
                if (project == null || project.Type is not (WallpaperType.Video or WallpaperType.Web or WallpaperType.Scene)) continue;
                try
                {
                    await _m.ApplyAsync(project, monitorId);
                    imported++;
                }
                catch (Exception ex) { Log.Warn($"No se pudo importar {folder}: {ex.Message}"); }
            }
            return imported;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer la configuración de Wallpaper Engine: " + ex.Message);
            return 0;
        }
    }

    public void Dispose()
    {
        _guard.Stop();
        _guard.Dispose();
    }
}
