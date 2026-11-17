using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using WallpaperForge.Core;
using WallpaperForge.Scene;
using WallpaperForge.Wallpapers;

namespace WallpaperForge.Rendering;

/// <summary>
/// Ventana sin bordes que se incrusta detrás de los íconos del escritorio en un monitor y
/// muestra un wallpaper (web, video o escena) dentro de un WebView2.
/// </summary>
public sealed class WallpaperWindow : Form
{
    private CoreWebView2Controller? _controller;
    private readonly List<string> _injectedScripts = new();
    // Las pausas se aplican de a una: si una pausa y una reanudación se pisan, la ventana podía quedar
    // oculta y suspendida aunque el monitor figure "reproduciendo" (y ningún cambio de fondo la arreglaba).
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private bool _paused;          // estado pedido
    private bool _appliedPaused;   // estado aplicado de verdad al WebView2
    private bool _attached;

    public MonitorInfo Monitor { get; private set; }
    public WallpaperProject? Project { get; private set; }
    public bool IsPausedByWatcher => _paused;

    /// <summary>La ventana y su WebView2 siguen existiendo (Explorer puede destruirlas al reiniciarse).</summary>
    public bool IsAlive => !IsDisposed && IsHandleCreated && Native.IsWindow(Handle) && _controller != null;

    public bool IsAttachedTo(DesktopHost desktop)
        => desktop.WallpaperParent != IntPtr.Zero && Native.GetParent(Handle) == desktop.WallpaperParent;

