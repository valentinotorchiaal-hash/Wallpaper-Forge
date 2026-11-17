using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace WallpaperForge.Core;

public enum AutostartKind { RunKeyUser, RunKeyMachine, StartupFolderUser, StartupFolderCommon, ScheduledTask, Service }

/// <summary>Una forma en que otro programa de fondos arranca con Windows.</summary>
public sealed class AutostartEntry
{
    public AutostartKind Kind { get; set; }
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    /// <summary>Nombre del valor del registro, archivo .lnk, tarea programada o servicio.</summary>
    public string Name { get; set; } = "";
    /// <summary>Clave de registro (para Run) o carpeta (para Startup).</summary>
    public string Location { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Tipo de inicio original del servicio (2 = automático), para restaurarlo.</summary>
    public int? OriginalServiceStart { get; set; }

    public bool NeedsAdmin => Kind is AutostartKind.RunKeyMachine or AutostartKind.StartupFolderCommon or AutostartKind.Service;
    public string Key => $"{Kind}|{Location}|{Name}";

    public string Describe() => Kind switch
    {
        AutostartKind.RunKeyUser => "Inicio de Windows (usuario)",
        AutostartKind.RunKeyMachine => "Inicio de Windows (todos los usuarios)",
        AutostartKind.StartupFolderUser => "Carpeta Inicio (usuario)",
        AutostartKind.StartupFolderCommon => "Carpeta Inicio (todos los usuarios)",
        AutostartKind.ScheduledTask => "Tarea programada",
        AutostartKind.Service => "Servicio de Windows (prioridad alta)",
        _ => Kind.ToString(),
    } + ": " + Name;
}

public sealed record RunningCompetitor(string AppId, string AppName, int Pid, string ProcessName, string? Path);

public sealed class CompetitorReport
{
    public List<AutostartEntry> Autostart { get; } = new();
    public List<RunningCompetitor> Running { get; } = new();
    public bool Any => Autostart.Any(a => a.Enabled) || Running.Count > 0;
    public IEnumerable<string> AppNames => Autostart.Select(a => a.AppName).Concat(Running.Select(r => r.AppName)).Distinct();
}

/// <summary>
/// Detecta, cierra y desactiva el arranque automático de otros programas de fondos (Wallpaper Engine, Lively).
/// Todo lo desactivado se guarda en la configuración para poder restaurarlo.
/// Lo que requiere permisos de administrador se ejecuta junto, con un único aviso de UAC.
/// </summary>
public static class OtherWallpaperApps
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunPath32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public static CompetitorReport Detect()
    {
        var report = new CompetitorReport();
        Safe(() => ScanRunKey(report, Registry.CurrentUser, RunPath, ApprovedRun, AutostartKind.RunKeyUser));
        Safe(() => ScanRunKey(report, Registry.LocalMachine, RunPath, ApprovedRun, AutostartKind.RunKeyMachine));
        Safe(() => ScanRunKey(report, Registry.LocalMachine, RunPath32, ApprovedRun32, AutostartKind.RunKeyMachine));
        Safe(() => ScanStartupFolder(report, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, AutostartKind.StartupFolderUser));
        Safe(() => ScanStartupFolder(report, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, AutostartKind.StartupFolderCommon));
        Safe(() => ScanServices(report));
        Safe(() => ScanScheduledTasks(report));
        Safe(() => ScanProcesses(report));
        return report;
    }

    // ---------------- Detección ----------------

    private static void ScanRunKey(CompetitorReport r, RegistryKey hive, string path, string approvedPath, AutostartKind kind)
    {
        using var key = hive.OpenSubKey(path);
        if (key == null) return;
        using var approved = hive.OpenSubKey(approvedPath);
        foreach (var name in key.GetValueNames())
        {
            var cmd = key.GetValue(name) as string;
            var app = AutostartMatcher.MatchPath(cmd);
            if (app == null) continue;
            r.Autostart.Add(new AutostartEntry
            {
                Kind = kind, AppId = app.Id, AppName = app.Name, Name = name,
                Location = (hive == Registry.CurrentUser ? "HKCU\\" : "HKLM\\") + path,
                Command = cmd ?? "",
                Enabled = !AutostartMatcher.IsStartupApprovedDisabled(approved?.GetValue(name) as byte[]),
            });
        }
    }

