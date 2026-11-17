using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WallpaperForge.Rendering;
using WallpaperForge.Wallpapers;

namespace WallpaperForge.Core;

/// <summary>
/// Autodiagnóstico en Windows real: WallpaperForge.exe --selftest [resultado.json]
/// Prueba la incrustación en el escritorio, la API web, la pausa y la detección de pantalla completa.
/// No toca la configuración del usuario. Código de salida 0 = todo bien.
/// </summary>
public static class SelfTest
{
    private sealed record Check(string Name, bool Ok, string Detail);

    public static async Task<int> RunAsync(string outputPath)
    {
        var checks = new List<Check>();
        void Add(string name, bool ok, string detail = "")
        {
            checks.Add(new Check(name, ok, detail));
            Log.Info($"[selftest] {(ok ? "OK  " : "FAIL")} {name} {detail}");
        }

        var info = new JsonObject
        {
            ["os"] = Environment.OSVersion.VersionString,
            ["build"] = Environment.OSVersion.Version.Build,
            ["is64"] = Environment.Is64BitOperatingSystem,
        };

        var temp = Path.Combine(Path.GetTempPath(), "wf-selftest-" + Guid.NewGuid().ToString("N"));
        WallpaperWindow? window = null;
        try
        {
            // 1) Monitores
            var monitors = Monitors.GetAll();
            Add("monitores detectados", monitors.Count > 0, string.Join("; ", monitors.Select(m => $"{m.Id} {m.Bounds} x{m.Scale}")));
            if (monitors.Count == 0) return Finish();
            var primary = monitors.First(m => m.IsPrimary);

            // 2) Escritorio
            var desktop = new DesktopHost();
            try
            {
                desktop.Initialize();
                Add("escritorio (Progman/WorkerW)", desktop.IsReady,
                    $"Progman={desktop.Progman:X} WorkerW={desktop.WorkerW:X} DefView={desktop.ShellView:X} 24H2={desktop.IsLayeredShellView}");
            }
            catch (Exception ex)
            {
                Add("escritorio (Progman/WorkerW)", false, ex.Message);
                return Finish();
            }
            info["layeredShellView"] = desktop.IsLayeredShellView;

            // 3) Wallpaper web de prueba con una propiedad
            Directory.CreateDirectory(temp);
            await File.WriteAllTextAsync(Path.Combine(temp, "project.json"), """
            { "title": "selftest", "type": "web", "file": "index.html",
              "general": { "properties": { "speed": { "type": "slider", "value": 7, "min": 0, "max": 10 } } } }
            """);
            await File.WriteAllTextAsync(Path.Combine(temp, "index.html"), """
            <!doctype html><html><body style="background:#123"><script>
            window.wallpaperPropertyListener = {
              applyUserProperties: function (p) { if (p.speed) document.title = 'speed:' + p.speed.value; },
              setPaused: function (x) { window.__pausedCalls = (window.__pausedCalls || 0) + 1; }
            };
            </script></body></html>
            """);
            var project = WallpaperProject.Load(temp, WallpaperSource.Local)!;
            var settings = new AppSettings { FpsLimit = 30 };

            window = new WallpaperWindow(primary);
            var sw = Stopwatch.StartNew();
            await window.InitializeAsync(desktop);
            Add("WebView2 inicializado", window.IsAlive, $"{sw.ElapsedMilliseconds} ms");
            Add("ventana incrustada en el escritorio", window.IsAttachedTo(desktop),
                $"padre={Native.GetParent(window.Handle):X} esperado={desktop.WallpaperParent:X}");
            Native.GetWindowRect(window.Handle, out var rect);
            Add("ventana cubre el monitor principal",
                rect.Left == primary.Bounds.Left && rect.Top == primary.Bounds.Top && rect.Width == primary.Bounds.Width && rect.Height == primary.Bounds.Height,
                $"{rect} vs {primary.Bounds}");

            await window.LoadAsync(project, settings);
            var title = await WaitForAsync(window, "document.title", v => v.Contains("speed:7"), TimeSpan.FromSeconds(20));
            Add("API web: applyUserProperties inicial", title?.Contains("speed:7") == true, title ?? "sin respuesta");

            await window.ApplyUserPropertiesAsync(new JsonObject { ["speed"] = new JsonObject { ["type"] = "slider", ["value"] = 3 } });
            title = await WaitForAsync(window, "document.title", v => v.Contains("speed:3"), TimeSpan.FromSeconds(5));
            Add("API web: cambio de propiedad en vivo", title?.Contains("speed:3") == true, title ?? "sin respuesta");

            // 4) Pausa real (suspensión de WebView2)
            await window.SetPausedAsync(true);
            Add("pausa: WebView2 suspendido", window.IsSuspended, "");
            await window.SetPausedAsync(false);
            var calls = await window.EvalAsync("String(window.__pausedCalls||0)");
            Add("reanudar: WebView2 activo y setPaused llamado", !window.IsSuspended && calls?.Contains('2') == true, $"setPaused x{calls}");

            // 5) Detección de pantalla completa por monitor
            var watcher = new FullscreenWatcher { MonitorsProvider = () => monitors, SettingsProvider = () => settings };
            using (var cover = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = primary.Bounds,
                TopMost = true,
                ShowInTaskbar = false,
                BackColor = Color.Black,
            })
            {
                // Proceso propio: el detector lo ignora a propósito, así que se prueba con un proceso aparte.
                cover.Show();
                await Task.Delay(300);
                var ownIgnored = !watcher.Compute(monitors, settings).Contains(primary.Id);
                Add("pantalla completa: ignora ventanas propias", ownIgnored, "");
            }
            var external = await ExternalFullscreenCheckAsync(watcher, monitors, settings, primary);
            Add("pantalla completa: detecta otra app sólo en su monitor", external.ok, external.detail);
        }
        catch (Exception ex)
        {
            Add("excepción", false, ex.ToString());
        }
        finally
        {
            try { window?.Close(); window?.Dispose(); } catch { }
            try { Directory.Delete(temp, true); } catch { }
        }
        return Finish();

