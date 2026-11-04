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
        bool fromStartup = args.Contains("--startup", StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Ya está abierta: se le pide que muestre su ventana y se sale.
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1500);
                client.WriteByte(1);
            }
            catch { }
            return;
        }

        Log.Init();
        Log.Info($"Wallpaper Forge {typeof(Program).Assembly.GetName().Version} iniciando (Windows {Environment.OSVersion.Version})");

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
