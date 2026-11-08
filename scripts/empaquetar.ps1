<#
  Crea el instalador de Kroko (sección 13) con Velopack.

  Requisitos (una sola vez):
    dotnet tool install -g vpk

  Uso:
    .\scripts\empaquetar.ps1 -Version 1.0.0

  Resultado: carpeta .\Releases con el instalador (solo la App; el cerebro vive en el servidor).
  Normalmente no hace falta: al crear un tag vX.Y.Z, GitHub Actions publica solo
  (.github/workflows/release-app.yml) y Kroko se actualiza desde GitHub Releases.
  Versión de vpk = la de la librería Velopack: dotnet tool install -g vpk --version 1.2.161
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish\app"
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "1/2 Publicando la App (sin el cerebro: vive en el servidor)..." -ForegroundColor Cyan
dotnet publish "$root\src\Kroko.App\Kroko.App.csproj" -c Release -r $Runtime --self-contained true `
    -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación de la App" }

Write-Host "2/2 Empaquetando con Velopack..." -ForegroundColor Cyan
vpk pack --packId KrokoApp --packVersion $Version --packDir $publish --mainExe Kroko.App.exe `
    --packTitle "Kroko" --outputDir (Join-Path $root "Releases")
if ($LASTEXITCODE -ne 0) { throw "Falló vpk pack (¿instalaste vpk con 'dotnet tool install -g vpk'?)" }

Write-Host "Listo: carpeta Releases (instalador KrokoApp-win-Setup.exe)" -ForegroundColor Green
Write-Host "Las claves NUNCA van en el instalador: se ponen en Configurar Kroko (en el servidor)." -ForegroundColor Yellow
