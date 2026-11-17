using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using WallpaperForge.Rendering;
using WallpaperForge.Wallpapers;

namespace WallpaperForge.Core;

/// <summary>Coordina monitores, ventanas de wallpaper, asignaciones guardadas y pausas automáticas.</summary>
public sealed class WallpaperManager : IDisposable
{
    private readonly DesktopHost _desktop = new();
    private readonly Dictionary<string, WallpaperWindow> _windows = new();
    private readonly FullscreenWatcher _watcher;
    private readonly HashSet<string> _covered = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Windows.Forms.Timer _healthTimer = new() { Interval = 3000 };
    private bool _pausedByUser;
    private bool _sessionLocked;
    private bool _onBattery;
    private bool _recovering;

    public AppSettings Settings { get; }
    public Library Library { get; } = new();
    public List<MonitorInfo> CurrentMonitors { get; private set; } = new();

    /// <summary>Se completa cuando terminó el primer escaneo de la biblioteca (se hace en segundo plano).</summary>
    public Task LibraryReady { get; private set; } = Task.CompletedTask;

    /// <summary>Algo cambió (asignaciones, pausa, monitores) y la UI debería refrescarse.</summary>
    public event Action? StateChanged;
    /// <summary>Advertencias al cargar un wallpaper (se muestran en la UI).</summary>
    public event Action<string, List<string>>? LoadWarnings;

