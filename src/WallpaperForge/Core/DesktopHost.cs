using System.Drawing;

namespace WallpaperForge.Core;

/// <summary>
/// Coloca ventanas detrás de los íconos del escritorio.
///
/// Windows 10 / 11 (hasta 23H2): se le envía 0x052C a Progman, que crea una ventana WorkerW
/// detrás de SHELLDLL_DefView; nuestras ventanas pasan a ser hijas de esa WorkerW.
///
/// Windows 11 24H2 en adelante: Progman tiene WS_EX_NOREDIRECTIONBITMAP y SHELLDLL_DefView es
/// hija de Progman. La WorkerW se crea como hija de Progman. Nuestras ventanas se agregan como
/// hijas de Progman (WS_CHILD + WS_EX_LAYERED), justo debajo de SHELLDLL_DefView y encima de WorkerW.
/// </summary>
public sealed class DesktopHost
{
    public IntPtr Progman { get; private set; }
    public IntPtr WorkerW { get; private set; }
    public IntPtr ShellView { get; private set; }

    /// <summary>true en Windows 11 24H2+ ("raised desktop" con DefView en capas).</summary>
    public bool IsLayeredShellView { get; private set; }

    public bool IsReady => Progman != IntPtr.Zero && WorkerW != IntPtr.Zero && Native.IsWindow(WorkerW);

    /// <summary>La ventana a la que se incrustan los wallpapers (Progman en 24H2+, WorkerW antes).</summary>
    public IntPtr WallpaperParent => IsLayeredShellView ? Progman : WorkerW;

    public void Initialize()
    {
        Progman = Native.FindWindow("Progman", null);
        if (Progman == IntPtr.Zero)
            throw new InvalidOperationException("No se encontró la ventana Progman del escritorio.");

        IsLayeredShellView = (Native.GetExStyle(Progman) & Native.WS_EX_NOREDIRECTIONBITMAP) != 0;

        // Le pide a Progman que cree la WorkerW detrás de los íconos (si ya existe, no hace nada).
        Native.SendMessageTimeout(Progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), Native.SMTO_NORMAL, 1000, out _);

        WorkerW = IntPtr.Zero;
        ShellView = IntPtr.Zero;

        if (IsLayeredShellView)
        {
            ShellView = Native.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            WorkerW = Native.FindWindowEx(Progman, IntPtr.Zero, "WorkerW", null);
        }
        else
        {
            // Buscar la ventana de nivel superior que contiene SHELLDLL_DefView; la WorkerW buscada es la siguiente.
            Native.EnumWindows((top, _) =>
            {
                var view = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view != IntPtr.Zero)
                {
                    ShellView = view;
                    WorkerW = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                }
                return true;
            }, IntPtr.Zero);

            // Algunas builds dejan la WorkerW como hija de Progman aunque no tengan el estilo nuevo.
            if (WorkerW == IntPtr.Zero)
                WorkerW = Native.FindWindowEx(Progman, IntPtr.Zero, "WorkerW", null);
        }

        // Último recurso: usar Progman directamente.
        if (WorkerW == IntPtr.Zero)
            WorkerW = Progman;

        Log.Info($"Escritorio: Progman={Progman:X} WorkerW={WorkerW:X} DefView={ShellView:X} 24H2={IsLayeredShellView}");
    }

    /// <summary>Convierte <paramref name="hwnd"/> en hija del escritorio y la ubica sobre <paramref name="screenBounds"/>.</summary>
    public void Attach(IntPtr hwnd, Rectangle screenBounds)
    {
        if (!IsReady) Initialize();

        long style = Native.GetStyle(hwnd);
        style &= ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME);
        style |= Native.WS_CHILD;
        Native.SetStyle(hwnd, style);

        long ex = Native.GetExStyle(hwnd);
        ex &= ~Native.WS_EX_APPWINDOW;
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;

        IntPtr parent;
        if (IsLayeredShellView)
        {
            ex |= Native.WS_EX_LAYERED;
            Native.SetExStyle(hwnd, ex);
            Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
            parent = Progman;
        }
        else
        {
            Native.SetExStyle(hwnd, ex);
            parent = WorkerW;
        }

        Native.SetParent(hwnd, parent);
        Reposition(hwnd, screenBounds);

        if (IsLayeredShellView)
        {
            // Debajo de los íconos...
            if (ShellView != IntPtr.Zero)
                Native.SetWindowPos(hwnd, ShellView, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            // ...y la WorkerW (fondo estático de Windows) al fondo de todo.
            if (WorkerW != Progman && Native.GetWindow(Progman, Native.GW_HWNDLAST) != WorkerW)
                Native.SetWindowPos(WorkerW, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>Ubica una ventana ya adjunta en coordenadas de pantalla (se traducen a coordenadas del padre).</summary>
    public void Reposition(IntPtr hwnd, Rectangle screenBounds)
    {
        var parent = WallpaperParent;
        var p = new Native.POINT { X = screenBounds.X, Y = screenBounds.Y };
        Native.ScreenToClient(parent, ref p);
        Native.SetWindowPos(hwnd, IntPtr.Zero, p.X, p.Y, screenBounds.Width, screenBounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Fuerza a Windows a redibujar el fondo estático (al salir de la app).</summary>
    public static void RefreshStaticWallpaper()
    {
        Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, null, Native.SPIF_UPDATEINIFILE);
    }
}
