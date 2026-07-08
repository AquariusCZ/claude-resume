<#
  install.ps1 - set up Claude Resume: icon, Desktop shortcut, and the Scheduled Task
  that runs the checker every 2 minutes. Safe to re-run.
#>
$ErrorActionPreference = 'Stop'
$AppDir = Join-Path $env:LOCALAPPDATA 'ClaudeResume'

# 1) allow local scripts (RemoteSigned) for both PowerShell editions if needed
try { if((Get-ExecutionPolicy -Scope CurrentUser) -in @('Restricted','Undefined','AllSigned')){ Set-ExecutionPolicy -Scope CurrentUser RemoteSigned -Force } } catch {}

# 2) generate a coral "resume" icon (rounded square + white play triangle)
$IcoPath = Join-Path $AppDir 'icon.ico'
try {
  Add-Type -AssemblyName System.Drawing
  $sz = 256; $bmp = New-Object System.Drawing.Bitmap $sz,$sz
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode='AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $r=52; $d=$r*2; $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0,0,$d,$d,180,90); $path.AddArc($sz-$d,0,$d,$d,270,90); $path.AddArc($sz-$d,$sz-$d,$d,$d,0,90); $path.AddArc(0,$sz-$d,$d,$d,90,90); $path.CloseFigure()
  $c1=[System.Drawing.ColorTranslator]::FromHtml('#e5793f'); $c2=[System.Drawing.ColorTranslator]::FromHtml('#bd3e16')
  $rect = New-Object System.Drawing.Rectangle 0,0,$sz,$sz
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,$c1,$c2,60; $g.FillPath($brush,$path)
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $tri = @([System.Drawing.Point]::new(98,80),[System.Drawing.Point]::new(98,176),[System.Drawing.Point]::new(180,128))
  $g.FillPolygon($white, $tri)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); $png=$ms.ToArray()
  $io = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $io
  $w.Write([UInt16]0);$w.Write([UInt16]1);$w.Write([UInt16]1);$w.Write([Byte]0);$w.Write([Byte]0);$w.Write([Byte]0);$w.Write([Byte]0);$w.Write([UInt16]1);$w.Write([UInt16]32);$w.Write([UInt32]$png.Length);$w.Write([UInt32]22);$w.Write($png);$w.Flush()
  [System.IO.File]::WriteAllBytes($IcoPath,$io.ToArray())
} catch { $IcoPath = $null }

# 3) Desktop shortcut -> wscript launcher.vbs (AV-safe, opens the GUI)
$wsh = New-Object -ComObject WScript.Shell
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude续跑.lnk'
$sc = $wsh.CreateShortcut($lnk)
$sc.TargetPath = Join-Path $env:SystemRoot 'System32\wscript.exe'
$sc.Arguments = '"' + (Join-Path $AppDir 'launcher.vbs') + '"'
$sc.WorkingDirectory = $AppDir
if($IcoPath){ $sc.IconLocation = "$IcoPath,0" }
$sc.WindowStyle = 1
$sc.Description = 'Claude Resume - pick projects to auto-continue after the usage limit resets'
$sc.Save()

# 4) Scheduled Task: run the checker every 2 minutes (paths have no spaces -> no quote hell)
$tr = "wscript.exe $AppDir\checker-launch.vbs"
& schtasks /Create /F /TN 'ClaudeResumeChecker' /SC MINUTE /MO 2 /TR $tr | Out-Null

# 5) start disarmed (the GUI's "布防" button arms it)
. (Join-Path $AppDir 'lib.ps1')
$cfg = Get-CcuConfig; $cfg.enabled = $false; $cfg.armed = $false; Set-CcuConfig $cfg

Write-Host "Claude Resume installed." -ForegroundColor Green
Write-Host ("  Desktop shortcut: " + $lnk)
Write-Host ("  Scheduled task  : ClaudeResumeChecker (every 2 min)")
Write-Host ("  App folder      : " + $AppDir)
