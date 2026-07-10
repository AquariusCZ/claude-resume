<#
  picker.ps1 - "Claude Resume" GUI (config + monitor). WPF/XAML, Windows PowerShell 5.1 STA.
  The GUI never does long work: it selects projects, writes config, and monitors the
  Scheduled-Task checker via state.json + the log. -RenderTo <png> snapshots headless.
  Save UTF-8 WITH BOM.
#>
param([string]$RenderTo = '', [switch]$SelfTest)
Set-StrictMode -Off
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Drawing, System.Windows.Forms

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize" Width="900" Height="650" WindowStartupLocation="CenterScreen"
        FontFamily="Segoe UI, Microsoft YaHei">
  <Window.Resources>
    <SolidColorBrush x:Key="Card" Color="#FF1F1F1D"/>
    <SolidColorBrush x:Key="CardHover" Color="#FF262624"/>
    <SolidColorBrush x:Key="Ink" Color="#FFFFFFFF"/>
    <SolidColorBrush x:Key="Ink2" Color="#FFC3C2B7"/>
    <SolidColorBrush x:Key="Muted" Color="#FF8F8D86"/>
    <SolidColorBrush x:Key="Border0" Color="#1AFFFFFF"/>
    <SolidColorBrush x:Key="Accent" Color="#FFD1602F"/>
    <SolidColorBrush x:Key="AccentSoft" Color="#1FE8763F"/>
    <SolidColorBrush x:Key="Green" Color="#FF0CA30C"/>
    <SolidColorBrush x:Key="Danger" Color="#FFE66767"/>
    <LinearGradientBrush x:Key="AccentGrad" StartPoint="0,0" EndPoint="1,1">
      <GradientStop Color="#FFE8763F" Offset="0"/><GradientStop Color="#FFC0451C" Offset="1"/>
    </LinearGradientBrush>
    <Style x:Key="Chk" TargetType="CheckBox">
      <Setter Property="Template"><Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <Border x:Name="bx" Width="22" Height="22" CornerRadius="6" Background="#FF141413" BorderBrush="{StaticResource Border0}" BorderThickness="1.5">
            <Viewbox Width="12" Height="12" HorizontalAlignment="Center" VerticalAlignment="Center">
              <Path x:Name="ck" Data="M0,5 L4,9 L11,1" Stroke="White" StrokeThickness="1.8" Visibility="Collapsed"
                    StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
            </Viewbox>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="bx" Property="Background" Value="{StaticResource Accent}"/>
              <Setter TargetName="bx" Property="BorderBrush" Value="{StaticResource Accent}"/>
              <Setter TargetName="ck" Property="Visibility" Value="Visible"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key="BtnPrimary" TargetType="Button">
      <Setter Property="Foreground" Value="White"/><Setter Property="FontWeight" Value="SemiBold"/><Setter Property="FontSize" Value="13.5"/><Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template"><Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="b" CornerRadius="10" Background="{StaticResource Accent}" Padding="20,0" Height="42"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{StaticResource AccentGrad}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key="BtnGhost" TargetType="Button">
      <Setter Property="Foreground" Value="{StaticResource Ink2}"/><Setter Property="FontSize" Value="13"/><Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template"><Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="b" CornerRadius="10" Background="Transparent" BorderBrush="{StaticResource Border0}" BorderThickness="1" Padding="16,0" Height="42"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
          <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{StaticResource Accent}"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key="LinkBtn" TargetType="Button">
      <Setter Property="Foreground" Value="{StaticResource Muted}"/><Setter Property="FontSize" Value="12"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Background" Value="Transparent"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border Background="Transparent" Padding="4,2"><ContentPresenter VerticalAlignment="Center"/></Border></ControlTemplate></Setter.Value></Setter>
      <Style.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{StaticResource Accent}"/></Trigger></Style.Triggers>
    </Style>
  </Window.Resources>

  <Border CornerRadius="16" Background="#FF0D0D0D" BorderBrush="{StaticResource Border0}" BorderThickness="1">
    <Border.Effect><DropShadowEffect Color="#000000" Direction="270" ShadowDepth="10" BlurRadius="34" Opacity="0.5"/></Border.Effect>
    <Grid Margin="22">
      <Grid.RowDefinitions>
        <RowDefinition Height="40"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="150"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
      </Grid.RowDefinitions>
      <Grid x:Name="TitleBar" Grid.Row="0" Background="Transparent">
        <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
          <Border Width="32" Height="32" CornerRadius="9" Background="{StaticResource AccentGrad}"><TextBlock Text="R" Foreground="White" FontWeight="Bold" FontSize="16" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
          <TextBlock Text="Claude Resume" Foreground="{StaticResource Ink}" FontWeight="SemiBold" FontSize="13.5" VerticalAlignment="Center" Margin="10,0,0,0"/>
        </StackPanel>
        <Button x:Name="BtnClose" Content="&#xE8BB;" FontFamily="Segoe MDL2 Assets" FontSize="11" Foreground="{StaticResource Muted}" HorizontalAlignment="Right" Width="30" Height="30" Cursor="Hand" Background="Transparent" BorderThickness="0"/>
        <Button x:Name="BtnMin" Content="&#xE921;" FontFamily="Segoe MDL2 Assets" FontSize="11" Foreground="{StaticResource Muted}" HorizontalAlignment="Right" Margin="0,0,38,0" Width="30" Height="30" Cursor="Hand" Background="Transparent" BorderThickness="0"/>
      </Grid>
      <Grid Grid.Row="1" Margin="0,10,0,14">
        <StackPanel>
          <TextBlock Text="选择要自动续跑的项目" Foreground="{StaticResource Ink}" FontWeight="SemiBold" FontSize="20"/>
          <TextBlock x:Name="Subtitle" Text="勾选一个或多个,额度重置后自动继续" Foreground="{StaticResource Muted}" FontSize="12.5" Margin="0,3,0,0"/>
        </StackPanel>
        <Border x:Name="ResetChip" ToolTip="估算值(基于本地记录),精确重置时间以 claude.ai / 扩展为准。工具靠实时探测触发续跑,不依赖此估算数字。" HorizontalAlignment="Right" VerticalAlignment="Center" CornerRadius="999" Background="{StaticResource AccentSoft}" Padding="14,7">
          <TextBlock x:Name="ResetText" Text="读取中..." Foreground="{StaticResource Accent}" FontSize="12.5" FontWeight="SemiBold"/>
        </Border>
      </Grid>
      <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto"><StackPanel x:Name="ProjectList"/></ScrollViewer>
      <Border Grid.Row="3" CornerRadius="16" Background="#FF141413" Margin="0,14,0,0" Padding="14,10">
        <DockPanel>
          <TextBlock DockPanel.Dock="Top" Text="运行日志" Foreground="{StaticResource Muted}" FontSize="11" FontWeight="SemiBold" Margin="0,0,0,6"/>
          <ScrollViewer x:Name="LogScroll" VerticalScrollBarVisibility="Auto"><TextBlock x:Name="LogText" FontFamily="Cascadia Code, Consolas" FontSize="11.5" Foreground="{StaticResource Ink2}" TextWrapping="Wrap"/></ScrollViewer>
        </DockPanel>
      </Border>
      <Grid Grid.Row="4" Margin="0,16,0,0">
        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
        <!-- DockPanel (not StackPanel): StatusText fills the finite leftover width, so long
             flash messages truncate with an ellipsis instead of running under the buttons -->
        <DockPanel Grid.Column="0" VerticalAlignment="Center" Margin="0,0,14,0" LastChildFill="True">
          <Button x:Name="BtnAll" DockPanel.Dock="Left" Style="{StaticResource LinkBtn}" Content="全选" VerticalAlignment="Center"/>
          <Button x:Name="BtnNone" DockPanel.Dock="Left" Style="{StaticResource LinkBtn}" Content="取消勾选" Margin="14,0,0,0" VerticalAlignment="Center"/>
          <Button x:Name="BtnAdd" DockPanel.Dock="Left" Style="{StaticResource LinkBtn}" Content="+ 文件夹" Margin="14,0,0,0" VerticalAlignment="Center"/>
          <Button x:Name="BtnClearLog" DockPanel.Dock="Left" Style="{StaticResource LinkBtn}" Content="清空日志" Margin="14,0,0,0" VerticalAlignment="Center"/>
          <Button x:Name="BtnExportLog" DockPanel.Dock="Left" Style="{StaticResource LinkBtn}" Content="导出日志" Margin="14,0,0,0" VerticalAlignment="Center"/>
          <TextBlock x:Name="StatusText" Text="待布防" Foreground="{StaticResource Muted}" FontSize="12.5" VerticalAlignment="Center" Margin="18,0,0,0" TextTrimming="CharacterEllipsis"/>
        </DockPanel>
        <StackPanel Grid.Column="1" Orientation="Horizontal">
          <Button x:Name="BtnPreview" Style="{StaticResource BtnGhost}" Content="预演" Width="88"/>
          <Button x:Name="BtnDisarm" Style="{StaticResource BtnGhost}" Content="解除" Width="88" Margin="10,0,0,0"/>
          <Button x:Name="BtnArm" Style="{StaticResource BtnPrimary}" Content="布防 (等重置续跑)" Width="180" Margin="10,0,0,0"/>
        </StackPanel>
      </Grid>
      <TextBlock x:Name="FooterPath" Grid.Row="5" Margin="2,12,2,0" FontSize="11" Foreground="{StaticResource Muted}"
                 TextTrimming="CharacterEllipsis" Cursor="Hand" ToolTip="点击打开项目文件夹"/>
    </Grid>
  </Border>
