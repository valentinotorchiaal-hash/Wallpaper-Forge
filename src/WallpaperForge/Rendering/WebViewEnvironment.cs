using Microsoft.Web.WebView2.Core;
using WallpaperForge.Core;

namespace WallpaperForge.Rendering;

/// <summary>Un único entorno WebView2 compartido por la UI y todos los wallpapers (un solo proceso de navegador).</summary>
public static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _env;

    public static Task<CoreWebView2Environment> GetAsync()
    {
        return _env ??= Create();
    }

    private static async Task<CoreWebView2Environment> Create()
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            // - Acceso a archivos locales desde file:// (los wallpapers web de Wallpaper Engine lo necesitan).
            // - Autoplay con sonido sin interacción del usuario.
            // - Sin interceptar las teclas multimedia del teclado.
            AdditionalBrowserArguments = string.Join(' ',
                "--allow-file-access-from-files",
                "--autoplay-policy=no-user-gesture-required",
                "--disable-features=HardwareMediaKeyHandling,msSmartScreenProtection"),
        };
        Directory.CreateDirectory(AppPaths.WebViewData);
        try
        {
            return await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData, options);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Log.Error("WebView2 Runtime no está instalado");
            throw;
        }
    }

    public static string FileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}