    private static void ScanStartupFolder(CompetitorReport r, string folder, RegistryKey hive, AutostartKind kind)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        using var approved = hive.OpenSubKey(ApprovedFolder);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); } catch { continue; }
            var app = AutostartMatcher.MatchShortcutBytes(name, bytes);
            if (app == null) continue;
            r.Autostart.Add(new AutostartEntry
            {
                Kind = kind, AppId = app.Id, AppName = app.Name, Name = name, Location = folder, Command = file,
                Enabled = !AutostartMatcher.IsStartupApprovedDisabled(approved?.GetValue(name) as byte[]),
            });
        }
    }

    private static void ScanServices(CompetitorReport r)
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services == null) return;
        foreach (var name in services.GetSubKeyNames())
        {
            using var svc = services.OpenSubKey(name);
            var image = svc?.GetValue("ImagePath") as string;
            var app = AutostartMatcher.MatchPath(image);
            if (app == null) continue;
            int start = svc?.GetValue("Start") is int s ? s : 3;
            r.Autostart.Add(new AutostartEntry
            {
                Kind = AutostartKind.Service, AppId = app.Id, AppName = app.Name, Name = name,
                Location = "services", Command = image ?? "",
                Enabled = start is 0 or 1 or 2, OriginalServiceStart = start,
            });
        }
    }

    private static void ScanScheduledTasks(CompetitorReport r)
    {
        var output = RunCapture("schtasks.exe", "/Query /FO CSV /V /NH", 15000);
        if (output == null) return;
        // Columnas (en cualquier idioma de Windows): 1 = nombre de tarea, 8 = tarea a ejecutar, 11 = estado.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var cols = AutostartMatcher.ParseCsvLine(line.Trim());
            if (cols.Count < 12) continue;
            var app = AutostartMatcher.MatchPath(cols[8]);
            if (app == null || !seen.Add(cols[1])) continue;
            var state = cols[11];
            bool disabled = state.Contains("Disabled", StringComparison.OrdinalIgnoreCase)
                            || state.Contains("Deshabilitad", StringComparison.OrdinalIgnoreCase);
            r.Autostart.Add(new AutostartEntry
            {
                Kind = AutostartKind.ScheduledTask, AppId = app.Id, AppName = app.Name, Name = cols[1],
                Location = "tasks", Command = cols[8], Enabled = !disabled,
            });
        }
    }

    private static void ScanProcesses(CompetitorReport r)
    {
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == self) continue;
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* proceso elevado o protegido */ }
                var app = AutostartMatcher.MatchProcess(p.ProcessName, path);
                if (app != null) r.Running.Add(new RunningCompetitor(app.Id, app.Name, p.Id, p.ProcessName, path));
            }
        }
    }

    // ---------------- Acciones ----------------

    /// <summary>Cierra los procesos de otros programas de fondos. Devuelve los que no se pudieron cerrar (sin permisos).</summary>
    public static List<RunningCompetitor> CloseRunning(IEnumerable<RunningCompetitor> running)
    {
        var failed = new List<RunningCompetitor>();
        foreach (var rc in running)
        {
            try
            {
                using var p = Process.GetProcessById(rc.Pid);
                p.CloseMainWindow();
                if (!p.WaitForExit(1500)) p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                Log.Info($"Cerrado {rc.AppName}: {rc.ProcessName} ({rc.Pid})");
            }
            catch (ArgumentException) { /* ya terminó */ }
            catch (Exception ex)
            {
                Log.Warn($"No se pudo cerrar {rc.ProcessName}: {ex.Message}");
                failed.Add(rc);
            }
        }
        return failed;
    }

    public sealed class ActionResult
    {
        public List<AutostartEntry> Changed { get; } = new();
        public List<string> Errors { get; } = new();
        public bool ElevationDeclined { get; set; }
    }

    /// <summary>Desactiva (enable=false) o restaura (enable=true) entradas de inicio automático.</summary>
    public static ActionResult SetAutostart(IEnumerable<AutostartEntry> entries, bool enable, IEnumerable<RunningCompetitor>? alsoKill = null)
    {
        var result = new ActionResult();
        var elevated = new List<string>();
        var elevatedEntries = new List<AutostartEntry>();

        foreach (var e in entries)
        {
            try
            {
                switch (e.Kind)
                {
                    case AutostartKind.RunKeyUser:
                        WriteApproved(Registry.CurrentUser, ApprovedRun, e.Name, enable);
                        result.Changed.Add(e);
                        break;
                    case AutostartKind.StartupFolderUser:
                        WriteApproved(Registry.CurrentUser, ApprovedFolder, e.Name, enable);
                        result.Changed.Add(e);
                        break;
                    case AutostartKind.RunKeyMachine:
                    {
                        var approved = e.Location.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase) ? ApprovedRun32 : ApprovedRun;
                        elevated.Add($"reg add \"HKLM\\{approved}\" /v \"{e.Name}\" /t REG_BINARY /d {Hex(AutostartMatcher.StartupApprovedValue(enable))} /f");
                        elevatedEntries.Add(e);
                        break;
                    }
                    case AutostartKind.StartupFolderCommon:
                        elevated.Add($"reg add \"HKLM\\{ApprovedFolder}\" /v \"{e.Name}\" /t REG_BINARY /d {Hex(AutostartMatcher.StartupApprovedValue(enable))} /f");
                        elevatedEntries.Add(e);
                        break;
                    case AutostartKind.ScheduledTask:
                        // Primero sin permisos; si falla, se agrega al lote elevado.
                        var code = RunExit("schtasks.exe", $"/Change /TN \"{e.Name}\" {(enable ? "/ENABLE" : "/DISABLE")}");
                        if (code == 0) result.Changed.Add(e);
                        else
                        {
                            elevated.Add($"schtasks /Change /TN \"{e.Name}\" {(enable ? "/ENABLE" : "/DISABLE")}");
                            elevatedEntries.Add(e);
                        }
                        break;
                    case AutostartKind.Service:
                        var startMode = enable ? (e.OriginalServiceStart switch { 2 => "auto", 3 => "demand", _ => "auto" }) : "disabled";
                        if (!enable) elevated.Add($"sc stop \"{e.Name}\"");
                        elevated.Add($"sc config \"{e.Name}\" start= {startMode}");
                        elevatedEntries.Add(e);
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{e.Describe()}: {ex.Message}");
            }
        }

        if (!enable && alsoKill != null)
            foreach (var rc in alsoKill)
                elevated.Add($"taskkill /F /PID {rc.Pid}");

        if (elevated.Count > 0)
        {
            var ok = RunElevatedBatch(elevated);
            if (ok == null) result.ElevationDeclined = true;
            else if (ok == true) result.Changed.AddRange(elevatedEntries);
            else result.Errors.Add("Algunas acciones con permisos de administrador fallaron (ver log).");
        }
        foreach (var e in result.Changed) e.Enabled = enable;
        return result;
    }

    private static void WriteApproved(RegistryKey hive, string path, string name, bool enable)
    {
        using var key = hive.CreateSubKey(path, writable: true);
        key.SetValue(name, AutostartMatcher.StartupApprovedValue(enable), RegistryValueKind.Binary);
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    /// <summary>Ejecuta comandos como administrador (un solo aviso de UAC). null = el usuario canceló.</summary>
    private static bool? RunElevatedBatch(List<string> commands)
    {
        var script = Path.Combine(Path.GetTempPath(), $"wallpaperforge-{Guid.NewGuid():N}.cmd");
        var log = script + ".log";
        try
        {
            var sb = new StringBuilder("@echo off\r\nchcp 65001 >nul\r\n");
            foreach (var c in commands) sb.Append(c).Append($" >> \"{log}\" 2>&1\r\n");
            sb.Append("exit /b 0\r\n"); // el resultado real se verifica volviendo a detectar
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(60000);
            if (File.Exists(log)) Log.Info("Acciones como administrador:\n" + File.ReadAllText(log));
            return p?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn("El usuario canceló el aviso de administrador");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron ejecutar acciones como administrador", ex);
            return false;
        }
        finally
        {
            try { File.Delete(script); } catch { }
            try { File.Delete(log); } catch { }
        }
    }

    private static string? RunCapture(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
            return output.Result;
        }
        catch (Exception ex)
        {
            Log.Warn($"{exe} falló: {ex.Message}");
            return null;
        }
    }

    private static int RunExit(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!;
            return p.WaitForExit(15000) ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    private static void Safe(Action a)
    {
        try { a(); } catch (Exception ex) { Log.Warn("Detección de otros programas: " + ex.Message); }
    }
}
