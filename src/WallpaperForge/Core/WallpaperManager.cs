using System.Text.Json.Nodes;
using Microsoft.Win32;
using WallpaperForge.Rendering;
using WallpaperForge.Wallpapers;

namespace WallpaperForge.Core;

/// <summary>Coordina monitores, ventanas de wallpaper, asignaciones guardadas y pausa por pantalla completa.</summary>
public sealed class WallpaperManager : IDisposable
{
    private readonly DesktopHost _desktop = new();
    private readonly Dictionary<string, WallpaperWindow> _windows = new();
    private readonly FullscreenWatcher _watcher;
    private readonly HashSet<string> _covered = new();
    private bool _pausedByUser;

    public AppSettings Settings { get; }
    public Library Library { get; } = new();
    public List<MonitorInfo> CurrentMonitors { get; private set; } = new();

    /// <summary>Algo cambió (asignaciones, pausa, monitores) y la UI debería refrescarse.</summary>
    public event Action? StateChanged;
    /// <summary>Advertencias al cargar un wallpaper (se muestran en la UI).</summary>
    public event Action<string, List<string>>? LoadWarnings;

    public bool PausedByUser => _pausedByUser;
    public IReadOnlySet<string> CoveredMonitors => _covered;

    public WallpaperManager(AppSettings settings)
    {
        Settings = settings;
        _watcher = new FullscreenWatcher
        {
            SettingsProvider = () => Settings,
            MonitorsProvider = () => CurrentMonitors,
        };
        _watcher.CoveredMonitorsChanged += OnCoveredChanged;
    }

    public async Task StartAsync()
    {
        CurrentMonitors = Monitors.GetAll();
        _desktop.Initialize();
        Library.Scan(Settings);

        foreach (var (monitorId, folder) in Settings.Assignments.ToList())
        {
            if (CurrentMonitors.All(m => m.Id != monitorId)) continue;
            var project = Library.FindByFolder(folder) ?? WallpaperProject.Load(folder, WallpaperSource.Local);
            if (project == null)
            {
                Log.Warn($"El wallpaper asignado a {monitorId} ya no existe: {folder}");
                continue;
            }
            try { await ApplyAsync(project, monitorId, save: false); }
            catch (Exception ex) { Log.Error($"No se pudo restaurar el wallpaper de {monitorId}", ex); }
        }

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _watcher.Start();
    }

    public WallpaperProject? GetAssigned(string monitorId)
        => _windows.TryGetValue(monitorId, out var w) ? w.Project : null;

    public async Task ApplyAsync(WallpaperProject project, string monitorId, bool save = true)
    {
        var monitor = CurrentMonitors.FirstOrDefault(m => m.Id == monitorId)
                      ?? throw new ArgumentException($"Monitor desconocido: {monitorId}");
        if (project.Type is not (WallpaperType.Web or WallpaperType.Video or WallpaperType.Scene))
            throw new NotSupportedException($"Los wallpapers de tipo {project.Type} todavía no están soportados.");

        if (!_windows.TryGetValue(monitorId, out var window))
        {
            window = new WallpaperWindow(monitor);
            await window.InitializeAsync(_desktop);
            _windows[monitorId] = window;
        }

        var warnings = await window.LoadAsync(project, Settings);
        await window.SetPausedAsync(ShouldPause(monitorId));
        if (warnings.Count > 0) LoadWarnings?.Invoke(project.Title, warnings);

        if (save)
        {
            Settings.Assignments[monitorId] = project.Folder;
            SettingsStore.Save(Settings);
        }
        StateChanged?.Invoke();
    }

    public async Task ApplyToAllAsync(WallpaperProject project)
    {
        foreach (var m in CurrentMonitors) await ApplyAsync(project, m.Id);
    }

    public void Clear(string monitorId)
    {
        if (_windows.Remove(monitorId, out var w))
        {
            w.Close();
            w.Dispose();
        }
        Settings.Assignments.Remove(monitorId);
        SettingsStore.Save(Settings);
        if (_windows.Count == 0) DesktopHost.RefreshStaticWallpaper();
        StateChanged?.Invoke();
    }

    /// <summary>Propaga un cambio de propiedad a todas las ventanas que muestran ese wallpaper.</summary>
    public async Task SetPropertyAsync(WallpaperProject project, string key, JsonNode? value)
    {
        PropertyValues.Set(Settings, project, key, value);
        SettingsStore.Save(Settings);
        var changed = PropertyValues.BuildUserProperties(project, Settings, new[] { key });
        foreach (var w in WindowsShowing(project)) await w.ApplyUserPropertiesAsync(changed);
    }

