using WallpaperForge.Core;

namespace WallpaperForge.UI;

/// <summary>Contexto principal de la app: vive en la bandeja del sistema.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly WallpaperManager _manager;
    private readonly MessageWindow _messages;
    private MainWindow? _main;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _muteItem;

    public TrayApp(bool showWindow)
    {
        var settings = SettingsStore.Load();
        _manager = new WallpaperManager(settings);

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
        if (showWindow) ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        _main ??= new MainWindow(_manager);
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
