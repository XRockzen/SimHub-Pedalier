<#
  Publie une nouvelle version sur GitHub (XRockzen/SimHub-Pedalier).
  Le plugin de ton frère affiche un bandeau de mise à jour au démarrage suivant de SimHub ;
  si le firmware publié est plus récent que celui du pédalier, il propose aussi de le mettre à jour.

  -Version = version du PLUGIN (toujours augmentée). La version du FIRMWARE est FW_VERSION dans
  firmware\Pedalier\Pedalier.ino : l'augmenter à la main quand le firmware change.

  Usage :
    .\release.ps1 -Version 1.1.0 -Notes "- Ajout de ...`n- Correction de ..."
    .\release.ps1 -Version 1.1.0 -NotesFile notes.md -Deploy
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$Notes = "",
    [string]$NotesFile = "",
    # Installe aussi la nouvelle DLL dans le SimHub local (SimHub doit être fermé)
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$tag     = "v$Version"
$simhub  = if ($env:SIMHUB_INSTALL_PATH) { $env:SIMHUB_INSTALL_PATH } else { "F:\Program Files (x86)\SimHub\" }
$msbuild = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
$dllName = "User.PedalierDIY.dll"
$avrdude = (Get-ChildItem (Join-Path $env:LOCALAPPDATA "Arduino15\packages\arduino\tools\avrdude") -Directory | Sort-Object Name | Select-Object -Last 1).FullName

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "ERREUR : $msg" -ForegroundColor Red; exit 1 }

Push-Location $root
try {
    Step "Vérifications"
    $ghCmd = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue
    $script:ghExe = if ($ghCmd) { $ghCmd.Source } else { "C:\Program Files\GitHub CLI\gh.exe" }
    if (-not (Test-Path $script:ghExe)) { Fail "GitHub CLI (gh) introuvable : winget install --id GitHub.cli -e" }
    function gh { & $script:ghExe @args }
    gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "gh n'est pas connecté : lancez 'gh auth login'" }
    if (-not (Test-Path "$avrdude\bin\avrdude.exe")) { Fail "avrdude introuvable dans Arduino15 (installer la carte Arduino AVR dans l'IDE)" }

    # Identité des commits : l'adresse noreply de GitHub garde l'e-mail personnel hors de l'historique public
    if (-not (git config user.email)) {
        $login = gh api user --jq .login
        $id    = gh api user --jq .id
        $env:GIT_AUTHOR_NAME     = $login
        $env:GIT_COMMITTER_NAME  = $login
        $env:GIT_AUTHOR_EMAIL    = "$id+$login@users.noreply.github.com"
        $env:GIT_COMMITTER_EMAIL = $env:GIT_AUTHOR_EMAIL
    }
    if (git tag --list $tag) { Fail "Le tag $tag existe déjà" }
    $last = git tag --list "v*" | ForEach-Object { [Version]($_.TrimStart('v')) } | Sort-Object | Select-Object -Last 1
    if ($last -and [Version]$Version -le $last) { Fail "La version $Version doit être supérieure à la dernière publiée (v$last)" }
    if ($NotesFile) { $Notes = Get-Content $NotesFile -Raw -Encoding UTF8 }
    if (-not $Notes.Trim()) { Fail "Ajoutez des notes de version (-Notes ou -NotesFile) : elles s'affichent dans le bandeau de mise à jour" }

    Step "Numéro de version du plugin : $Version"
    $ai = Join-Path $root "plugin\Properties\AssemblyInfo.cs"
    $content = Get-Content $ai -Raw -Encoding UTF8
    $content = $content -replace 'AssemblyVersion\("[^"]*"\)',     "AssemblyVersion(""$Version.0"")"
    $content = $content -replace 'AssemblyFileVersion\("[^"]*"\)', "AssemblyFileVersion(""$Version.0"")"
    [IO.File]::WriteAllText($ai, $content, (New-Object Text.UTF8Encoding($true)))

    Step "Compilation du plugin"
    $env:SIMHUB_INSTALL_PATH = $simhub
    & $msbuild (Join-Path $root "plugin\User.PedalierDIY.csproj") /p:Configuration=Release /t:Rebuild /v:minimal /nologo |
        Where-Object { $_ -match 'error|->' }
    if ($LASTEXITCODE -ne 0) { Fail "La compilation du plugin a échoué" }
    $built = Join-Path $root "plugin\bin\Release\$dllName"
    $builtVersion = [Reflection.AssemblyName]::GetAssemblyName($built).Version
    if ($builtVersion -ne [Version]"$Version.0") { Fail "La DLL compilée est en $builtVersion" }

    Step "Compilation du firmware"
    $dist = Join-Path $root "dist"
    Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $dist | Out-Null
    & (Join-Path $root "firmware\build_firmware.ps1") -Out $dist
    if (-not (Test-Path "$dist\Pedalier.hex")) { Fail "La compilation du firmware a échoué" }
    $fw = [regex]::Match((Get-Content (Join-Path $root "firmware\Pedalier\Pedalier.ino") -Raw), '#define\s+FW_VERSION\s+"([^"]+)"').Groups[1].Value
    $hexName = "Pedalier-$fw.hex"
    Move-Item "$dist\Pedalier.hex" "$dist\$hexName"

    Step "Préparation des fichiers"
    Copy-Item $built $dist
    Copy-Item "$avrdude\bin\avrdude.exe" $dist
    Copy-Item "$avrdude\etc\avrdude.conf" $dist
    $files = @($dllName, $hexName, "avrdude.exe", "avrdude.conf")
    $sums = $files | ForEach-Object { (Get-FileHash (Join-Path $dist $_) -Algorithm SHA256).Hash.ToLower() + "  " + $_ }
    [IO.File]::WriteAllText((Join-Path $dist "SHA256SUMS.txt"), ($sums -join "`n") + "`n")
    [IO.File]::WriteAllText((Join-Path $dist "notes.md"), $Notes, (New-Object Text.UTF8Encoding($false)))
    $sums | ForEach-Object { Write-Host "  $_" }
    Write-Host "Plugin $Version, firmware $fw"

    # git et gh écrivent leur progression sur stderr : on les juge sur le code de sortie
    $ErrorActionPreference = "Continue"

    Step "Commit, tag et push"
    git add -A 2>&1 | Out-Null
    if (git status --porcelain) {
        git commit -q -m "Release $tag" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "Le commit a échoué" }
    }
    git tag -a $tag -m "Release $tag (firmware $fw)"
    if ($LASTEXITCODE -ne 0) { Fail "La création du tag a échoué" }
    git push -q origin HEAD --follow-tags 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "Le push a échoué" }

    Step "Release GitHub"
    $assets = @($files + "SHA256SUMS.txt") | ForEach-Object { Join-Path $dist $_ }
    gh release create $tag @assets --title "$tag (firmware $fw)" --notes-file (Join-Path $dist "notes.md") 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "La création de la release a échoué" }
    $ErrorActionPreference = "Stop"

    if ($Deploy) {
        Step "Installation locale"
        if (Get-Process SimHubWPF -ErrorAction SilentlyContinue) { Write-Host "SimHub est ouvert : installation locale ignorée" -ForegroundColor Yellow }
        else { Copy-Item $built $simhub -Force; Write-Host "Installé dans $simhub" }
    }

    Write-Host "`nPublié : $tag" -ForegroundColor Green
    gh release view $tag --json url --jq .url
}
finally {
    Pop-Location
}