    public bool PausedByUser => _pausedByUser;
    public bool OnBatteryPause => _onBattery && Settings.PauseOnBattery;
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
        _healthTimer.Tick += async (_, _) => await HealthCheckAsync();
    }

    public async Task StartAsync()
    {
        // Al arrancar con Windows, Explorer puede no estar listo todavía: se espera hasta 90 s.
        await WaitForDesktopAsync(TimeSpan.FromSeconds(90));
        CurrentMonitors = Monitors.GetAll();

        // El escaneo completo (con Steam Workshop puede ser lento) va en segundo plano; los wallpapers
        // asignados se cargan directamente desde su carpeta para que el escritorio aparezca enseguida.
        LibraryReady = Task.Run(() => Library.Scan(Settings));

        foreach (var (monitorId, folder) in Settings.Assignments.ToList())
        {
            if (CurrentMonitors.All(m => m.Id != monitorId)) continue;
            var project = WallpaperProject.Load(folder, WallpaperSource.Local);
            if (project == null)
            {
                Log.Warn($"El wallpaper asignado a {monitorId} ya no existe: {folder}");
                continue;
            }
            try { await ApplyAsync(project, monitorId, save: false); }
            catch (Exception ex) { Log.Error($"No se pudo restaurar el wallpaper de {monitorId}", ex); }
        }

        _onBattery = IsOnBattery();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _watcher.Start();
        _healthTimer.Start();
        if (OnBatteryPause) await UpdatePauseStatesAsync();
    }

    private async Task WaitForDesktopAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                _desktop.Initialize();
                if (Monitors.GetAll().Count > 0) return;
            }
            catch (Exception ex) when (sw.Elapsed < timeout)
            {
                Log.Info("Esperando al escritorio de Windows: " + ex.Message);
            }
            if (sw.Elapsed >= timeout) throw new InvalidOperationException("El escritorio de Windows no respondió a tiempo.");
            await Task.Delay(1500);
        }
    }

    public WallpaperProject? GetAssigned(string monitorId)
        => _windows.TryGetValue(monitorId, out var w) ? w.Project : null;

    public async Task ApplyAsync(WallpaperProject project, string monitorId, bool save = true)
    {
        await _gate.WaitAsync();
        try { await ApplyCoreAsync(project, monitorId, save); }
        finally { _gate.Release(); }
        StateChanged?.Invoke();
    }

    private async Task ApplyCoreAsync(WallpaperProject project, string monitorId, bool save)
    {
        var monitor = CurrentMonitors.FirstOrDefault(m => m.Id == monitorId)
                      ?? throw new ArgumentException($"Monitor desconocido: {monitorId}");
        if (project.Type is not (WallpaperType.Web or WallpaperType.Video or WallpaperType.Scene))
            throw new NotSupportedException($"Los wallpapers de tipo {project.Type} todavía no están soportados.");

        if (!_windows.TryGetValue(monitorId, out var window) || !window.IsAlive)
        {
            if (window != null) DisposeWindow(window);
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
    }

    public async Task ApplyToAllAsync(WallpaperProject project)
    {
        foreach (var m in CurrentMonitors) await ApplyAsync(project, m.Id);
    }

    public void Clear(string monitorId)
    {
        if (_windows.Remove(monitorId, out var w)) DisposeWindow(w);
        Settings.Assignments.Remove(monitorId);
        SettingsStore.Save(Settings);
        if (_windows.Count == 0 && !_desktop.IsLayeredShellView) DesktopHost.RefreshStaticWallpaper();
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

    /// <summary>Se llama cuando cambian ajustes que afectan la pausa (modo, batería).</summary>
    public async Task SettingsChangedAsync()
    {
        _watcher.Check();
        foreach (var w in _windows.Values.ToList()) await w.ApplyGeneralAsync(Settings.FpsLimit);
        await UpdatePauseStatesAsync();
        StateChanged?.Invoke();
    }

    public void OpenDevTools(string monitorId)
    {
        if (_windows.TryGetValue(monitorId, out var w)) w.OpenDevTools();
    }

    private IEnumerable<WallpaperWindow> WindowsShowing(WallpaperProject p)
        => _windows.Values.Where(w => w.Project?.Id == p.Id);

    private bool ShouldPause(string monitorId)
        => _pausedByUser || _sessionLocked || OnBatteryPause || _covered.Contains(monitorId);

    private async void OnCoveredChanged(IReadOnlySet<string> covered)
    {
        _covered.Clear();
        _covered.UnionWith(covered);
        await UpdatePauseStatesAsync();
        StateChanged?.Invoke();
    }

    private async Task UpdatePauseStatesAsync()
    {
        foreach (var (id, w) in _windows.ToList()) await w.SetPausedAsync(ShouldPause(id));
    }

    private static void DisposeWindow(WallpaperWindow w)
    {
        try { w.Close(); w.Dispose(); }
        catch (Exception ex) { Log.Warn("Cerrando ventana: " + ex.Message); }
    }

    // ---------- Recuperación: Explorer reiniciado, WorkerW destruida, ventanas perdidas ----------

    /// <summary>Cada 3 s comprueba que las ventanas sigan incrustadas en el escritorio; si no, las recrea.</summary>
    private async Task HealthCheckAsync()
    {
        if (_recovering || _windows.Count == 0) return;
        bool healthy = _desktop.IsReady && _windows.Values.All(w => w.IsAlive && w.IsAttachedTo(_desktop));
        if (healthy) return;

        if (_windows.Values.Any(w => !w.IsAlive))
        {
            Log.Warn("Una ventana de wallpaper se destruyó (¿Explorer se reinició?); recreando");
            await RecreateAllAsync();
            return;
        }
        // Las ventanas siguen vivas pero cambió la WorkerW/Progman: alcanza con volver a incrustarlas.
        Log.Warn("Cambió la ventana del escritorio; reincrustando wallpapers");
        try
        {
            _desktop.Initialize();
            foreach (var w in _windows.Values) w.Reattach(_desktop);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo reincrustar; recreando", ex);
            await RecreateAllAsync();
        }
    }

    /// <summary>Explorer se reinició (TaskbarCreated): las ventanas hijas de la WorkerW anterior se destruyeron.</summary>
    public async void OnExplorerRestarted()
    {
        Log.Info("Explorer se reinició; recreando wallpapers");
        await Task.Delay(1500); // dar tiempo a que Explorer cree Progman/WorkerW
        await RecreateAllAsync();
    }

    private async Task RecreateAllAsync()
    {
        if (_recovering) return;
        _recovering = true;
        await _gate.WaitAsync();
        try
        {
            var projects = _windows.ToDictionary(kv => kv.Key, kv => kv.Value.Project);
            foreach (var w in _windows.Values) DisposeWindow(w);
            _windows.Clear();

            try { await WaitForDesktopAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception ex) { Log.Error("El escritorio no volvió", ex); return; }
            CurrentMonitors = Monitors.GetAll();

            foreach (var (id, project) in projects)
            {
                if (project == null || CurrentMonitors.All(m => m.Id != id)) continue;
                try { await ApplyCoreAsync(project, id, save: false); }
                catch (Exception ex) { Log.Error($"No se pudo recrear el wallpaper de {id}", ex); }
            }
        }
        finally
        {
            _gate.Release();
            _recovering = false;
        }
        StateChanged?.Invoke();
    }

    // ---------- Cambios del sistema ----------

    private static bool OnUiThread(Action again)
    {
        if (Program.UiContext != null && SynchronizationContext.Current != Program.UiContext)
        {
            Program.UiContext.Post(_ => again(), null);
            return false;
        }
        return true;
    }

    private async void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!OnUiThread(() => OnDisplaySettingsChanged(sender, e))) return;
        await Task.Delay(800); // Windows tarda en terminar de reacomodar los monitores
        Log.Info("Cambió la configuración de pantallas");

        await _gate.WaitAsync();
        try
        {
            CurrentMonitors = Monitors.GetAll();
            _desktop.Initialize();

            foreach (var id in _windows.Keys.ToList())
            {
                var m = CurrentMonitors.FirstOrDefault(x => x.Id == id);
                if (m == null)
                {
                    // Monitor desconectado: se cierra la ventana pero se conserva la asignación guardada.
                    if (_windows.Remove(id, out var w)) DisposeWindow(w);
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
                var p = Library.FindByFolder(folder) ?? WallpaperProject.Load(folder, WallpaperSource.Local);
                if (p != null)
                {
                    try { await ApplyCoreAsync(p, id, save: false); }
                    catch (Exception ex) { Log.Error("Restaurar", ex); }
                }
            }
        }
        finally { _gate.Release(); }
        StateChanged?.Invoke();
    }

    private async void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (!OnUiThread(() => OnSessionSwitch(sender, e))) return;
        // Pantalla bloqueada o sesión desconectada: no tiene sentido renderizar.
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            _sessionLocked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _sessionLocked = false;
        else return;
        await UpdatePauseStatesAsync();
    }

    private async void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (!OnUiThread(() => OnPowerModeChanged(sender, e))) return;
        if (e.Mode == PowerModes.Resume)
        {
            // Al volver de suspensión, Explorer a veces recrea el escritorio.
            await Task.Delay(2000);
            await HealthCheckAsync();
        }
        var battery = IsOnBattery();
        if (battery == _onBattery) return;
        _onBattery = battery;
        Log.Info(battery ? "Funcionando con batería" : "Conectado a la corriente");
        await UpdatePauseStatesAsync();
        StateChanged?.Invoke();
    }

    private static bool IsOnBattery()
    {
        try { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; }
        catch { return false; }
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _healthTimer.Stop();
        _healthTimer.Dispose();
        _watcher.Dispose();
        foreach (var w in _windows.Values) DisposeWindow(w);
        _windows.Clear();
        DesktopHost.RefreshStaticWallpaper();
    }
}
