# Wallpaper Forge

Fondos de pantalla animados para Windows 11: **video**, **web (HTML/JS)** y **escenas**,
compatible con los wallpapers que ya tenés descargados de Wallpaper Engine.

> Estado: **v0.2.** La v0.1 se ejecutó sin errores en Windows 11. En cada cambio, GitHub Actions compila,
> corre los tests y ejecuta un autodiagnóstico en una máquina Windows (`--selftest`).

## Instalar (recomendado)

Desde la carpeta del repo, en PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

Compila, copia la app a `%LOCALAPPDATA%\Programs\WallpaperForge`, crea el acceso directo en el menú Inicio
y la abre. A partir de ahí **arranca sola con Windows** (se puede desactivar en Configuración → Inicio).
Para actualizar después de un `git pull`, se corre el mismo script.

## Qué hace la v0.1

| Función | Estado |
|---|---|
| Wallpapers de video (mp4, webm, mov…) con volumen, velocidad y escalado | ✅ |
| Wallpapers web (HTML/CSS/JS) | ✅ |
| API web de Wallpaper Engine (`wallpaperPropertyListener`, `applyUserProperties`, `applyGeneralProperties`, `setPaused`, carpetas, archivo aleatorio) | ✅ |
| Abrir wallpapers de Wallpaper Engine (Steam Workshop y `projects/myprojects`) | ✅ video y web; escenas parcial |
| Escenas `.pkg`: capas de imagen, jerarquía, rotación/escala, opacidad, mezcla aditiva, texturas animadas, sonidos, visibilidad ligada a propiedades | ✅ (mejor esfuerzo) |
| Escenas: efectos/shaders, partículas, texto, puppet warp, parallax, SceneScript | ❌ próximas etapas |
| Un wallpaper distinto por monitor | ✅ |
| Pausa **por monitor** cuando hay una app en pantalla completa (los demás monitores siguen animados) | ✅ |
| Opción de pausar también con ventanas maximizadas, lista de procesos ignorados | ✅ |
| Panel de propiedades (slider, bool, color, combo, texto, archivo, carpeta, condiciones) | ✅ |
| Biblioteca con búsqueda, filtros por tipo/origen, orden y vista previa | ✅ |
| Importar video, importar carpeta web, agregar carpetas | ✅ |
| Ícono en la bandeja del sistema, arranque con Windows (activado por defecto) | ✅ |
| Reemplazar a Wallpaper Engine / Lively: cerrarlos, desactivar su inicio automático (registro, carpeta Inicio, tarea programada o servicio "prioridad alta") y restaurarlos | ✅ |
| Pasar a Forge los wallpapers que Wallpaper Engine tenía en cada monitor | ✅ (mejor esfuerzo) |
| Pausa con batería, al bloquear la sesión, y límite de FPS en escenas | ✅ |
| Recuperación automática: Explorer reiniciado, salida de suspensión, caída del proceso del wallpaper | ✅ |
| Reacción al audio, interacción con el mouse, wallpapers de aplicación (.exe) | ❌ próximas etapas |
| Compartir online / Steam Workshop propio | ❌ próximas etapas |

## Requisitos

- Windows 10 (19041+) o Windows 11 (incluido 24H2).
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) para ejecutar, o el SDK de .NET 8 para compilar.
- Microsoft Edge WebView2 Runtime (viene instalado en Windows 11).

## Compilar y ejecutar

En Visual Studio 2022: abrir `WallpaperForge.sln`, proyecto de inicio `WallpaperForge`, F5.

Por línea de comandos:

```bash
dotnet build -c Release
```

```bash
dotnet run --project src/WallpaperForge -- --dev
```

`--dev` habilita las DevTools de WebView2 en la interfaz y en los wallpapers (útil para depurar wallpapers web).

Autodiagnóstico en tu PC (no toca tu configuración; deja un JSON con el resultado de cada prueba):

```powershell
WallpaperForge.exe --selftest resultado.json
```

Publicar un ejecutable:

```bash
dotnet publish src/WallpaperForge -c Release -o publish
```

## Tests

```bash
dotnet test tests/WallpaperForge.Tests
```

Tests del runtime web en Chromium (requiere Node y Playwright):

```bash
WF_EXPORT_SCENE=scene-out dotnet test tests/WallpaperForge.Tests
```

