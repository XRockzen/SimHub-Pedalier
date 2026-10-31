<#
  Compile le firmware du pédalier en fichier .hex, sans l'IDE Arduino, avec la chaîne
  avr-gcc installée par l'IDE (Arduino15) et les bibliothèques du carnet de croquis.
  Mêmes réglages que la carte "Pedalier DIY (Pro Micro 5V)" : nom USB "Pedalier DIY", PID 0x80F0.

    .\build_firmware.ps1                 -> firmware\build\Pedalier.hex
    .\build_firmware.ps1 -Out <dossier>  -> <dossier>\Pedalier.hex

  Affiche la version (FW_VERSION du .ino) et l'occupation flash / RAM.
#>
param([string]$Out = "")
# "Continue" : avr-gcc écrit ses avertissements sur stderr, ce qui n'est pas une erreur.
# Les échecs sont jugés sur le code de sortie, et "throw" arrête toujours le script.
$ErrorActionPreference = "Continue"
$root     = $PSScriptRoot
$a15      = Join-Path $env:LOCALAPPDATA "Arduino15\packages\arduino"
$bin      = (Get-ChildItem "$a15\tools\avr-gcc" -Directory | Sort-Object Name | Select-Object -Last 1).FullName + "\bin"
$hw       = (Get-ChildItem "$a15\hardware\avr" -Directory | Sort-Object Name | Select-Object -Last 1).FullName
# Carnet de croquis : celui de l'IDE 2 (arduino-cli.yaml), sinon Documents\Arduino
$sketchbook = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Arduino"
$cliYaml = Join-Path $env:USERPROFILE ".arduinoIDE\arduino-cli.yaml"
if (Test-Path $cliYaml) {
    $m = [regex]::Match([IO.File]::ReadAllText($cliYaml), '(?m)^\s+user:\s*(.+?)\s*$')
    if ($m.Success) { $sketchbook = $m.Groups[1].Value }
}
$libs     = Join-Path $sketchbook "libraries"
$nau      = "$libs\SparkFun_Qwiic_Scale_NAU7802_Arduino_Library\src"
$joy      = "$libs\Joystick\src"
$sketch   = Join-Path $root "Pedalier\Pedalier.ino"
$build    = Join-Path $root "build\obj"
if (-not $Out) { $Out = Join-Path $root "build" }

foreach ($p in "$bin\avr-gcc.exe", $hw, $nau, $joy, $sketch) { if (-not (Test-Path $p)) { throw "Introuvable : $p" } }
Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $build, $Out | Out-Null

# Identité USB dans un fichier de réponse lu par gcc : PowerShell 5.1 abîme les guillemets
# intérieurs des arguments passés à un programme externe.
$usbRsp = Join-Path $build "usb.rsp"
[IO.File]::WriteAllText($usbRsp, "-DUSB_VID=0x2341 -DUSB_PID=0x80F0 '-DUSB_MANUFACTURER=`"DIY`"' '-DUSB_PRODUCT=`"Pedalier DIY`"'")
$flags = @("-mmcu=atmega32u4", "-DF_CPU=16000000L", "-DARDUINO=10819", "-DARDUINO_AVR_LEONARDO", "-DARDUINO_ARCH_AVR", "@$usbRsp",
           "-Os", "-ffunction-sections", "-fdata-sections", "-flto", "-Wall", "-Wextra",
           "-I$hw\cores\arduino", "-I$hw\variants\leonardo", "-I$hw\libraries\Wire\src", "-I$hw\libraries\EEPROM\src",
           "-I$hw\libraries\HID\src", "-I$nau", "-I$joy", "-I$joy\DynamicHID")
$cpp = @("-std=gnu++11", "-fno-exceptions", "-fno-threadsafe-statics", "-fpermissive")

function Compile($src, $obj, $extra) {
    # Les avertissements des sources Arduino ne nous concernent pas : seule l'erreur compte
    $outp = & "$bin\avr-gcc.exe" -c @flags @extra $src -o $obj 2>&1
    if ($LASTEXITCODE -ne 0) { $outp | Write-Host; throw "Échec de compilation : $src" }
}

# Cœur Arduino en archive (comme l'IDE : seuls les objets utiles sont liés)
$core = @()
foreach ($f in Get-ChildItem "$hw\cores\arduino" -File) {
    $o = Join-Path $build ("core_" + $f.Name + ".o")
    switch ($f.Extension) {
        ".c"   { Compile $f.FullName $o @("-std=gnu11"); $core += $o }
        ".cpp" { Compile $f.FullName $o ($cpp + "-x" + "c++"); $core += $o }
        ".S"   { Compile $f.FullName $o @("-x", "assembler-with-cpp"); $core += $o }
    }
}
& "$bin\avr-gcc-ar.exe" rcs (Join-Path $build "core.a") @core
if ($LASTEXITCODE -ne 0) { throw "Archive du cœur impossible" }

# Bibliothèques
$objs = @()
$sources = @(Get-ChildItem "$hw\libraries\Wire\src\utility\*.c") + @(Get-ChildItem "$hw\libraries\Wire\src\*.cpp") +
           @(Get-ChildItem "$nau\*.cpp") + @(Get-ChildItem "$joy\*.cpp") + @(Get-ChildItem "$joy\DynamicHID\*.cpp")
foreach ($f in $sources) {
    $o = Join-Path $build ("lib_" + $f.Name + ".o")
    if ($f.Extension -eq ".c") { Compile $f.FullName $o @("-std=gnu11") } else { Compile $f.FullName $o ($cpp + "-x" + "c++") }
    $objs += $o
}

# Croquis : l'IDE ajoute #include <Arduino.h> en tête du .ino
$cppSketch = Join-Path $build "Pedalier.cpp"
[IO.File]::WriteAllText($cppSketch, "#include <Arduino.h>`n" + [IO.File]::ReadAllText($sketch), (New-Object Text.UTF8Encoding($false)))
$warn = & "$bin\avr-gcc.exe" -c @flags @cpp -x c++ $cppSketch -o (Join-Path $build "sketch.o") 2>&1
if ($LASTEXITCODE -ne 0) { $warn | Write-Host; throw "Échec de compilation du croquis" }
$warn | Where-Object { "$_" -match "Pedalier\.cpp:\d+" } | ForEach-Object { Write-Host "AVERTISSEMENT : $_" -ForegroundColor Yellow }

$elf = Join-Path $build "Pedalier.elf"
& "$bin\avr-gcc.exe" -mmcu=atmega32u4 -Os -flto -fuse-linker-plugin "-Wl,--gc-sections" (Join-Path $build "sketch.o") @objs (Join-Path $build "core.a") -o $elf -lm
if ($LASTEXITCODE -ne 0) { throw "Échec de l'édition de liens" }
$hex = Join-Path $Out "Pedalier.hex"
& "$bin\avr-objcopy.exe" -O ihex -R .eeprom $elf $hex
if ($LASTEXITCODE -ne 0) { throw "Création du .hex impossible" }

$size = & "$bin\avr-size.exe" -C --mcu=atmega32u4 $elf | Where-Object { $_ -match "Program|Data" }
$flash = [int]([regex]::Match(($size -join " "), "Program:\s+(\d+)").Groups[1].Value)
if ($flash -gt 28672) { throw "Firmware trop gros : $flash octets (28672 maximum)" }
$version = [regex]::Match([IO.File]::ReadAllText($sketch), '#define\s+FW_VERSION\s+"([^"]+)"').Groups[1].Value
Write-Host "Firmware $version -> $hex" -ForegroundColor Green
$size | ForEach-Object { Write-Host "  $_" }
