using WallpaperForge.Core;

namespace WallpaperForge.UI;

/// <summary>Contexto principal de la app: vive en la bandeja del sistema.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly WallpaperManager _manager;
    private readonly OtherAppsService _otherApps;
    private readonly MessageWindow _messages;
    private MainWindow? _main;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _muteItem;

    public TrayApp(bool showWindow)
    {
        var settings = SettingsStore.Load();
        _manager = new WallpaperManager(settings);
        _otherApps = new OtherAppsService(_manager);

        _pauseItem = new ToolStripMenuItem("Pausar todo", null, async (_, _) =>
            await _manager.SetPausedByUserAsync(!_manager.PausedByUser));
        _muteItem = new ToolStripMenuItem("Silenciar", null, (_, _) => _manager.SetMuted(!_manager.Settings.Muted));

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Abrir biblioteca", null, (_, _) => ShowMainWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_muteItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Salir", null, (_, _) => Exit()));
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Get(),
            Text = "Wallpaper Forge",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };

        // Ventana oculta para recibir "TaskbarCreated" (Explorer se reinició).
        _messages = new MessageWindow(() => _manager.OnExplorerRestarted());

        _manager.StateChanged += RefreshMenu;
        _manager.LoadWarnings += (title, warnings) =>
        {
            if (_main?.Visible == true) return; // la UI ya lo muestra
            _tray.ShowBalloonTip(5000, "Wallpaper Forge",
                $"\"{title}\" se cargó con limitaciones ({warnings.Count} elementos no soportados todavía).", ToolTipIcon.Info);
        };

        _ = StartAsync(showWindow || !settings.StartMinimized || settings.Assignments.Count == 0);
    }

    private async Task StartAsync(bool showWindow)
    {
        try
        {
            await _manager.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Error al iniciar", ex);
            MessageBox.Show("No se pudo iniciar Wallpaper Forge:\n" + ex.Message, "Wallpaper Forge",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        var s = _manager.Settings;
        if (!s.FirstRunCompleted)
        {
            // Primer inicio (o primera vez con la v0.2): arrancar con Windows queda activado.
            // La v0.1 guardaba "false" por defecto, por eso se fuerza en vez de leer el valor viejo.
            s.StartWithWindows = true;
            StartupManager.Apply(true);
            s.FirstRunCompleted = true;
            SettingsStore.Save(s);
        }
        else
        {
            StartupManager.RefreshPathIfEnabled();
        }

        if (s.CloseOtherWallpaperApps) _otherApps.StartGuard();
        if (showWindow) ShowMainWindow();
        if (!s.CompetitorsPromptShown) await OfferToReplaceOtherAppsAsync();
    }

    /// <summary>Si hay otro programa de fondos (p. ej. Wallpaper Engine), ofrece cerrarlo y desactivar su inicio.</summary>
    private async Task OfferToReplaceOtherAppsAsync()
    {
        CompetitorReport report;
        try { report = await _otherApps.DetectAsync(); }
        catch (Exception ex) { Log.Error("Detección de otros programas", ex); return; }

        _manager.Settings.CompetitorsPromptShown = true;
        SettingsStore.Save(_manager.Settings);
        if (!report.Any) return;

        var names = string.Join(" y ", report.AppNames);
        var answer = MessageBox.Show(
            $"Se detectó {names} en esta PC.\n\n" +
            "¿Querés que Wallpaper Forge lo reemplace?\n\n" +
            "• Se cierra ahora y se desactiva su inicio automático con Windows.\n" +
            "• Los wallpapers que tenía en cada monitor se pasan a Wallpaper Forge.\n" +
            "• No se desinstala nada y se puede revertir desde Configuración → Otros programas de fondos.\n\n" +
            "Puede aparecer un aviso de Windows pidiendo permisos de administrador.",
            "Wallpaper Forge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        var result = await _otherApps.DisableAsync();
        var msg = new System.Text.StringBuilder();
        if (result.Closed.Count > 0) msg.AppendLine("Cerrado: " + string.Join(", ", result.Closed));
        if (result.Disabled.Count > 0) msg.AppendLine("Inicio automático desactivado:\n  " + string.Join("\n  ", result.Disabled));
        if (result.Imported > 0) msg.AppendLine($"Wallpapers importados: {result.Imported}");
        if (result.ElevationDeclined) msg.AppendLine("\nNo se aceptaron los permisos de administrador: algunas entradas siguen activas.");
        if (result.Errors.Count > 0) msg.AppendLine("\nErrores:\n  " + string.Join("\n  ", result.Errors));
        if (msg.Length == 0) msg.Append("No había nada para cambiar.");
        _tray.ShowBalloonTip(8000, "Wallpaper Forge", msg.ToString().Length > 250 ? "Listo. Detalles en Configuración." : msg.ToString(), ToolTipIcon.Info);
        Log.Info("Reemplazo de otros programas:\n" + msg);
    }

    public void ShowMainWindow()
    {
        _main ??= new MainWindow(_manager, _otherApps);
        _main.BringToFront2();
    }

    private void RefreshMenu()
    {
        _pauseItem.Text = _manager.PausedByUser ? "Reanudar" : "Pausar todo";
        _muteItem.Checked = _manager.Settings.Muted;
    }

    private void Exit()
    {
        Program.IsExiting = true;
        _tray.Visible = false;
        _main?.Close();
        _otherApps.Dispose();
        _manager.Dispose();
        _messages.DestroyHandle();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _main?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Ventana invisible que escucha mensajes globales del shell.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly uint _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        private readonly Action _onTaskbarCreated;

        public MessageWindow(Action onTaskbarCreated)
        {
            _onTaskbarCreated = onTaskbarCreated;
            CreateHandle(new CreateParams { Caption = "WallpaperForge.Messages" });
        }

        protected override void WndProc(ref Message m)
        {
            if ((uint)m.Msg == _taskbarCreated)
                _onTaskbarCreated();
            base.WndProc(ref m);
        }
    }
}