        int Finish()
        {
            var json = new JsonObject
            {
                ["info"] = info,
                ["passed"] = checks.Count(c => c.Ok),
                ["failed"] = checks.Count(c => !c.Ok),
                ["checks"] = new JsonArray(checks.Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name, ["ok"] = c.Ok, ["detail"] = c.Detail,
                }).ToArray()),
            };
            File.WriteAllText(outputPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return checks.Count > 0 && checks.All(c => c.Ok) ? 0 : 1;
        }
    }

    /// <summary>Abre un PowerShell con una ventana sin bordes a pantalla completa en el monitor principal.</summary>
    private static async Task<(bool ok, string detail)> ExternalFullscreenCheckAsync(
        FullscreenWatcher watcher, List<MonitorInfo> monitors, AppSettings settings, MonitorInfo primary)
    {
        var b = primary.Bounds;
        var script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; " +
                     "$f.FormBorderStyle='None'; $f.StartPosition='Manual'; $f.TopMost=$true; " +
                     $"$f.SetBounds({b.X},{b.Y},{b.Width},{b.Height}); $f.ShowInTaskbar=$false; " +
                     "$t = New-Object System.Windows.Forms.Timer; $t.Interval=8000; $t.add_Tick({$f.Close()}); $t.Start(); " +
                     "[System.Windows.Forms.Application]::Run($f)";
        Process? ps = null;
        try
        {
            ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
            HashSet<string> covered = new();
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(300);
                covered = watcher.Compute(monitors, settings);
                if (covered.Contains(primary.Id)) break;
            }
            bool others = monitors.Where(m => m.Id != primary.Id).All(m => !covered.Contains(m.Id));
            return (covered.Contains(primary.Id) && others, "tapados: " + string.Join(",", covered));
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally
        {
            try { if (ps != null && !ps.HasExited) ps.Kill(true); } catch { }
        }
    }

    private static async Task<string?> WaitForAsync(WallpaperWindow w, string expr, Func<string, bool> done, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        string? last = null;
        while (sw.Elapsed < timeout)
        {
            last = await w.EvalAsync(expr);
            if (last != null && done(last)) return last;
            await Task.Delay(250);
        }
        return last;
    }
}