</Window>
'@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$win = [Windows.Markup.XamlReader]::Load($reader)
$els = @{}
foreach($n in 'TitleBar','BtnClose','BtnMin','Subtitle','ResetText','ResetChip','ProjectList','LogText','LogScroll','StatusText','BtnAll','BtnNone','BtnAdd','BtnClearLog','BtnExportLog','BtnPreview','BtnDisarm','BtnArm','FooterPath'){ $els[$n] = $win.FindName($n) }
# global UI-thread exception guard: never let a handler bug close the window
$win.Dispatcher.add_UnhandledException({ param($s,$e)
  try { [System.IO.File]::AppendAllText((Join-Path $env:LOCALAPPDATA 'ClaudeResume\logs\gui-error.log'), ((Get-Date).ToString('s') + "  " + $e.Exception.ToString() + "`r`n"), (New-Object System.Text.UTF8Encoding($false))) } catch {}
  $e.Handled = $true
})

$sync = [hashtable]::Synchronized(@{ resetUtc=$null; hasActivity=$false; ok=$false; realResetUtc=$null; probeLimited=$false })
$script:cards = @()
$script:flash = @{ text=''; until=[datetime]::MinValue }
function Set-Flash($t){ $script:flash.text = $t; $script:flash.until = (Get-Date).AddSeconds(6) }
$script:logFile = Join-Path $script:LogDir ("run-" + (Get-Date).ToString('yyyyMMdd') + ".log")
function Read-LogTail { try { if(Test-Path $script:logFile){ return ((Get-Content $script:logFile -Tail 40 -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`r`n") } } catch {} return '' }

function New-ProjectCard($proj){
  $b = New-Object Windows.Controls.Border
  $b.CornerRadius='16'; $b.Padding='16,13'; $b.Margin='0,0,0,10'
  $b.Background=$win.FindResource('Card'); $b.BorderBrush=$win.FindResource('Border0'); $b.BorderThickness='1'
  $dp = New-Object Windows.Controls.DockPanel
  $chk = New-Object Windows.Controls.CheckBox
  $chk.Style=$win.FindResource('Chk'); $chk.VerticalAlignment='Center'; [Windows.Controls.DockPanel]::SetDock($chk,'Left'); $chk.Margin='0,0,14,0'
  $right = New-Object Windows.Controls.TextBlock
  $right.Text= $(if($proj.lastUsedUtc){ $proj.lastUsedUtc.ToLocalTime().ToString('MM-dd HH:mm') } else { '' })
  $right.Foreground=$win.FindResource('Muted'); $right.FontSize=11.5; $right.VerticalAlignment='Center'; [Windows.Controls.DockPanel]::SetDock($right,'Right')
  $rm = New-Object Windows.Controls.TextBlock
  $rm.Text=[string][char]0x2715; $rm.Foreground=$win.FindResource('Muted'); $rm.FontSize=13; $rm.Cursor='Hand'; $rm.VerticalAlignment='Center'; $rm.Margin='16,0,2,0'; $rm.ToolTip='从列表移除'
  [Windows.Controls.DockPanel]::SetDock($rm,'Right')
  $rm.Add_MouseEnter({ $rm.Foreground=$win.FindResource('Danger') }.GetNewClosure())
  $rm.Add_MouseLeave({ $rm.Foreground=$win.FindResource('Muted') }.GetNewClosure())
  $rm.Add_MouseLeftButtonUp({ param($s,$e) $e.Handled=$true; Remove-ProjectCard $proj.path }.GetNewClosure())
  $sp = New-Object Windows.Controls.StackPanel
  $nameRow = New-Object Windows.Controls.StackPanel; $nameRow.Orientation='Horizontal'
  $nm = New-Object Windows.Controls.TextBlock; $nm.Text=$proj.name; $nm.Foreground=$win.FindResource('Ink'); $nm.FontWeight='SemiBold'; $nm.FontSize=14
  $nameRow.Children.Add($nm) | Out-Null
  if($proj.isGit){
    $badge = New-Object Windows.Controls.Border; $badge.CornerRadius='999'; $badge.Background=$win.FindResource('AccentSoft'); $badge.Padding='7,1'; $badge.Margin='8,0,0,0'; $badge.VerticalAlignment='Center'
    $bt = New-Object Windows.Controls.TextBlock; $bt.Text='git'; $bt.Foreground=$win.FindResource('Accent'); $bt.FontSize=10; $badge.Child=$bt; $nameRow.Children.Add($badge) | Out-Null
  }
  $pt = New-Object Windows.Controls.TextBlock; $pt.Text=$proj.path; $pt.Foreground=$win.FindResource('Muted'); $pt.FontSize=12; $pt.TextTrimming='CharacterEllipsis'; $pt.Margin='0,2,0,0'
  $sp.Children.Add($nameRow) | Out-Null; $sp.Children.Add($pt) | Out-Null
  $dp.Children.Add($chk) | Out-Null; $dp.Children.Add($rm) | Out-Null; $dp.Children.Add($right) | Out-Null; $dp.Children.Add($sp) | Out-Null
  $b.Child=$dp
  $b.Add_MouseLeftButtonUp({ $chk.IsChecked = -not $chk.IsChecked }.GetNewClosure())
  $b.Add_MouseEnter({ $b.Background = $win.FindResource('CardHover') }.GetNewClosure())
  $b.Add_MouseLeave({ $b.Background = $win.FindResource('Card') }.GetNewClosure())
  return [pscustomobject]@{ border=$b; check=$chk; proj=$proj }
}
function Add-ProjectCard($proj, [bool]$check){
  foreach($c in $script:cards){ if($c.proj.path -eq $proj.path){ if($check){ $c.check.IsChecked=$true }; return } }
  $card = New-ProjectCard $proj
  $script:cards += $card
  $els.ProjectList.Children.Add($card.border) | Out-Null
  if($check){ $card.check.IsChecked = $true }
}

function Remove-ProjectCard($path){
  $card = $script:cards | Where-Object { $_.proj.path -eq $path } | Select-Object -First 1
  if(-not $card){ return }
  $els.ProjectList.Children.Remove($card.border)
  $script:cards = @($script:cards | Where-Object { $_.proj.path -ne $path })
  $c = Get-CcuConfig
  $cust = @(); if($c.customProjects){ $cust=@($c.customProjects) }
  if($cust | Where-Object { $_.path -eq $path }){
    $c.customProjects = @($cust | Where-Object { $_.path -ne $path })
  } else {
    $hid = @(); if($c.hiddenProjects){ $hid=@($c.hiddenProjects) }
    if($hid -notcontains $path){ $hid += $path }
    $c | Add-Member -NotePropertyName hiddenProjects -NotePropertyValue $hid -Force
  }
  Set-CcuConfig $c
  Set-Flash "已移除: $(Split-Path $path -Leaf)"
}

# ---- discover + merge custom folders (minus hidden) ----
$cfg = Get-CcuConfig
$hidden = @(); if($cfg.hiddenProjects){ $hidden = @($cfg.hiddenProjects) }
$discovered = @(Get-ClaudeProjects | Where-Object { $hidden -notcontains $_.path })
$all = @($discovered)
if($cfg.customProjects){
  foreach($cp in @($cfg.customProjects)){
    if($cp.path -and (Test-Path $cp.path) -and ($hidden -notcontains $cp.path) -and -not ($all | Where-Object { $_.path -eq $cp.path })){
      $all += [pscustomobject]@{ name=(Split-Path $cp.path -Leaf); path=$cp.path; sessionId=$null;
        lastUsedUtc=(Get-Item $cp.path -ErrorAction SilentlyContinue).LastWriteTimeUtc; isGit=(Test-Path (Join-Path $cp.path '.git')); folder='' }
    }
  }
}
foreach($p in $all){ Add-ProjectCard $p $false }
$selPaths = @(); if($cfg.selected){ $selPaths = @($cfg.selected | ForEach-Object { $_.path }) }
foreach($c in $script:cards){ if($selPaths -contains $c.proj.path){ $c.check.IsChecked = $true } }
$els.Subtitle.Text = "发现 $($all.Count) 个项目 · 勾选一个或多个,或点 + 文件夹 手动添加"

# project folder (source + docs) shown in the footer; runtime copy lives in $PSScriptRoot
$projectHome = if($cfg.projectHome){ $cfg.projectHome } else { Join-Path ([Environment]::GetFolderPath('Desktop')) 'claude-resume' }
$els.FooterPath.Text = "📁 项目文件夹: $projectHome    ·    运行副本: $PSScriptRoot"
$els.FooterPath.Add_MouseLeftButtonUp({ $t = if(Test-Path $projectHome){ $projectHome } else { $PSScriptRoot }; Start-Process explorer.exe $t }.GetNewClosure())

# ---- screenshot mode ----
if($RenderTo){
  $win.WindowStartupLocation='Manual'; $win.Left=-12000; $win.Top=-12000; $win.ShowInTaskbar=$false
  $win.Show(); $win.Dispatcher.Invoke([action]{}, [System.Windows.Threading.DispatcherPriority]::Loaded)
  Start-Sleep -Milliseconds 500
  $win.Dispatcher.Invoke([action]{}, [System.Windows.Threading.DispatcherPriority]::ContextIdle)
  # render via VisualBrush: Render($win.Content) directly yields a blank bitmap for a
  # layered (AllowsTransparency) window parked off-screen
  $dv = New-Object Windows.Media.DrawingVisual
  $dc = $dv.RenderOpen()
  $vb = New-Object Windows.Media.VisualBrush $win.Content
  $dc.DrawRectangle($vb, $null, (New-Object Windows.Rect 0,0,900,650))
  $dc.Close()
  $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap 900,650,96,96,([Windows.Media.PixelFormats]::Pbgra32)
  $rtb.Render($dv)
  $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
  $fs = [IO.File]::Open($RenderTo,'Create'); $enc.Save($fs); $fs.Close(); $win.Close(); return
}

function Get-Selected { $script:cards | Where-Object { $_.check.IsChecked } | ForEach-Object { [pscustomobject]@{ name=$_.proj.name; path=$_.proj.path } } }
function Set-StatusLine($t){ $els.StatusText.Text = $t; $els.StatusText.ToolTip = $t }  # tooltip carries the full text when truncated

# ---- events ----
$els.BtnClose.Add_Click({ $win.Close() })
$els.BtnMin.Add_Click({ $win.WindowState='Minimized' })
# manual drag (avoids DragMove's modal loop, which can natively crash a transparent WPF window)
$els.TitleBar.Add_MouseLeftButtonDown({
  try {
    $script:dragMouse = [System.Windows.Forms.Control]::MousePosition
    $script:dragWinL = $win.Left; $script:dragWinT = $win.Top
    $tf = [System.Windows.PresentationSource]::FromVisual($win).CompositionTarget.TransformToDevice
    $script:dpiX = $tf.M11; $script:dpiY = $tf.M22
    $script:dragging = $true
    [void]$els.TitleBar.CaptureMouse()
  } catch {}
})
$els.TitleBar.Add_MouseMove({
  try {
    if($script:dragging){
      $cur = [System.Windows.Forms.Control]::MousePosition
      $win.Left = $script:dragWinL + ($cur.X - $script:dragMouse.X) / $script:dpiX
      $win.Top  = $script:dragWinT + ($cur.Y - $script:dragMouse.Y) / $script:dpiY
    }
  } catch {}
})
$els.TitleBar.Add_MouseLeftButtonUp({ try { $script:dragging = $false; $els.TitleBar.ReleaseMouseCapture() } catch {} })
$els.BtnAll.Add_Click({ foreach($c in $script:cards){ $c.check.IsChecked=$true } })
$els.BtnNone.Add_Click({ foreach($c in $script:cards){ $c.check.IsChecked=$false } })
$els.BtnAdd.Add_Click({
  try {
    $shell = New-Object -ComObject Shell.Application
    $folder = $shell.BrowseForFolder(0, '选择要加入的项目文件夹', 0)
    if($folder -and $folder.Self -and $folder.Self.Path){
      $path = $folder.Self.Path
      if(-not (Test-Path (Join-Path $path '*') -PathType Container) -and -not (Test-Path $path)){ Set-Flash '无效文件夹'; return }
      $proj = [pscustomobject]@{ name=(Split-Path $path -Leaf); path=$path; sessionId=$null;
        lastUsedUtc=(Get-Item $path -ErrorAction SilentlyContinue).LastWriteTimeUtc; isGit=(Test-Path (Join-Path $path '.git')); folder='' }
      Add-ProjectCard $proj $true
      $c = Get-CcuConfig
      $cust = @(); if($c.customProjects){ $cust=@($c.customProjects) }
      if(-not ($cust | Where-Object { $_.path -eq $path })){ $cust += [pscustomobject]@{ name=$proj.name; path=$path } }
      $c.customProjects = $cust; Set-CcuConfig $c
      Set-Flash "已添加并勾选: $($proj.name)"
    }
  } catch { Set-Flash ('添加出错: ' + $_.Exception.Message) }
})
$els.BtnArm.Add_Click({
  try {
    $sel = @(Get-Selected)
    if($sel.Count -eq 0){ Set-Flash '请先勾选至少一个项目'; return }
    $c = Get-CcuConfig; $c.enabled=$true; $c.armed=$true; $c.selected=$sel; $c.skipPermissions=$true; $c.dirtyGuard='stash'; Set-CcuConfig $c
    # fresh cycle: stale sawLimited would fire instantly, stale projectStatus would skip projects
    $st = Get-CcuState; $st.sawLimited=$false; $st.projectStatus=@{}; $st.phase='waiting'; $st.firedForId=$null; Set-CcuState $st
    Set-Flash "已布防 · 监视 $($sel.Count) 个项目,重置后自动续跑"
  } catch { Set-Flash ('布防出错: ' + $_.Exception.Message) }
})
$els.BtnDisarm.Add_Click({
  try { $c=Get-CcuConfig; $c.enabled=$false; $c.armed=$false; Set-CcuConfig $c; Set-Flash '已解除布防(全局停用)' }
  catch { Set-Flash ('解除出错: ' + $_.Exception.Message) }
})
$els.BtnClearLog.Add_Click({
  try {
    if(Test-Path $script:logFile){ [System.IO.File]::WriteAllText($script:logFile, '') }
    $els.LogText.Text = ''
    Set-Flash '日志已清空'
  } catch { Set-Flash ('清空出错: ' + $_.Exception.Message) }
})
$els.BtnExportLog.Add_Click({
  try {
    # every run-*.log (oldest first) + the GUI error log, merged into one shareable file
    $files = @()
    if(Test-Path $script:LogDir){ $files = @(Get-ChildItem $script:LogDir -Filter 'run-*.log' -ErrorAction SilentlyContinue | Sort-Object Name) }
    $guiErr = Join-Path $script:LogDir 'gui-error.log'
    if(Test-Path $guiErr){ $files += Get-Item $guiErr }
    $files = @($files | Where-Object { $_.Length -gt 0 })
    if($files.Count -eq 0){ Set-Flash '没有可导出的日志'; return }
    $dlg = New-Object System.Windows.Forms.SaveFileDialog
    $dlg.Title = '导出运行日志'
    $dlg.FileName = 'Claude续跑日志-' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.log'
    $dlg.InitialDirectory = [Environment]::GetFolderPath('Desktop')
    $dlg.Filter = '日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*'
    if($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK){ Set-Flash '已取消导出'; return }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('Claude Resume 日志导出 · ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
    foreach($f in $files){
      [void]$sb.AppendLine(''); [void]$sb.AppendLine('===== ' + $f.Name + ' =====')
      try { [void]$sb.AppendLine([System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8).TrimEnd()) }
      catch { [void]$sb.AppendLine('(读取失败: ' + $_.Exception.Message + ')') }   # one bad file must not abort the export
    }
    # UTF-8 WITH BOM so Chinese text opens correctly in any editor
    [System.IO.File]::WriteAllText($dlg.FileName, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
    Start-Process explorer.exe ('/select,"' + $dlg.FileName + '"')
    Set-Flash ('已导出: ' + (Split-Path $dlg.FileName -Leaf))
  } catch { Set-Flash ('导出出错: ' + $_.Exception.Message) }
})
$els.BtnPreview.Add_Click({
  try {
    $sel = @(Get-Selected); $c = Get-CcuConfig; $c.selected=$sel; Set-CcuConfig $c
    Set-Flash '预演中(只算不跑)...'
    # a marker guarantees a fresh visible line even if the checker path changes
    Write-CcuLog ('----- 预演 @ ' + (Get-Date).ToString('HH:mm:ss') + '  (' + $sel.Count + ' 个项目已选) -----') 'info'
    Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'checker.ps1'),'-DryRun' -WindowStyle Hidden -Wait
    $els.LogText.Text = Read-LogTail; $els.LogScroll.ScrollToEnd()
    Set-Flash '预演完成 · 见下方日志'
  } catch { Set-Flash ('预演出错: ' + $_.Exception.Message) }
})

# ---- background runspace: only the slow ccusage read ----
$rs = [runspacefactory]::CreateRunspace(); $rs.ApartmentState='MTA'; $rs.ThreadOptions='ReuseThread'; $rs.Open()
$rs.SessionStateProxy.SetVariable('sync',$sync)
$rs.SessionStateProxy.SetVariable('libPath',(Join-Path $PSScriptRoot 'lib.ps1'))
$ps = [powershell]::Create(); $ps.Runspace=$rs
[void]$ps.AddScript({
  . $libPath
  $lastProbe=[datetime]::MinValue; $myReset=$null; $myResetAt=[datetime]::MinValue
  while($true){
    $nowU=[DateTimeOffset]::UtcNow
    try { $sr=Get-SessionReset; $sync.ok=$sr.ok; $sync.hasActivity=$sr.hasActivity; $sync.resetUtc=$sr.resetUtc } catch { $sr=$null }
    $estSecs=$null; if($sr -and $sr.ok -and $null -ne $sr.secondsUntilReset){ $estSecs=[double]$sr.secondsUntilReset }
    # Own live probe for the EXACT reset: only worth it near reset (the server sends resetsAt once a
    # window is >~75% used), once on open then every 5 min. Kept in memory -> no state write, no race
    # with the checker. A probe is a quick haiku call: tiny quota, and free while actually rate-limited.
    $worth = $sync.hasActivity -and (($null -eq $estSecs) -or ($estSecs -le 5400))
    if($worth -and ((Get-Date)-$lastProbe).TotalMinutes -ge 5){
      $lastProbe=Get-Date
      try {
        $cfg=Get-CcuConfig; $pr=Test-ClaudeReady -Model $cfg.probeModel
        $sync.probeLimited=($pr.reason -eq 'limited')
        if($pr.fiveHourResetUtc){ $myReset=$pr.fiveHourResetUtc; $myResetAt=Get-Date }
      } catch {}
    }
    # exact reset the checker's own probes cached (free read), used when we have no fresher one
    $stReset=$null
    try {
      $st=Get-CcuState
      if($st.realFiveHourResetUtc -and $st.realResetProbedUtc){
        $rr=[DateTimeOffset]::FromUnixTimeSeconds([long]$st.realFiveHourResetUtc)
        $pb=[DateTimeOffset]::FromUnixTimeSeconds([long]$st.realResetProbedUtc)
        if(($nowU-$pb).TotalHours -lt 5){ $stReset=$rr }
      }
    } catch {}
    # prefer our own fresh probe, fall back to the checker's cache; drop any value already in the past
    $best=$null
    if($myReset -and ((Get-Date)-$myResetAt).TotalHours -lt 5 -and $myReset -gt $nowU){ $best=$myReset }
    elseif($stReset -and $stReset -gt $nowU){ $best=$stReset }
    $sync.realResetUtc=$best
    Start-Sleep -Seconds 10
  }
})
$hb = $ps.BeginInvoke()

# ---- UI timer: repaint every second (fast local/file reads only) ----
$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(1)
$timer.Add_Tick({
  if($sync.realResetUtc){
    $secs=($sync.realResetUtc-[DateTimeOffset]::UtcNow).TotalSeconds
    $els.ResetText.Text='距重置 '+(Format-Countdown $secs)+' · 精确'
    if($els.ResetChip){ $els.ResetChip.ToolTip='服务器返回的精确重置时间(与 claude 的 /usage 同源),运行时实时探测读到。' }
  } elseif($sync.resetUtc){
    $secs=($sync.resetUtc-[DateTimeOffset]::UtcNow).TotalSeconds
    $els.ResetText.Text='≈ 距重置 '+(Format-Countdown $secs)
    if($els.ResetChip){ $els.ResetChip.ToolTip='估算值(基于本地记录)。接近重置或被限流时会自动探测并切换为服务器精确值。触发续跑始终靠实时探测,不依赖此数字。' }
  } elseif($sync.ok -and -not $sync.hasActivity){ $els.ResetText.Text='额度当前可用' }
  else { $els.ResetText.Text='读取中...' }
  $lt = Read-LogTail
  if($lt -and $els.LogText.Text -ne $lt){ $els.LogText.Text=$lt; $els.LogScroll.ScrollToEnd() }
  if((Get-Date) -lt $script:flash.until){ Set-StatusLine $script:flash.text }
  else {
    $en=$false; try { $en=[bool](Get-CcuConfig).enabled } catch {}
    $ph='idle'; try { $ph="$((Get-CcuState).phase)" } catch {}
    Set-StatusLine (($(if($en){'● 已布防'}else{'○ 未布防'})) + ' · 引擎: ' + $ph)
  }
})
$timer.Start()
$win.Add_Closed({ try { $timer.Stop() } catch {}; try { $ps.Stop(); $rs.Close() } catch {} })
if($SelfTest){ $tt=New-Object Windows.Threading.DispatcherTimer; $tt.Interval=[TimeSpan]::FromMilliseconds(2500); $tt.Add_Tick({ $tt.Stop(); $win.Close() }); $tt.Start() }
[void]$win.ShowDialog()
