using System.Drawing;
using System.Runtime.InteropServices;

namespace WallpaperForge.Core;

/// <summary>Un monitor físico. Las coordenadas están en píxeles físicos (la app es Per-Monitor DPI V2).</summary>
public sealed record MonitorInfo(
    string Id,          // nombre de dispositivo, p. ej. \\.\DISPLAY1
    int Index,          // 1, 2, 3... según orden de enumeración
    Rectangle Bounds,
    Rectangle WorkArea,
    bool IsPrimary,
    double Scale,       // 1.0 = 96 DPI
    IntPtr Handle);

public static class Monitors
{
    public static List<MonitorInfo> GetAll()
    {
        var list = new List<(IntPtr h, Native.MONITORINFOEX info)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi)) list.Add((h, mi));
            return true;
        }, IntPtr.Zero);

        // Orden estable: primero el principal, después de izquierda a derecha.
        var ordered = list
            .OrderByDescending(m => (m.info.dwFlags & 1) != 0)
            .ThenBy(m => m.info.rcMonitor.Left)
            .ThenBy(m => m.info.rcMonitor.Top)
            .ToList();

        var result = new List<MonitorInfo>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (h, mi) = ordered[i];
            double scale = 1.0;
            try
            {
                if (Native.GetDpiForMonitor(h, 0, out uint dx, out _) == 0) scale = dx / 96.0;
            }
            catch { /* shcore no disponible */ }

            result.Add(new MonitorInfo(
                mi.szDevice,
                i + 1,
                ToRect(mi.rcMonitor),
                ToRect(mi.rcWork),
                (mi.dwFlags & 1) != 0,
                scale,
                h));
        }
        return result;
    }

    private static Rectangle ToRect(Native.RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}
