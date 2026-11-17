<#
  Instala Wallpaper Forge en %LOCALAPPDATA%\Programs\WallpaperForge y lo inicia.
  - Compila en Release (requiere el SDK de .NET 8).
  - Cierra la versión que esté abierta antes de reemplazarla.
  - Crea un acceso directo en el menú Inicio.
  - El arranque con Windows lo configura la propia app (apunta a esta carpeta, no a bin\Debug).

  Uso (desde la carpeta del repo):
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\WallpaperForge'

Write-Host "Compilando..." -ForegroundColor Cyan
dotnet publish (Join-Path $repo 'src\WallpaperForge\WallpaperForge.csproj') -c Release -o (Join-Path $repo 'publish') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "La compilación falló" }

$running = Get-Process -Name WallpaperForge -ErrorAction SilentlyContinue
if ($running) {
  Write-Host "Cerrando la versión abierta..." -ForegroundColor Cyan
  $running | Stop-Process -Force
  Start-Sleep -Seconds 1
}

Write-Host "Copiando a $target ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $repo 'publish\*') -Destination $target -Recurse -Force

$exe = Join-Path $target 'WallpaperForge.exe'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Wallpaper Forge.lnk'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $target
$lnk.Save()

Write-Host "Iniciando Wallpaper Forge..." -ForegroundColor Green
Start-Process $exe
Write-Host "Listo. Queda instalado en $target y arranca con Windows." -ForegroundColor Green