```bash
node tests/runtime/runtime-test.js scene-out
```

## Arquitectura

```
src/WallpaperForge/
  Program.cs               instancia única, arranque
  Core/
    DesktopHost.cs         incrusta ventanas detrás de los íconos (WorkerW; variante 24H2 con Progman)
    OtherWallpaperApps.cs  detecta/cierra/desactiva Wallpaper Engine y Lively (con un solo aviso de UAC)
    AutostartMatcher.cs    reglas de detección (sin dependencias de Windows, testeadas)
    WallpaperEngineMigration.cs  lee qué wallpaper tenía Wallpaper Engine en cada monitor
    SelfTest.cs            autodiagnóstico --selftest
    Monitors.cs            monitores en píxeles físicos (Per-Monitor DPI V2)
    FullscreenWatcher.cs   detecta qué monitor está tapado por una app en pantalla completa
    WallpaperManager.cs    una ventana por monitor, asignaciones, pausa, cambios de pantalla, reinicio de Explorer
    Settings.cs            %APPDATA%\WallpaperForge\settings.json
    StartupManager.cs      arranque con Windows (HKCU\...\Run)
  Wallpapers/
    WallpaperProject.cs    lectura de project.json (formato de Wallpaper Engine)
    Library.cs             escaneo de biblioteca propia, carpetas extra y Steam Workshop (app 431960)
    SteamLocator.cs        bibliotecas de Steam (libraryfolders.vdf)
    PropertyValues.cs      valores por defecto + cambios del usuario
  Scene/
    PkgArchive.cs          lector .pkg
    TexFile.cs, Dxt.cs     lector .tex (RGBA, DXT1/3/5, RG88, R8, LZ4, imágenes embebidas, sprite sheets)
    PngWriter.cs           PNG sin dependencias
    SceneConverter.cs      scene.json → capas PNG + scene-data.js
  Rendering/
    WallpaperWindow.cs     ventana por monitor con WebView2 (pausa = suspensión real del navegador)
    WebViewEnvironment.cs  un solo proceso de navegador compartido
  UI/
    MainWindow.cs, Bridge.cs, TrayApp.cs
  wwwroot/
    ui/                    interfaz (HTML/CSS/JS). Abriendo ui/index.html en un navegador se ve en modo demostración
    runtime/we-shim.js     compatibilidad con la API web de Wallpaper Engine
    runtime/video.html     reproductor de video
    runtime/scene.html     renderizador 2D de escenas
```

### Por qué WebView2 también para video

El plan inicial era libmpv para los videos. Con WebView2 (Chromium) el video se decodifica por
hardware, soporta H.264, VP9 y AV1 (HEVC sólo si Windows tiene la extensión HEVC instalada), y la app queda con un solo motor y un solo proceso de navegador
para todo. Pausar un monitor suspende su WebView2, así que no consume CPU ni GPU mientras está tapado.
Si más adelante hace falta (formatos raros, menor consumo de RAM), se puede agregar libmpv como reproductor alternativo.

### Archivos de datos

| Qué | Dónde |
|---|---|
| Configuración | `%APPDATA%\WallpaperForge\settings.json` |
| Logs (7 días) | `%LOCALAPPDATA%\WallpaperForge\logs` |
| Caché de escenas convertidas | `%LOCALAPPDATA%\WallpaperForge\scene-cache` |
| Biblioteca propia | `Documentos\Wallpaper Forge\wallpapers` |

## Qué revisar en la primera prueba en Windows

1. Que el wallpaper aparezca **detrás de los íconos** del escritorio (y no encima ni oculto).
2. Con dos monitores: un juego en pantalla completa en uno debe pausar sólo ese monitor.
3. Que al salir de la app vuelva el fondo normal de Windows.
4. Reiniciar `explorer.exe` desde el Administrador de tareas: el wallpaper debe volver solo.
5. Si algo falla: el log está en `%LOCALAPPDATA%\WallpaperForge\logs`.

## Licencia

MIT. Ver [LICENSE](LICENSE) y [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Proyecto independiente, no afiliado a Wallpaper Engine ni a Valve. Los wallpapers del Workshop
pertenecen a sus autores: Wallpaper Forge sólo los lee desde tu disco.
