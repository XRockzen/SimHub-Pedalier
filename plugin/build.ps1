<#
  Compile le plugin avec le MSBuild livré avec Windows (aucune installation nécessaire).
    .\build.ps1           compile seulement (bin\Release\User.PedalierDIY.dll)
    .\build.ps1 -Deploy   compile puis copie la DLL dans SimHub (SimHub doit être fermé)
#>
param([switch]$Deploy)
$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$simhub  = if ($env:SIMHUB_INSTALL_PATH) { $env:SIMHUB_INSTALL_PATH } else { "F:\Program Files (x86)\SimHub\" }
$msbuild = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"

# Le chemin de SimHub vient de SIMHUB_INSTALL_PATH (lu par le .csproj), pas d'un /p: :
# son "\" final échapperait le guillemet de l'argument.
& $msbuild "$root\User.PedalierDIY.csproj" /nologo /v:minimal /p:Configuration=Release
if ($LASTEXITCODE -ne 0) { Write-Host "ERREUR de compilation" -ForegroundColor Red; exit 1 }

if ($Deploy) {
    if (Get-Process SimHubWPF -ErrorAction SilentlyContinue) { Write-Host "Ferme SimHub avant de déployer." -ForegroundColor Red; exit 1 }
    Copy-Item "$root\bin\Release\User.PedalierDIY.dll" $simhub -Force
    Copy-Item "$root\bin\Release\User.PedalierDIY.pdb" $simhub -Force -ErrorAction SilentlyContinue
    Write-Host "Plugin copié dans $simhub" -ForegroundColor Green
}
