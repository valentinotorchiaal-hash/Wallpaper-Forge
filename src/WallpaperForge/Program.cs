using System.IO.Pipes;
using WallpaperForge.Core;
using WallpaperForge.UI;

namespace WallpaperForge;

internal static class Program
{
    private const string MutexName = "WallpaperForge.SingleInstance.v1";
    private const string PipeName = "WallpaperForge.Activate.v1";

    public static bool DevMode { get; private set; }
    public static bool IsExiting { get; set; }
    public static SynchronizationContext? UiContext { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        DevMode = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);

        int selfTestIndex = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (selfTestIndex >= 0)
        {
            Environment.Exit(RunSelfTest(selfTestIndex + 1 < args.Length ? args[selfTestIndex + 1] : "selftest-result.json"));
            return;
        }
        bool fromStartup = args.Contains("--startup", StringComparer.OrdinalIgnoreCase);

        Log.Init();

        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Hay otra instancia abierta. Si es OTRA copia del programa (otra carpeta, otra versión),
            // se cierra y esta toma su lugar: así nunca queda corriendo una versión vieja.
            if (!TakeOverFromOtherCopies(mutex))
            {
                // Es esta misma copia: se le pide que muestre su ventana y se sale.
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(1500);
                    client.WriteByte(1);
                }
                catch { }
                return;
            }
        }
        Log.Info($"Wallpaper Forge {AppVersion.Text} iniciando desde {Environment.ProcessPath} (Windows {Environment.OSVersion.Version})");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error("Excepción no controlada (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Excepción no controlada", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Tarea sin observar", e.Exception); e.SetObserved(); };

        var context = new TrayApp(showWindow: !fromStartup);
        UiContext = SynchronizationContext.Current;
        StartActivationListener(context);
        Application.Run(context);
        Log.Info("Wallpaper Forge cerrado");
    }

    /// <summary>
    /// Cierra las instancias de Wallpaper Forge que corren desde OTRA ubicación (p. ej. una versión vieja en bin\Debug
    /// o en la carpeta del zip) y toma el control. Devuelve false si la otra instancia es esta misma copia.
    /// </summary>
    private static bool TakeOverFromOtherCopies(Mutex mutex)
    {
        var me = Environment.ProcessPath ?? "";
        var others = System.Diagnostics.Process.GetProcessesByName("WallpaperForge")
            .Where(p => p.Id != Environment.ProcessId).ToList();
        var different = new List<System.Diagnostics.Process>();
        foreach (var p in others)
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path == null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(me), StringComparison.OrdinalIgnoreCase))
                different.Add(p);
        }
        if (different.Count == 0) return false;

        foreach (var p in different)
        {
            try
            {
                Log.Info($"Cerrando otra copia de Wallpaper Forge: {SafePath(p)} (pid {p.Id})");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception ex) { Log.Warn($"No se pudo cerrar la otra copia (pid {p.Id}): {ex.Message}"); }
        }
        try
        {
            // El mutex de la instancia cerrada queda "abandonado": al tomarlo, pasa a ser nuestro.
            return mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException) { return true; }
    }

    private static string SafePath(System.Diagnostics.Process p)
    {
        try { return p.MainModule?.FileName ?? "?"; } catch { return "?"; }
    }

    /// <summary>Corre el autodiagnóstico con su propio bucle de mensajes (no usa la instancia única ni la configuración).</summary>
    private static int RunSelfTest(string output)
    {
        Log.Init();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        int code = 1;
        var ctx = new ApplicationContext();
        using var host = new Control(); // instala el SynchronizationContext de WinForms
        host.CreateControl();
        UiContext = SynchronizationContext.Current;
        var timeout = new System.Windows.Forms.Timer { Interval = 120_000 };
        timeout.Tick += (_, _) => { Log.Error("selftest: tiempo agotado"); ctx.ExitThread(); };
        timeout.Start();
        host.BeginInvoke(async () =>
        {
            try { code = await SelfTest.RunAsync(Path.GetFullPath(output)); }
            catch (Exception ex) { Log.Error("selftest", ex); }
            finally { ctx.ExitThread(); }
        });
        Application.Run(ctx);
        return code;
    }

    /// <summary>Escucha pedidos de una segunda instancia para traer la ventana al frente.</summary>
    private static void StartActivationListener(TrayApp app)
    {
        var ctx = SynchronizationContext.Current!;
        _ = Task.Run(async () =>
        {
            while (!IsExiting)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    server.ReadByte();
                    ctx.Post(_ => app.ShowMainWindow(), null);
                }
                catch (Exception ex)
                {
                    Log.Warn("Pipe de activación: " + ex.Message);
                    await Task.Delay(1000);
                }
            }
        });
    }
}