    public WallpaperWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        Bounds = monitor.Bounds;
        Text = "Wallpaper Forge - " + monitor.Id;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public async Task InitializeAsync(DesktopHost desktop)
    {
        CreateHandle();
        var env = await WebViewEnvironment.GetAsync();
        _controller = await env.CreateCoreWebView2ControllerAsync(Handle);
        _controller.DefaultBackgroundColor = Color.Black;
        _controller.ShouldDetectMonitorScaleChanges = false;
        _controller.RasterizationScale = Monitor.Scale;
        var core = _controller.CoreWebView2;
        var st = core.Settings;
        st.AreDefaultContextMenusEnabled = false;
        st.AreDevToolsEnabled = Program.DevMode;
        st.IsStatusBarEnabled = false;
        st.IsZoomControlEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = false;
        st.IsPinchZoomEnabled = false;
        st.IsSwipeNavigationEnabled = false;
        st.IsGeneralAutofillEnabled = false;
        st.IsPasswordAutosaveEnabled = false;

        // Los wallpapers no pueden abrir ventanas nuevas.
        core.NewWindowRequested += (_, e) => e.Handled = true;
        // Ni navegar fuera de archivos locales (los links se ignoran).
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
                !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) &&
                !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Navegación bloqueada en wallpaper: {e.Uri}");
                e.Cancel = true;
            }
        };
        // Si el monitor está en pausa, se vuelve a suspender cuando termina de cargar.
        core.NavigationCompleted += (_, _) =>
        {
            if (_paused) _ = SetPausedAsync(true, force: true);
        };
        core.ProcessFailed += OnProcessFailed;

        // Primero se incrusta en el escritorio y después se muestra, para que no aparezca un instante encima de todo.
        desktop.Attach(Handle, Monitor.Bounds);
        _attached = true;
        Show();
        // WinForms puede reaplicar su posición en coordenadas de pantalla al mostrarse: se vuelve a ubicar.
        desktop.Reposition(Handle, Monitor.Bounds);
        ResizeController();
        // El WebView2 se crea cuando la ventana todavía no se ve y nace oculto: si no se lo muestra acá,
        // el monitor sigue con el fondo de Windows hasta que algo lo pause y lo reanude.
        _controller.IsVisible = true;
    }

    private DateTime _lastCrashReload = DateTime.MinValue;

    /// <summary>Si el proceso del wallpaper se cae, se recarga; si se cae el navegador, la ventana queda "muerta" y se recrea.</summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error($"WebView2 del monitor {Monitor.Id} falló: {e.ProcessFailedKind} ({e.Reason})");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _controller = null; // el chequeo de salud del administrador recrea la ventana
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                // Evita un bucle si el wallpaper se cae siempre al cargar.
                if (DateTime.UtcNow - _lastCrashReload < TimeSpan.FromSeconds(30)) return;
                _lastCrashReload = DateTime.UtcNow;
                try { _controller?.CoreWebView2.Reload(); } catch { }
                break;
        }
    }

    public void UpdateMonitor(MonitorInfo monitor, DesktopHost desktop)
    {
        Monitor = monitor;
        if (_controller != null) _controller.RasterizationScale = monitor.Scale;
        if (_attached) desktop.Reposition(Handle, monitor.Bounds);
        ResizeController();
    }

    public void Reattach(DesktopHost desktop)
    {
        desktop.Attach(Handle, Monitor.Bounds);
        ResizeController();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ResizeController();
    }

    private void ResizeController()
    {
        if (_controller != null) _controller.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
    }

    /// <summary>Carga un wallpaper. Devuelve advertencias (p. ej. partes de una escena no soportadas).</summary>
    public async Task<List<string>> LoadAsync(WallpaperProject project, AppSettings settings)
    {
        if (_controller == null) throw new InvalidOperationException("Ventana no inicializada");
        var core = _controller.CoreWebView2;
        var warnings = new List<string>();
        Project = project;

        if (core.IsSuspended) core.Resume(); // no se puede navegar suspendido
        // Si no está en pausa, se asegura que se vea (si está en pausa, NavigationCompleted la vuelve a suspender).
        if (!_paused && _pauseGate.CurrentCount > 0 && !_controller.IsVisible)
        {
            Log.Warn($"Monitor {Monitor.Id}: el wallpaper estaba oculto sin estar en pausa; se vuelve a mostrar");
            _controller.IsVisible = true;
            _appliedPaused = false;
        }

        foreach (var id in _injectedScripts) core.RemoveScriptToExecuteOnDocumentCreated(id);
        _injectedScripts.Clear();

        var playback = PropertyValues.Playback(settings, project);
        var init = new JsonObject
        {
            ["user"] = PropertyValues.BuildUserProperties(project, settings),
            ["general"] = new JsonObject { ["fps"] = settings.FpsLimit },
            ["directories"] = PropertyValues.BuildDirectories(project, settings),
            ["playback"] = new JsonObject
            {
                ["volume"] = playback.Volume,
                ["rate"] = playback.Rate,
                ["fit"] = playback.Fit.ToString().ToLowerInvariant(),
            },
            ["monitor"] = new JsonObject { ["id"] = Monitor.Id, ["index"] = Monitor.Index },
        };
        var shim = await File.ReadAllTextAsync(Path.Combine(AppPaths.RuntimeDir, "we-shim.js"));
        _injectedScripts.Add(await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.__WF_INIT = " + init.ToJsonString() + ";\n" + shim));

        string url;
        switch (project.Type)
        {
            case WallpaperType.Web:
                url = WebViewEnvironment.FileUrl(project.EntryPath ?? Path.Combine(project.Folder, "index.html"));
                break;
            case WallpaperType.Image:
                url = WebViewEnvironment.FileUrl(Path.Combine(AppPaths.RuntimeDir, "image.html"))
                      + "?src=" + Uri.EscapeDataString(WebViewEnvironment.FileUrl(project.EntryPath!));
                break;
            case WallpaperType.Video:
                url = WebViewEnvironment.FileUrl(Path.Combine(AppPaths.RuntimeDir, "video.html"))
                      + "?src=" + Uri.EscapeDataString(WebViewEnvironment.FileUrl(project.EntryPath!));
                break;
            case WallpaperType.Scene:
                var weInstall = Library.ResolveWallpaperEngineInstall(settings);
                var result = await Task.Run(() => SceneConverter.ConvertCached(
                    project.Folder, weInstall, project.RawProject, project.EntryFile ?? "scene.json"));
                warnings.AddRange(result.Warnings);
                url = WebViewEnvironment.FileUrl(Path.Combine(AppPaths.RuntimeDir, "scene.html"))
                      + "?data=" + Uri.EscapeDataString(WebViewEnvironment.FileUrl(result.DataScriptPath));
                if (project.PreviewPath != null)
                    url += "&preview=" + Uri.EscapeDataString(WebViewEnvironment.FileUrl(project.PreviewPath));
                break;
            default:
                throw new NotSupportedException($"Tipo de wallpaper no soportado todavía: {project.Type}");
        }

        SetMuted(settings.Muted);
        Log.Info($"Monitor {Monitor.Id}: cargando '{project.Title}' ({project.Type}) → {url}");
        core.Navigate(url);
        return warnings;
    }

    public void Unload()
    {
        Project = null;
        if (_controller == null) return;
        if (_controller.CoreWebView2.IsSuspended) _controller.CoreWebView2.Resume();
        _controller.CoreWebView2.Navigate("about:blank");
    }

    public void SetMuted(bool muted)
    {
        if (_controller != null) _controller.CoreWebView2.IsMuted = muted;
    }

    public Task ApplyUserPropertiesAsync(JsonObject changed) =>
        ExecAsync($"window.__wf && window.__wf.applyUser({changed.ToJsonString()})");

    public Task ApplyGeneralAsync(int fps) =>
        ExecAsync($"window.__wf && window.__wf.applyGeneral({{fps:{fps}}})");

    public Task ApplyPlaybackAsync(PlaybackOptions o) =>
        ExecAsync($"window.__wf && window.__wf.applyPlayback({{volume:{o.Volume},rate:{o.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture)},fit:'{o.Fit.ToString().ToLowerInvariant()}'}})");

    /// <summary>Pausa o reanuda el wallpaper. En pausa el WebView2 se suspende y no consume CPU/GPU.</summary>
    public async Task SetPausedAsync(bool paused, bool force = false)
    {
        if (_controller == null) return;
        _paused = paused;
        await _pauseGate.WaitAsync();
        try
        {
            // Siempre se termina en el último estado pedido: si mientras se pausaba llegó una reanudación
            // (o al revés), se vuelve a aplicar.
            while (_controller != null && (force || _appliedPaused != _paused))
            {
                force = false;
                bool target = _paused;
                await ApplyPauseAsync(target);
                _appliedPaused = target;
            }
        }
        finally { _pauseGate.Release(); }
    }

    private async Task ApplyPauseAsync(bool paused)
    {
        if (_controller == null) return;
        var core = _controller.CoreWebView2;
        try
        {
            if (paused)
            {
                await ExecAsync("window.__wf && window.__wf.setPaused(true)");
                if (_controller == null) return;
                _controller.IsVisible = false;   // detiene requestAnimationFrame y el compositor
                try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
                try { await core.TrySuspendAsync(); } catch (Exception ex) { Log.Warn("TrySuspend: " + ex.Message); }
            }
            else
            {
                if (core.IsSuspended) core.Resume();
                try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
                _controller.IsVisible = true;
                await ExecAsync("window.__wf && window.__wf.setPaused(false)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo cambiar la pausa del monitor {Monitor.Id}", ex);
        }
    }

    /// <summary>
    /// Red de seguridad: si el monitor no está en pausa pero el WebView2 quedó oculto o suspendido,
    /// lo vuelve a mostrar. Devuelve true si tuvo que arreglar algo.
    /// </summary>
    public bool RepairIfStuck()
    {
        if (_controller == null || _paused || _pauseGate.CurrentCount == 0) return false;
        var core = _controller.CoreWebView2;
        if (_controller.IsVisible && !core.IsSuspended) return false;
        Log.Warn($"Monitor {Monitor.Id}: el wallpaper estaba oculto sin estar en pausa; se vuelve a mostrar");
        try
        {
            if (core.IsSuspended) core.Resume();
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
            _controller.IsVisible = true;
            _appliedPaused = false;
            _ = ExecAsync("window.__wf && window.__wf.setPaused(false)");
        }
        catch (Exception ex) { Log.Warn($"No se pudo volver a mostrar el monitor {Monitor.Id}: {ex.Message}"); }
        return true;
    }

    private async Task ExecAsync(string script)
    {
        if (_controller == null) return;
        try { await _controller.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception ex) { Log.Warn($"ExecuteScript falló ({Monitor.Id}): {ex.Message}"); }
    }

    public bool IsSuspended => _controller?.CoreWebView2.IsSuspended == true;

    /// <summary>Evalúa JavaScript y devuelve el resultado como JSON (o null si falla).</summary>
    public async Task<string?> EvalAsync(string script)
    {
        if (_controller == null) return null;
        try { return await _controller.CoreWebView2.ExecuteScriptAsync(script); }
        catch { return null; }
    }

    public void OpenDevTools() => _controller?.CoreWebView2.OpenDevToolsWindow();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller?.Close();
            _controller = null;
        }
        base.Dispose(disposing);
    }
}
