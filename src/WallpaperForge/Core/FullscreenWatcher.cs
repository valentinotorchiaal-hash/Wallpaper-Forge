using System.Diagnostics;
using System.Drawing;

namespace WallpaperForge.Core;

/// <summary>
/// Revisa periódicamente qué monitores están tapados por una ventana en pantalla completa
/// (o maximizada, según <see cref="PauseMode"/>). Cada monitor se evalúa por separado:
/// si hay un juego en pantalla completa en el monitor 2, sólo se pausa el wallpaper del monitor 2.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ApplicationFrameWindow_Hidden",
        "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow", "ForegroundStaging",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "EdgeUiInputTopWndClass",
    };

    private readonly System.Windows.Forms.Timer _timer;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly Dictionary<uint, string> _processNames = new();
    private HashSet<string> _covered = new();

    public Func<IReadOnlyList<MonitorInfo>> MonitorsProvider { get; set; } = () => Monitors.GetAll();
    public Func<AppSettings> SettingsProvider { get; set; } = () => new AppSettings();

    /// <summary>Se dispara con el conjunto de Ids de monitores tapados, sólo cuando cambia.</summary>
    public event Action<IReadOnlySet<string>>? CoveredMonitorsChanged;

    public FullscreenWatcher(int intervalMs = 500)
    {
        _timer = new System.Windows.Forms.Timer { Interval = intervalMs };
        _timer.Tick += (_, _) => Check();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void Check()
    {
        var settings = SettingsProvider();
        HashSet<string> covered = settings.PauseMode == PauseMode.Never
            ? new()
            : Compute(MonitorsProvider(), settings);

        if (!covered.SetEquals(_covered))
        {
            _covered = covered;
            Log.Info("Monitores tapados: " + (covered.Count == 0 ? "ninguno" : string.Join(", ", covered)));
            CoveredMonitorsChanged?.Invoke(covered);
        }
    }

    public HashSet<string> Compute(IReadOnlyList<MonitorInfo> monitors, AppSettings settings)
    {
        var covered = new HashSet<string>();
        if (monitors.Count == 0) return covered;
        bool includeMaximized = settings.PauseMode == PauseMode.FullscreenOrMaximized;
        var ignore = new HashSet<string>(settings.PauseIgnoreProcesses, StringComparer.OrdinalIgnoreCase);
        var shellWindow = Native.GetShellWindow();

        Native.EnumWindows((hwnd, _) =>
        {
            if (covered.Count == monitors.Count) return false; // todos tapados, no hace falta seguir

            if (hwnd == shellWindow || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.IsCloaked(hwnd))
                return true;

            long ex = Native.GetExStyle(hwnd);
            // Overlays transparentes (GeForce Experience, Discord, etc.) no cuentan.
            if ((ex & Native.WS_EX_TRANSPARENT) != 0 || (ex & Native.WS_EX_NOACTIVATE) != 0) return true;

            if (ShellClasses.Contains(Native.ClassNameOf(hwnd))) return true;

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid) return true;
            if (ignore.Count > 0 && ignore.Contains(ProcessName(pid))) return true;

            var r = Native.VisibleBounds(hwnd);
            if (r.Width <= 0 || r.Height <= 0) return true;
            var rect = new Rectangle(r.Left, r.Top, r.Width, r.Height);
            bool zoomed = includeMaximized && Native.IsZoomed(hwnd);

            foreach (var m in monitors)
            {
                if (covered.Contains(m.Id)) continue;
                if (rect.Contains(m.Bounds))
                {
                    covered.Add(m.Id);
                }
                else if (zoomed)
                {
                    // Ventana maximizada: ocupa el área de trabajo del monitor en el que está.
                    var hm = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONULL);
                    if (hm == m.Handle) covered.Add(m.Id);
                }
            }
            return true;
        }, IntPtr.Zero);

        return covered;
    }

    private string ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var n)) return n;
        try { n = Process.GetProcessById((int)pid).ProcessName; } catch { n = ""; }
        if (_processNames.Count > 512) _processNames.Clear();
        _processNames[pid] = n;
        return n;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
