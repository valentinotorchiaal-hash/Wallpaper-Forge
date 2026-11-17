using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WallpaperForge.Core;
using WallpaperForge.Rendering;

namespace WallpaperForge.UI;

/// <summary>Ventana de la biblioteca. La interfaz es HTML (wwwroot/ui) dentro de un WebView2.</summary>
public sealed class MainWindow : Form
{
    public const string UiHost = "app.wallpaperforge.local";
    public const string MediaHost = "media.wallpaperforge.local";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Bridge _bridge;

    public MainWindow(WallpaperManager manager, OtherAppsService otherApps)
    {
        Text = "Wallpaper Forge";
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 18, 22);
        Icon = AppIcon.Get();
        Controls.Add(_web);
        _bridge = new Bridge(manager, otherApps, this);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await WebViewEnvironment.GetAsync();
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Program.DevMode;
            core.Settings.AreDefaultContextMenusEnabled = Program.DevMode;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(UiHost, AppPaths.UiDir, CoreWebView2HostResourceAccessKind.DenyCors);

            // Previews y archivos de wallpapers: https://media.wallpaperforge.local/?path=C:\...
            core.AddWebResourceRequestedFilter($"https://{MediaHost}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnMediaRequested;

            core.WebMessageReceived += async (_, e) =>
            {
                // Archivos arrastrados a la ventana: WebView2 entrega sus rutas reales como objetos adicionales.
                var paths = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).ToList();
                await _bridge.HandleAsync(e.WebMessageAsJson, paths);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
            };
            core.Navigate($"https://{UiHost}/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "Falta Microsoft Edge WebView2 Runtime. Instalalo desde https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "Wallpaper Forge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo iniciar la interfaz", ex);
            MessageBox.Show(this, ex.Message, "Wallpaper Forge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnMediaRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = _web.CoreWebView2.Environment;
        try
        {
            var uri = new Uri(e.Request.Uri);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var path = query["path"];
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !_bridge.CanServe(path))
            {
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            var length = new FileInfo(path).Length;
            var mime = MimeOf(path);
            var range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
            if (TryParseRange(range, length, out long start, out long end))
            {
                // Pedido parcial (los <video> lo usan para leer sólo el principio o saltar): 206 con un bloque.
                const long MaxChunk = 4 * 1024 * 1024;
                end = Math.Min(end, start + MaxChunk - 1);
                var buffer = new byte[end - start + 1];
                using (var fs = File.OpenRead(path))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    fs.ReadExactly(buffer);
                }
                e.Response = env.CreateWebResourceResponse(new MemoryStream(buffer), 206, "Partial Content",
                    $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *\r\nAccept-Ranges: bytes\r\nContent-Range: bytes {start}-{end}/{length}\r\nContent-Length: {buffer.Length}");
                return;
            }
            e.Response = env.CreateWebResourceResponse(File.OpenRead(path), 200, "OK",
                $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *\r\nAccept-Ranges: bytes\r\nContent-Length: {length}\r\nCache-Control: max-age=3600");
        }
        catch (Exception ex)
        {
            Log.Warn("Error sirviendo archivo a la UI: " + ex.Message);
            e.Response = env.CreateWebResourceResponse(null, 500, "Error", "");
        }
    }

    /// <summary>Interpreta "bytes=inicio-fin" (fin opcional). Sólo un rango.</summary>
    internal static bool TryParseRange(string? header, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || length <= 0) return false;
        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var a = spec[..dash].Trim();
        var b = spec[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            // "bytes=-500": los últimos 500 bytes
            if (!long.TryParse(b, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix);
            return true;
        }
        if (!long.TryParse(a, out start) || start >= length) return false;
        if (b.Length > 0 && long.TryParse(b, out var e2)) end = Math.Min(e2, length - 1);
        return end >= start;
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avif" => "image/avif",
        _ => "application/octet-stream",
    };

    /// <summary>Con la ventana oculta en la bandeja, la interfaz se suspende: no consume CPU y libera memoria.</summary>
    protected override async void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        var core = _web.CoreWebView2;
        if (core == null) return;
        try
        {
            if (!Visible)
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                await Task.Delay(200); // el control primero se marca como no visible
                if (!Visible) await core.TrySuspendAsync();
            }
            else
            {
                if (core.IsSuspended) core.Resume();
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            }
        }
        catch (Exception ex) { Log.Warn("Suspender interfaz: " + ex.Message); }
    }

    public void PostEvent(string name, object? data) => _bridge.PostEvent(name, data);

    internal void PostJson(string json)
    {
        var core = _web.CoreWebView2;
        if (core == null || core.IsSuspended) return; // al volver a mostrarse, la interfaz pide el estado de nuevo
        core.PostWebMessageAsJson(json);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Cerrar la ventana no cierra la app: queda en la bandeja.
        if (e.CloseReason == CloseReason.UserClosing && !Program.IsExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void BringToFront2()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }
}
