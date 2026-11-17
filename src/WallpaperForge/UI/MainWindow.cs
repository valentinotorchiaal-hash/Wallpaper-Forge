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

            core.WebMessageReceived += async (_, e) => await _bridge.HandleAsync(e.WebMessageAsJson);
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
            var stream = File.OpenRead(path);
            e.Response = env.CreateWebResourceResponse(stream, 200, "OK",
                $"Content-Type: {MimeOf(path)}\r\nCache-Control: max-age=3600");
        }
        catch (Exception ex)
        {
            Log.Warn("Error sirviendo archivo a la UI: " + ex.Message);
            e.Response = env.CreateWebResourceResponse(null, 500, "Error", "");
        }
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
        _ => "application/octet-stream",
    };

    public void PostEvent(string name, object? data) => _bridge.PostEvent(name, data);

    internal void PostJson(string json)
    {
        if (_web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
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
