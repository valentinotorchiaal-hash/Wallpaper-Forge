<#
  Instala (o actualiza) Wallpaper Forge y deja UNA sola versión en la PC.

  1. Compila en Release (requiere el SDK de .NET 8).
  2. Cierra TODAS las copias abiertas de Wallpaper Forge, estén donde estén.
  3. Copia la app a %LOCALAPPDATA%\Programs\WallpaperForge.
  4. Busca otras copias de WallpaperForge.exe (bin\Debug, carpeta del zip, Descargas, Escritorio...)
     y pregunta si borrarlas.
  5. Corrige accesos directos viejos y deja el arranque con Windows apuntando a la versión instalada.
  6. La abre.

  Uso (desde la carpeta del repo):
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1
  Sin preguntas (borra las copias viejas):
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Yes
#>
param([switch]$Yes)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\WallpaperForge'
$exe = Join-Path $target 'WallpaperForge.exe'
$publish = Join-Path $repo 'publish'

Write-Host "1/6 Compilando..." -ForegroundColor Cyan
dotnet publish (Join-Path $repo 'src\WallpaperForge\WallpaperForge.csproj') -c Release -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "La compilación falló" }

Write-Host "2/6 Cerrando todas las copias abiertas de Wallpaper Forge..." -ForegroundColor Cyan
$running = Get-Process -Name WallpaperForge -ErrorAction SilentlyContinue
# Se anota de dónde corre cada copia abierta: puede estar más adentro de lo que llega la búsqueda del paso 4
# (p. ej. Descargas\WallpaperForge-v0.1\src\WallpaperForge\bin\Release\net8.0-windows...\win-x64).
$runningExes = @()
foreach ($p in $running) {
  $path = try { $p.Path } catch { '?' }
  if ($path -and $path -ne '?') { $runningExes += $path }
  Write-Host "    cerrando $path (pid $($p.Id))"
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
if ($running) { Start-Sleep -Seconds 2 }

Write-Host "3/6 Instalando en $target ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $target | Out-Null
# Se borra la instalación anterior para que no queden archivos viejos (p. ej. la interfaz de otra versión).
Get-ChildItem $target -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $publish '*') -Destination $target -Recurse -Force
$version = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "    versión instalada: $version"

Write-Host "4/6 Buscando otras copias del programa..." -ForegroundColor Cyan
$places = @(
  (Join-Path $repo 'src\WallpaperForge\bin'),
  [Environment]::GetFolderPath('Desktop'),
  [Environment]::GetFolderPath('MyDocuments'),
  (Join-Path $env:USERPROFILE 'Downloads'),
  (Join-Path $env:USERPROFILE 'source\repos'),
  $env:USERPROFILE
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
$copies = @()
foreach ($place in $places) {
  $depth = if ($place -eq $env:USERPROFILE) { 2 } else { 6 }
  $copies += Get-ChildItem -Path $place -Filter 'WallpaperForge.exe' -Recurse -Depth $depth -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notlike "$target\*" -and $_.FullName -notlike "$publish\*" -and $_.FullName -notlike "*\obj\*" }
}
foreach ($r in $runningExes) {
  if ($r -notlike "$target\*" -and $r -notlike "$publish\*" -and (Test-Path $r)) { $copies += Get-Item $r }
}
$copies = $copies | Sort-Object FullName -Unique
if ($copies.Count -eq 0) {
  Write-Host "    no hay otras copias."
} else {
  foreach ($c in $copies) { Write-Host ("    {0}  (v{1})" -f $c.FullName, $c.VersionInfo.ProductVersion) -ForegroundColor Yellow }
  $answer = if ($Yes) { 'S' } else { Read-Host "    ¿Borrar estas copias viejas? Se borran sólo los archivos del programa (S/N)" }
  if ($answer -match '^[sSyY]') {
    # Sólo se borran los archivos y carpetas que forman parte del programa (los mismos nombres que trae
    # la versión recién compilada). Nunca se borra nada más de esas carpetas.
    $appItems = Get-ChildItem $publish -Force | Select-Object -ExpandProperty Name
    foreach ($c in $copies) {
      $dir = $c.DirectoryName
      foreach ($name in $appItems) {
        $item = Join-Path $dir $name
        if (-not (Test-Path $item)) { continue }
        # Las carpetas genéricas sólo se borran si son de Wallpaper Forge.
        if ($name -eq 'wwwroot' -and -not (Test-Path (Join-Path $item 'runtime\we-shim.js'))) { continue }
        if ($name -eq 'runtimes' -and -not (Get-ChildItem $item -Recurse -Filter 'WebView2Loader.dll' -ErrorAction SilentlyContinue)) { continue }
        Remove-Item -Path $item -Recurse -Force -ErrorAction SilentlyContinue
      }
      Write-Host "    borrada la copia de: $dir"
      if (-not (Get-ChildItem $dir -Force -ErrorAction SilentlyContinue)) { Remove-Item $dir -Force -ErrorAction SilentlyContinue }
    }
  } else {
    Write-Host "    se dejaron como están. Igual, al abrir la versión instalada, cualquier otra copia abierta se cierra sola."
  }
}

Write-Host "5/6 Accesos directos y arranque con Windows..." -ForegroundColor Cyan
$shell = New-Object -ComObject WScript.Shell
$shortcutDirs = @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Startup'))
foreach ($d in $shortcutDirs) {
  Get-ChildItem $d -Filter '*.lnk' -ErrorAction SilentlyContinue | ForEach-Object {
    $l = $shell.CreateShortcut($_.FullName)
    if ($l.TargetPath -like '*WallpaperForge.exe' -and $l.TargetPath -ne $exe) {
      Remove-Item $_.FullName -Force
      Write-Host "    acceso directo viejo borrado: $($_.FullName)"
    }
  }
}
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Wallpaper Forge.lnk'))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $target
$lnk.Save()

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
Set-ItemProperty -Path $run -Name 'WallpaperForge' -Value "`"$exe`" --startup"
if (-not (Test-Path $approved)) { New-Item -Path $approved -Force | Out-Null }
Set-ItemProperty -Path $approved -Name 'WallpaperForge' -Value ([byte[]](2,0,0,0,0,0,0,0,0,0,0,0)) -Type Binary
Write-Host "    arranca con Windows desde $exe"

Write-Host "6/6 Abriendo Wallpaper Forge $version..." -ForegroundColor Green
Start-Process $exe
Write-Host "Listo. La versión aparece al lado del nombre, arriba a la izquierda de la ventana." -ForegroundColor Green
