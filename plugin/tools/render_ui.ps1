# Rendu hors écran de la page du plugin (sans SimHub ni pédalier), un PNG par onglet.
# Le thème de SimHub est absent : contrôles WPF par défaut (clairs) sur fond sombre, c'est normal.
#   powershell -STA -File tools\render_ui.ps1 -Out <dossier>
param([string]$Out, [int]$W = 1250, [int]$H = 900)
$ErrorActionPreference = "Stop"
$simhub = "F:\Program Files (x86)\SimHub"
$root   = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
foreach ($dll in "log4net", "SimHub.Logging", "GameReaderCommon", "Newtonsoft.Json", "SimHub.Plugins") {
    try { [void][Reflection.Assembly]::LoadFrom("$simhub\$dll.dll") } catch { }
}
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\Release\User.PedalierDIY.dll")
$np  = [Reflection.BindingFlags]"NonPublic,Public,Instance"
New-Item -ItemType Directory -Force $Out | Out-Null

$plugin = [Activator]::CreateInstance($asm.GetType("User.PedalierDIY.PedalierPlugin"))
$link   = [Activator]::CreateInstance($asm.GetType("User.PedalierDIY.PedalLink"))
$plugin.GetType().GetField("Settings", $np).SetValue($plugin, [Activator]::CreateInstance($asm.GetType("User.PedalierDIY.PedalierSettings")))
$plugin.GetType().GetField("Link", $np).SetValue($plugin, $link)
$plugin.GetType().GetField("DataDir", $np).SetValue($plugin, (Join-Path $Out "data"))

# Liaison simulée "connectée" avec des valeurs en direct
$lt = $link.GetType()
$state = [Enum]::Parse($asm.GetType("User.PedalierDIY.LinkState"), "Connected")
$lt.GetField("_state", $np).SetValue($link, $state)
$lt.GetProperty("PortName").SetValue($link, "COM7")
$lt.GetProperty("Firmware").SetValue($link, "1.0.0")
$link.WantConnected = $true
$live = [Activator]::CreateInstance($asm.GetType("User.PedalierDIY.LiveData"))
$live.AccRaw = 3420; $live.AccCal = 5560; $live.AccOut = 2277
$live.BrkRaw = 512000; $live.BrkCal = 3380; $live.BrkOut = 1384; $live.Flags = 3
$link.Live = $live

# Configuration de démonstration (celle du test 1)
$cfg = [Activator]::CreateInstance($asm.GetType("User.PedalierDIY.PedalConfig"))
$lines = "P ACC MIN 3005","P ACC MAX 3705","P ACC DZL 300","P ACC DZH 300","P ACC TAU 4","P ACC HYS 8",
         "P ACC CURVE 0 1000 2000 3000 4000 5000 6000 7000 8000 9000 10000",
         "P BRK MIN 161051","P BRK MAX 1087051","P BRK DZL 200","P BRK DZH 100","P BRK TAU 10","P BRK HYS 4",
         "P BRK CURVE 0 1000 2000 3000 4000 5000 6000 7000 8000 9000 10000",
         "P BRK MED 3","P BRK SPS 320","P BRK TBAND 300","P BRK TMS 2000","P LIN 6 0 2350 4480 6420 8250 10000"
foreach ($l in $lines) { [void]$cfg.ApplyLine($l) }
$cfg.Brk.Curve = $asm.GetType("User.PedalierDIY.Curves").GetMethod("Preset").Invoke($null, @(4))

$ctlType = $asm.GetType("User.PedalierDIY.PedalierControl")
$ctl = $ctlType.GetConstructors($np)[0].Invoke(@($plugin))
$ctlType.GetField("_lastState", $np).SetValue($ctl, $state)
$ctlType.GetField("_device", $np).SetValue($ctl, $cfg.Clone())
$ctlType.GetField("_draft", $np).SetValue($ctl, $cfg)
$ctlType.GetField("_unsaved", $np).SetValue($ctl, $true)
[void]$ctlType.GetMethod("FillUi", $np).Invoke($ctl, $null)

$win = New-Object System.Windows.Window
$win.Width = $W; $win.Height = $H
$win.Background = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0x25, 0x26, 0x2B))
$win.Foreground = [System.Windows.Media.Brushes]::White
$win.Content = $ctl
$win.WindowStyle = "None"; $win.ShowInTaskbar = $false
$win.Left = -5000; $win.Top = 0
$win.Show()

function Pump { $f = New-Object System.Windows.Threading.DispatcherFrame
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke([System.Windows.Threading.DispatcherPriority]::Background, [System.Action]{ $f.Continue = $false }) | Out-Null
    [System.Windows.Threading.Dispatcher]::PushFrame($f) }
function Snap($name) {
    for ($i = 0; $i -lt 6; $i++) { Pump; Start-Sleep -Milliseconds 80 }
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap([int]$win.ActualWidth, [int]$win.ActualHeight, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($win)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $fs = [IO.File]::Create((Join-Path $Out "$name.png")); $enc.Save($fs); $fs.Close()
    "saved $name"
}
$tabs = $ctlType.GetField("_tabs", $np).GetValue($ctl)
$names = "1_direct", "2_calibration", "3_reglages", "4_profils", "5_diagnostic"
for ($i = 0; $i -lt $tabs.Items.Count; $i++) {
    $tabs.SelectedIndex = $i
    if ($i -eq 4) { [void]$ctlType.GetMethod("ShowDiag", $np).Invoke($ctl, @("DIAG FW=1.0.0 CFG=1 SRC=EEPROM ACC_OK=1 AIMANT=OK AGC=207 MAG=2086 BURN=0 ACC_HZ=1007 ERR_ACC=0 BRK_OK=1 BRK_HZ=321 ERR_BRK=0 TARES=11 LIN_OK=1 UPTIME=3725")) }
    Snap $names[$i]
}
$win.Close()