    public async Task ResetPropertiesAsync(WallpaperProject project)
    {
        PropertyValues.Reset(Settings, project);
        SettingsStore.Save(Settings);
        var all = PropertyValues.BuildUserProperties(project, Settings);
        foreach (var w in WindowsShowing(project)) await w.ApplyUserPropertiesAsync(all);
    }

    public async Task SetPlaybackAsync(WallpaperProject project, PlaybackOptions o)
    {
        Settings.Playback[project.Folder] = o;
        SettingsStore.Save(Settings);
        foreach (var w in WindowsShowing(project)) await w.ApplyPlaybackAsync(o);
    }

    /// <summary>Recarga el wallpaper (necesario p. ej. cuando cambia una carpeta de una propiedad "directory").</summary>
    public async Task ReloadAsync(WallpaperProject project)
    {
        foreach (var w in WindowsShowing(project).ToList()) await w.LoadAsync(project, Settings);
    }

    public void SetMuted(bool muted)
    {
        Settings.Muted = muted;
        SettingsStore.Save(Settings);
        foreach (var w in _windows.Values) w.SetMuted(muted);
        StateChanged?.Invoke();
    }

    public async Task SetPausedByUserAsync(bool paused)
    {
        _pausedByUser = paused;
        await UpdatePauseStatesAsync();
        StateChanged?.Invoke();
    }

    public void OpenDevTools(string monitorId)
    {
        if (_windows.TryGetValue(monitorId, out var w)) w.OpenDevTools();
    }

    private IEnumerable<WallpaperWindow> WindowsShowing(WallpaperProject p)
        => _windows.Values.Where(w => w.Project?.Id == p.Id);

    private bool ShouldPause(string monitorId) => _pausedByUser || _covered.Contains(monitorId);

    private async void OnCoveredChanged(IReadOnlySet<string> covered)
    {
        _covered.Clear();
        _covered.UnionWith(covered);
        await UpdatePauseStatesAsync();
        StateChanged?.Invoke();
    }

    private async Task UpdatePauseStatesAsync()
    {
        foreach (var (id, w) in _windows) await w.SetPausedAsync(ShouldPause(id));
    }

    // ---------- Cambios del sistema ----------

    private async void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // SystemEvents llega en otro hilo; se vuelve al hilo de la UI.
        if (Program.UiContext != null && SynchronizationContext.Current != Program.UiContext)
        {
            Program.UiContext.Post(_ => OnDisplaySettingsChanged(sender, e), null);
            return;
        }
        await Task.Delay(800); // Windows tarda en terminar de reacomodar los monitores
        Log.Info("Cambió la configuración de pantallas");
        CurrentMonitors = Monitors.GetAll();
        _desktop.Initialize();

        foreach (var id in _windows.Keys.ToList())
        {
            var m = CurrentMonitors.FirstOrDefault(x => x.Id == id);
            if (m == null)
            {
                // Monitor desconectado: se cierra la ventana pero se conserva la asignación guardada.
                _windows.Remove(id, out var w);
                w?.Close();
                w?.Dispose();
            }
            else
            {
                _windows[id].Reattach(_desktop);
                _windows[id].UpdateMonitor(m, _desktop);
            }
        }

        // Monitores reconectados con wallpaper guardado.
        foreach (var (id, folder) in Settings.Assignments.ToList())
        {
            if (_windows.ContainsKey(id) || CurrentMonitors.All(m => m.Id != id)) continue;
            var p = Library.FindByFolder(folder);
            if (p != null) { try { await ApplyAsync(p, id, save: false); } catch (Exception ex) { Log.Error("Restaurar", ex); } }
        }
        StateChanged?.Invoke();
    }

    /// <summary>Explorer se reinició (TaskbarCreated): hay que volver a incrustar las ventanas.</summary>
    public void OnExplorerRestarted()
    {
        Log.Info("Explorer se reinició; reincrustando wallpapers");
        try
        {
            _desktop.Initialize();
            foreach (var w in _windows.Values) w.Reattach(_desktop);
        }
        catch (Exception ex) { Log.Error("No se pudo reincrustar", ex); }
    }

    private async void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (Program.UiContext != null && SynchronizationContext.Current != Program.UiContext)
        {
            Program.UiContext.Post(_ => OnSessionSwitch(sender, e), null);
            return;
        }
        // Pantalla bloqueada: no tiene sentido renderizar.
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            foreach (var w in _windows.Values) await w.SetPausedAsync(true);
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            await UpdatePauseStatesAsync();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _watcher.Dispose();
        foreach (var w in _windows.Values)
        {
            w.Close();
            w.Dispose();
        }
        _windows.Clear();
        DesktopHost.RefreshStaticWallpaper();
    }
}
