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

# own taskbar identity (custom icon, no grouping under powershell.exe) + win32 helpers
Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("shell32.dll")] public static extern int SetCurrentProcessExplicitAppUserModelID(string id);
'@
try { [void][Win32.Native]::SetCurrentProcessExplicitAppUserModelID('ClaudeResume.Picker') } catch {}

# single instance: opening it again focuses the existing window instead of stacking copies
# (every extra window runs its own probes and races config.json writes). FindWindow is
# unreliable for WPF layered/transparent windows, so locate the existing window by the
# other picker process's MainWindowHandle instead.
$script:instanceMutex = New-Object System.Threading.Mutex($false, 'Local\ClaudeResumePickerSingleton')
$script:instanceOwned = $false
try { $script:instanceOwned = $script:instanceMutex.WaitOne(0) }
catch [System.Threading.AbandonedMutexException] { $script:instanceOwned = $true }
if(-not $script:instanceOwned -and -not $RenderTo -and -not $SelfTest){
  try {
    $other = Get-Process -Name powershell,pwsh -ErrorAction SilentlyContinue |
             Where-Object { $_.Id -ne $PID -and $_.MainWindowTitle -eq 'Claude Resume' -and $_.MainWindowHandle -ne 0 } |
             Select-Object -First 1
    if($other){ [void][Win32.Native]::ShowWindow($other.MainWindowHandle, 9); [void][Win32.Native]::SetForegroundWindow($other.MainWindowHandle) }  # 9 = SW_RESTORE
  } catch {}
  return
}

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Claude Resume"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize" Width="900" Height="700" WindowStartupLocation="CenterScreen"
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
        <RowDefinition Height="40"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="140"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
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
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center">
          <Border x:Name="ChatModelChip" Cursor="Hand" ToolTip="飞书机器人(聊天 + 项目执行)使用的模型(点击切换 默认 / Sonnet / Opus / Haiku)。与飞书共享同一配置,两边实时同步;被限流时可换个模型重试。" CornerRadius="9" Background="{StaticResource Card}" BorderBrush="{StaticResource Border0}" BorderThickness="1" Padding="13,7" Margin="0,0,8,0">
            <TextBlock x:Name="ChatModelText" Text="模型 默认" Foreground="{StaticResource Ink2}" FontSize="12.5" FontWeight="SemiBold"/>
          </Border>
          <Border x:Name="IntervalChip" Cursor="Hand" ToolTip="布防后每隔多久自动实探一次额度(点击切换 5 / 15 / 30 分钟);被限流后自动加密到 4 分钟。" CornerRadius="9" Background="{StaticResource Card}" BorderBrush="{StaticResource Border0}" BorderThickness="1" Padding="13,7" Margin="0,0,8,0">
            <TextBlock x:Name="IntervalText" Text="间隔 15m" Foreground="{StaticResource Ink2}" FontSize="12.5" FontWeight="SemiBold"/>
          </Border>
          <Border x:Name="ResetChip" Cursor="Hand" ToolTip="5h / 7d 两个额度窗口的实时用量(实探读到)。5h「低」表示还远未接近上限(此时服务器不下发具体百分比);接近或被限流时显示精确倒计时。点击立即重新实探。" VerticalAlignment="Center" CornerRadius="9" Background="{StaticResource AccentSoft}" BorderBrush="{StaticResource Border0}" BorderThickness="1" Padding="13,7">
            <StackPanel Orientation="Horizontal">
              <TextBlock Text="&#xE72C;" FontFamily="Segoe MDL2 Assets" Foreground="{StaticResource Muted}" FontSize="12" Margin="0,0,7,0" VerticalAlignment="Center"/>
              <TextBlock x:Name="ResetText" Text="实探中…" Foreground="{StaticResource Accent}" FontSize="12.5" FontWeight="SemiBold" VerticalAlignment="Center"/>
            </StackPanel>
          </Border>
        </StackPanel>
      </Grid>
      <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto"><StackPanel x:Name="ProjectList"/></ScrollViewer>
      <Border Grid.Row="3" CornerRadius="16" Background="#FF141413" Margin="0,14,0,0" Padding="14,10">
        <DockPanel>
          <DockPanel DockPanel.Dock="Top" Margin="0,0,0,6" LastChildFill="False">
            <TextBlock DockPanel.Dock="Left" Text="运行日志" Foreground="{StaticResource Muted}" FontSize="11" FontWeight="SemiBold" VerticalAlignment="Center"/>
            <Button x:Name="BtnPopLog" DockPanel.Dock="Right" Style="{StaticResource LinkBtn}" Content="⤢ 弹出大窗" VerticalAlignment="Center"/>
            <TextBlock x:Name="StatusText" DockPanel.Dock="Right" Text="待布防" Foreground="{StaticResource Ink2}" FontSize="12" FontWeight="SemiBold" VerticalAlignment="Center" Margin="0,0,16,0" TextTrimming="CharacterEllipsis"/>
          </DockPanel>
          <ScrollViewer x:Name="LogScroll" VerticalScrollBarVisibility="Auto"><TextBlock x:Name="LogText" FontFamily="Cascadia Code, Consolas" FontSize="12" TextWrapping="Wrap"/></ScrollViewer>
        </DockPanel>
      </Border>
      <Grid Grid.Row="4" Margin="0,16,0,0">
        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
        <!-- WrapPanel so the links wrap to a new line instead of being hidden behind the big buttons -->
        <WrapPanel Grid.Column="0" VerticalAlignment="Center" Margin="0,0,14,0">
          <Button x:Name="BtnAll" Style="{StaticResource LinkBtn}" Content="全选" Margin="0,2,16,2"/>
          <Button x:Name="BtnNone" Style="{StaticResource LinkBtn}" Content="取消勾选" Margin="0,2,16,2"/>
          <Button x:Name="BtnAdd" Style="{StaticResource LinkBtn}" Content="+ 文件夹" Margin="0,2,16,2"/>
          <Button x:Name="BtnClearLog" Style="{StaticResource LinkBtn}" Content="清空日志" Margin="0,2,16,2"/>
          <Button x:Name="BtnExportLog" Style="{StaticResource LinkBtn}" Content="导出日志" Margin="0,2,16,2"/>
          <Button x:Name="BtnForgetChat" Style="{StaticResource LinkBtn}" Content="忘记闲聊" Margin="0,2,16,2"/>
          <Button x:Name="BtnClearQuery" Style="{StaticResource LinkBtn}" Content="清空查询" Margin="0,2,16,2" ToolTip="清空所有项目的『只读查询』记忆(下次查询从头开始)"/>
          <Button x:Name="BtnAuthUsers" Style="{StaticResource LinkBtn}" Content="授权用户" Margin="0,2,16,2" ToolTip="查看 / 移除有权限的飞书用户(飞书后台看不到，这才是真正的授权名单)"/>
          <Button x:Name="BtnTour" Style="{StaticResource LinkBtn}" Content="更新导览" Margin="0,2,16,2" ToolTip="为勾选的项目生成/刷新 AI 导览 AI_GUIDE.md(供飞书只读查询更快更准更省);每个项目需 1-3 分钟，进度看运行日志"/>
        </WrapPanel>
        <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
          <Button x:Name="BtnPreview" Style="{StaticResource BtnGhost}" Content="预演" Width="88"/>
          <Button x:Name="BtnDisarm" Style="{StaticResource BtnGhost}" Content="解除" Width="88" Margin="10,0,0,0"/>
          <Button x:Name="BtnArm" Style="{StaticResource BtnPrimary}" Content="布防续跑" Width="132" Margin="10,0,0,0"/>
        </StackPanel>
      </Grid>
      <TextBlock x:Name="FooterPath" Grid.Row="5" Margin="2,10,2,2" FontSize="11" Foreground="{StaticResource Muted}"
                 TextTrimming="CharacterEllipsis" Cursor="Hand" ToolTip="点击打开项目文件夹"/>
    </Grid>
  </Border>
</Window>
'@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$win = [Windows.Markup.XamlReader]::Load($reader)
# taskbar/alt-tab icon: the coral icon generated by install.ps1 (without this, the window
# shows powershell.exe's icon)
try {
  $icoPath = Join-Path $script:AppDir 'icon.ico'
  if(Test-Path $icoPath){ $win.Icon = [Windows.Media.Imaging.BitmapFrame]::Create([Uri]$icoPath) }
} catch {}
$els = @{}
foreach($n in 'TitleBar','BtnClose','BtnMin','Subtitle','ResetText','ResetChip','ChatModelChip','ChatModelText','IntervalChip','IntervalText','ProjectList','LogText','LogScroll','StatusText','BtnPopLog','BtnAll','BtnNone','BtnAdd','BtnClearLog','BtnExportLog','BtnForgetChat','BtnClearQuery','BtnAuthUsers','BtnTour','BtnPreview','BtnDisarm','BtnArm','FooterPath'){ $els[$n] = $win.FindName($n) }
# global UI-thread exception guard: never let a handler bug close the window
$win.Dispatcher.add_UnhandledException({ param($s,$e)
  try { [System.IO.File]::AppendAllText((Join-Path $env:LOCALAPPDATA 'ClaudeResume\logs\gui-error.log'), ((Get-Date).ToString('s') + "  " + $e.Exception.ToString() + "`r`n"), (New-Object System.Text.UTF8Encoding($false))) } catch {}
  $e.Handled = $true
})

$script:cards = @()
# shared with the probe runspace: req=please probe now, probing=in flight, results below
$sync = [hashtable]::Synchronized(@{ req=$true; probing=$false; fhReset=$null; fhUtil=$null; sdReset=$null; sdUtil=$null; limited=$false; ready=$false; probedAt=[datetime]::MinValue; err=$null })
$script:flash = @{ text=''; until=[datetime]::MinValue }
function Set-Flash($t){ $script:flash.text = $t; $script:flash.until = (Get-Date).AddSeconds(6) }
$script:logFile = Join-Path $script:LogDir ("run-" + (Get-Date).ToString('yyyyMMdd') + ".log")
# ALWAYS resolve the newest run-*.log (not the one from the day the GUI opened) — otherwise the log
# goes blank after midnight because the checker writes run-<today>.log while we read run-<open-day>.log.
function Get-CurLogFile { try { return (Get-ChildItem $script:LogDir -Filter 'run-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1) } catch { return $null } }
function Read-LogTail { param([int]$Tail=40) try { $f = Get-CurLogFile; if($f){ return ((Get-Content $f.FullName -Tail $Tail -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`r`n") } } catch {} return '' }

# ---- colored log rendering (per [level]) ----
function New-Brush($hex){ New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($hex)) }
# level -> color for the [level] tag (info is a clear blue so even all-info logs look colored)
$script:logColors = @{
  info   = (New-Brush '#FF58A6FF')   # blue
  ok     = (New-Brush '#FF3FB950')   # green
  launch = (New-Brush '#FFE8763F')   # coral
  warn   = (New-Brush '#FFE3B341')   # amber
  error  = (New-Brush '#FFF07070')   # red
  stream = (New-Brush '#FF8F8D86')   # muted
}
# level -> color for the message BODY (kept readable; warn/error echo the tag color)
$script:logBodyColors = @{
  info   = (New-Brush '#FFD8D7CF')
  ok     = (New-Brush '#FF6FCF84')
  launch = (New-Brush '#FFEDD3C4')
  warn   = (New-Brush '#FFE3B341')
  error  = (New-Brush '#FFF07070')
  stream = (New-Brush '#FF9C9A92')
}
$script:logTsColor = (New-Brush '#FF6E7681')   # timestamp: dim gray
function Set-LogColored($tb, $text){
  # rebuild the TextBlock inlines. Each line is split into timestamp / [level] / body, colored
  # separately so even an all-[info] tail reads as colored (gray time · blue tag · light body).
  $tb.Inlines.Clear()
  if(-not $text -or $text.Trim().Length -eq 0){
    $ph=New-Object Windows.Documents.Run('(暂无日志 · 布防或点「预演」后,这里会实时显示彩色运行日志)')
    $ph.Foreground=$script:logColors['stream']; $tb.Inlines.Add($ph); return
  }
  foreach($line in ($text -split "(`r`n|`n)")){
    if($line -eq "`r`n" -or $line -eq "`n" -or $line.Length -eq 0){ continue }
    $m=[regex]::Match($line, '^(\[[^\]]+\])\s+(\[(\w+)\])\s?([\s\S]*)$')
    if($m.Success){
      $lvl=$m.Groups[3].Value.ToLower()
      $tag=$script:logColors[$lvl]; if(-not $tag){ $tag=$script:logColors['info'] }
      $body=$script:logBodyColors[$lvl]; if(-not $body){ $body=$script:logBodyColors['info'] }
      $r1=New-Object Windows.Documents.Run($m.Groups[1].Value); $r1.Foreground=$script:logTsColor
      $r2=New-Object Windows.Documents.Run(' '+$m.Groups[2].Value); $r2.Foreground=$tag; $r2.FontWeight='Bold'
      $r3=New-Object Windows.Documents.Run(' '+$m.Groups[4].Value); $r3.Foreground=$body
      $tb.Inlines.Add($r1); $tb.Inlines.Add($r2); $tb.Inlines.Add($r3)
    } else {
      $r=New-Object Windows.Documents.Run($line); $r.Foreground=$script:logBodyColors['info']; $tb.Inlines.Add($r)
    }
    $tb.Inlines.Add((New-Object Windows.Documents.LineBreak))
  }
}
$script:lastLogText = $null   # so the timer only re-renders when the tail changed

# ---- pop-out log window (larger, resizable, colored, auto-refreshing) ----
$script:logWin = $null
function Show-LogWindow {
  try {
    if($script:logWin -and $script:logWin.IsVisible){ $script:logWin.Activate(); return }
    $w = New-Object Windows.Window
    $w.Title = 'Claude续跑 · 运行日志'; $w.Width = 1040; $w.Height = 720; $w.WindowStartupLocation='CenterScreen'
    $w.Background = (New-Brush '#FF0D0D0D')
    try { if($script:AppDir){ $ico=Join-Path $script:AppDir 'icon.ico'; if(Test-Path $ico){ $w.Icon=[Windows.Media.Imaging.BitmapFrame]::Create([Uri]$ico) } } } catch {}
    $sv = New-Object Windows.Controls.ScrollViewer; $sv.VerticalScrollBarVisibility='Auto'; $sv.Padding='16'
    $tb = New-Object Windows.Controls.TextBlock; $tb.FontFamily='Cascadia Code, Consolas'; $tb.FontSize=13.5; $tb.TextWrapping='Wrap'; $tb.LineHeight=19
    $sv.Content=$tb; $w.Content=$sv
    $render = { try { Set-LogColored $tb (Read-LogTail 400); $sv.ScrollToEnd() } catch {} }.GetNewClosure()
    & $render
    $t = New-Object Windows.Threading.DispatcherTimer; $t.Interval=[TimeSpan]::FromSeconds(1); $t.Add_Tick($render); $t.Start()
    $w.Add_Closed({ try { $t.Stop() } catch {}; $script:logWin=$null }.GetNewClosure())
    $script:logWin = $w
    $w.Show()
  } catch {}
}

function Show-AuthWindow {
  try {
    if($script:authWin -and $script:authWin.IsVisible){ $script:authWin.Activate(); return }
    $w = New-Object Windows.Window
    $w.Title='Claude续跑 · 授权用户'; $w.Width=660; $w.Height=560; $w.WindowStartupLocation='CenterScreen'
    $w.Background=(New-Brush '#FF141414')
    try { if($script:AppDir){ $ico=Join-Path $script:AppDir 'icon.ico'; if(Test-Path $ico){ $w.Icon=[Windows.Media.Imaging.BitmapFrame]::Create([Uri]$ico) } } } catch {}
    $root=New-Object Windows.Controls.DockPanel
    $hdr=New-Object Windows.Controls.TextBlock
    $hdr.Text='权限模型:名单里的人能『改项目』(通常只有你);其他所有人自动『只读浏览查询』,无需逐个授权。闲聊对所有人开放。名单存在本机 config.json,飞书后台看不到(后台只有『应用可用范围』,管谁能用机器人)。'
    $hdr.TextWrapping='Wrap'; $hdr.Foreground=(New-Brush '#FFB9B9B9'); $hdr.FontSize=12; $hdr.Margin='18,16,18,6'
    [Windows.Controls.DockPanel]::SetDock($hdr,'Top'); $root.Children.Add($hdr)|Out-Null
    $sv=New-Object Windows.Controls.ScrollViewer; $sv.VerticalScrollBarVisibility='Auto'; $sv.Padding='18,4,18,16'
    $script:authList=New-Object Windows.Controls.StackPanel; $sv.Content=$script:authList
    $root.Children.Add($sv)|Out-Null; $w.Content=$root
    # script-scoped so the remove handlers can re-invoke it (avoids the closure-captures-itself trap)
    $script:authRender = {
      $self = $script:authRender   # capture into locals so GetNewClosure'd handlers can re-invoke via $self
      $script:authList.Children.Clear()
      $cfg=Get-CcuConfig
      $secs=@(
        @{ title='✅ 可改项目 —— 只有这些人能改;其他所有人只读浏览'; ids=@(@($cfg.feishuAuthOpenIds)|Where-Object{$_}) }
      )
      foreach($sec in $secs){
        $t=New-Object Windows.Controls.TextBlock; $t.Text=$sec.title; $t.Foreground=(New-Brush '#FFEDEDED'); $t.FontWeight='SemiBold'; $t.FontSize=13; $t.Margin='0,10,0,6'
        $script:authList.Children.Add($t)|Out-Null
        if($sec.ids.Count -eq 0){
          $e=New-Object Windows.Controls.TextBlock; $e.Text='(无)'; $e.Foreground=(New-Brush '#FF8A8A8A'); $e.FontSize=12; $e.Margin='2,0,0,4'
          $script:authList.Children.Add($e)|Out-Null; continue
        }
        foreach($id in $sec.ids){
          $b=New-Object Windows.Controls.Border; $b.CornerRadius='10'; $b.Padding='12,9'; $b.Margin='0,0,0,7'
          $b.Background=(New-Brush '#FF1E1E1E'); $b.BorderBrush=(New-Brush '#FF2E2E2E'); $b.BorderThickness='1'
          $dp=New-Object Windows.Controls.DockPanel
          $rm=New-Object Windows.Controls.TextBlock; $rm.Text='移除'; $rm.Foreground=(New-Brush '#FFE06C6C'); $rm.FontSize=12.5; $rm.Cursor='Hand'; $rm.VerticalAlignment='Center'; $rm.Margin='12,0,2,0'; [Windows.Controls.DockPanel]::SetDock($rm,'Right')
          $thisId=$id
          $rm.Add_MouseLeftButtonUp({ param($s,$e) $e.Handled=$true
            $ans=[System.Windows.MessageBox]::Show(("移除该用户的全部权限?`n" + $thisId), '确认', 'YesNo', 'Question')
            if($ans -ne 'Yes'){ return }
            try {
              $c=Get-CcuConfig
              $newFull=@(@($c.feishuAuthOpenIds)|Where-Object{ $_ -and $_ -ne $thisId })
              $hadFull=@(@($c.feishuAuthOpenIds)|Where-Object{ $_ }).Count
              # removing the LAST 可改 user empties the list, which unlocks the bot for EVERYONE — warn hard
              if($hadFull -gt 0 -and $newFull.Count -eq 0){
                $warn=[System.Windows.MessageBox]::Show('⚠ 这是最后一个『可改项目』用户。移除后名单为空 = 解除锁定,所有飞书用户都能改你的项目 / 改配置 / 授权他人。确定要解除锁定?','危险','YesNo','Warning')
                if($warn -ne 'Yes'){ return }
              }
              $c.feishuAuthOpenIds=$newFull
              $c.feishuViewerOpenIds=@(@($c.feishuViewerOpenIds)|Where-Object{ $_ -and $_ -ne $thisId })
              Set-CcuConfig $c
            } catch {}
            & $self
          }.GetNewClosure())
          $tx=New-Object Windows.Controls.TextBlock; $tx.Text=$id; $tx.Foreground=(New-Brush '#FFEDEDED'); $tx.FontFamily='Cascadia Code, Consolas'; $tx.FontSize=12.5; $tx.VerticalAlignment='Center'; $tx.TextTrimming='CharacterEllipsis'
          $dp.Children.Add($rm)|Out-Null; $dp.Children.Add($tx)|Out-Null
          $b.Child=$dp; $script:authList.Children.Add($b)|Out-Null
        }
      }
      $tip=New-Object Windows.Controls.TextBlock
      $tip.Text='想让某人也能改:让他给机器人发一句话拿到 open_id,再在飞书发「授权 ou_xxx」(或从收到的卡片点「可改项目」)。⚠ 名单为空 = 未锁定(所有人都能改),至少留你自己。'
      $tip.TextWrapping='Wrap'; $tip.Foreground=(New-Brush '#FF8A8A8A'); $tip.FontSize=11.5; $tip.Margin='2,14,0,0'
      $script:authList.Children.Add($tip)|Out-Null
    }
    & $script:authRender
    $w.Add_Closed({ $script:authWin=$null })
    $script:authWin=$w
    $w.Show()
  } catch { Set-Flash ('打开授权窗口出错: ' + $_.Exception.Message) }
}

# wipe ONE project's read-only query session: session id = same sha1(path) formula as the agent's
# querySession(); delete its session jsonl(s) + the started flag so the next query starts fresh.
function Clear-ProjectQuery($projPath){
  $sha1=[System.Security.Cryptography.SHA1]::Create()
  $h=(($sha1.ComputeHash([Text.Encoding]::UTF8.GetBytes($projPath.ToLower())) | ForEach-Object { $_.ToString('x2') }) -join '')
  $id=$h.Substring(0,8)+'-'+$h.Substring(8,4)+'-4'+$h.Substring(13,3)+'-8'+$h.Substring(17,3)+'-'+$h.Substring(20,12)
  $n=0; $proot=Join-Path $env:USERPROFILE '.claude\projects'
  if(Test-Path $proot){
    foreach($d in @(Get-ChildItem $proot -Directory -ErrorAction SilentlyContinue)){
      $jf=Join-Path $d.FullName ($id+'.jsonl')
      if(Test-Path $jf){ Remove-Item $jf -Force -ErrorAction SilentlyContinue; $n++ }
    }
  }
  Remove-Item (Join-Path (Join-Path $script:AppDir 'feishu-query') ($h+'.started')) -Force -ErrorAction SilentlyContinue
  return $n
}
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
  # per-project "清空只读查询会话" (this project only)
  $clr = New-Object Windows.Controls.TextBlock
  $clr.Text='清查询'; $clr.Foreground=$win.FindResource('Muted'); $clr.FontSize=11.5; $clr.Cursor='Hand'; $clr.VerticalAlignment='Center'; $clr.Margin='16,0,0,0'; $clr.ToolTip='清空本项目的『只读查询』会话记忆(下次查询从头开始)'
  [Windows.Controls.DockPanel]::SetDock($clr,'Right')
  $clr.Add_MouseEnter({ $clr.Foreground=$win.FindResource('Accent') }.GetNewClosure())
  $clr.Add_MouseLeave({ $clr.Foreground=$win.FindResource('Muted') }.GetNewClosure())
  $clr.Add_MouseLeftButtonUp({ param($s,$e) $e.Handled=$true
    $n = Clear-ProjectQuery $proj.path
    Set-Flash ("已清空「" + $proj.name + "」的只读查询记忆(" + $n + " 个会话)")
  }.GetNewClosure())
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
  $dp.Children.Add($chk) | Out-Null; $dp.Children.Add($rm) | Out-Null; $dp.Children.Add($right) | Out-Null; $dp.Children.Add($clr) | Out-Null; $dp.Children.Add($sp) | Out-Null
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
# probe interval chip: click cycles 5m -> 15m -> 30m (persisted; checker reads it every tick)
function Update-IntervalChip { $v = 15; try { $v = [int](Get-CcuConfig).probeIntervalMinutes } catch {}; if($v -lt 2){ $v = 15 }; $els.IntervalText.Text = "间隔 ${v}m" }
Update-IntervalChip

# chat-model chip: click cycles 默认/Sonnet/Opus/Haiku (writes feishuChatModel — shared with the Feishu agent)
$script:modelCycle = @('','claude-fable-5','opus','sonnet','haiku')
function Get-ModelLabel($m){ switch("$m".ToLower()){ 'claude-fable-5' { 'Fable 5' } 'sonnet' { 'Sonnet' } 'opus' { 'Opus' } 'haiku' { 'Haiku' } '' { '默认' } default { "$m" } } }
function Update-ChatModelChip { $m=''; try { $m="$((Get-CcuConfig).feishuChatModel)" } catch {}; $els.ChatModelText.Text = '模型 ' + (Get-ModelLabel $m) }
Update-ChatModelChip
$els.ChatModelChip.Add_MouseLeftButtonUp({
  try {
    $c = Get-CcuConfig; $cur = "$($c.feishuChatModel)"
    $i = [Array]::IndexOf($script:modelCycle, $cur.ToLower()); if($i -lt 0){ $i = 0 }
    $next = $script:modelCycle[($i + 1) % $script:modelCycle.Count]
    $c.feishuChatModel = $next; Set-CcuConfig $c
    Update-ChatModelChip
    Set-Flash ('模型 → ' + (Get-ModelLabel $next) + '(飞书同步)')
  } catch { Set-Flash ('设置出错: ' + $_.Exception.Message) }
})
$els.IntervalChip.Add_MouseLeftButtonUp({
  try {
    $c = Get-CcuConfig
    $cur = 15; try { $cur = [int]$c.probeIntervalMinutes } catch {}
    $next = if($cur -lt 15){ 15 } elseif($cur -lt 30){ 30 } else { 5 }
    $c.probeIntervalMinutes = $next; Set-CcuConfig $c
    Update-IntervalChip
    Set-Flash "实探间隔 ${next}m"
  } catch { Set-Flash ('设置出错: ' + $_.Exception.Message) }
})
# clicking the quota chip triggers a fresh live probe (the display IS the refresh button)
$els.ResetChip.Add_MouseLeftButtonUp({ if(-not $sync.probing){ $sync.req=$true; Set-Flash '正在实探…' } })
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
    Set-Flash "已布防 · $($sel.Count) 个项目"
  } catch { Set-Flash ('布防出错: ' + $_.Exception.Message) }
})
$els.BtnDisarm.Add_Click({
  try { $c=Get-CcuConfig; $c.enabled=$false; $c.armed=$false; Set-CcuConfig $c; Set-Flash '已解除布防(全局停用)' }
  catch { Set-Flash ('解除出错: ' + $_.Exception.Message) }
})
$els.BtnClearLog.Add_Click({
  try {
    $lf = Get-CurLogFile; if($lf){ [System.IO.File]::WriteAllText($lf.FullName, '') }
    $els.LogText.Inlines.Clear(); $script:lastLogText = $null
    Set-Flash '日志已清空'
  } catch { Set-Flash ('清空出错: ' + $_.Exception.Message) }
})
$els.BtnPopLog.Add_Click({ Show-LogWindow })
$els.BtnForgetChat.Add_Click({
  try {
    # forget the Feishu 闲聊 memory: drop the "started" flag AND the claude session for that cwd
    $chatDir = Join-Path $script:AppDir 'feishu-chat'
    Remove-Item (Join-Path $chatDir '.started') -Force -ErrorAction SilentlyContinue
    $proot = Join-Path $env:USERPROFILE '.claude\projects'
    if(Test-Path $proot){
      Get-ChildItem $proot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*ClaudeResume-feishu-chat' } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Set-Flash '已清空闲聊记忆(下次闲聊从头开始)'
  } catch { Set-Flash ('清空出错: ' + $_.Exception.Message) }
})
$els.BtnClearQuery.Add_Click({
  try {
    # wipe every project's shared 只读查询 session. Each feishu-query flag is named <sha1(path)>.started;
    # rebuild the session uuid from that filename exactly as the agent's querySession() does, then delete
    # the matching claude session jsonl (must delete it — --session-id on an existing id errors), + the flag.
    $qdir = Join-Path $script:AppDir 'feishu-query'
    $proot = Join-Path $env:USERPROFILE '.claude\projects'
    $sessions = 0; $projects = 0
    if(Test-Path $qdir){
      foreach($f in @(Get-ChildItem $qdir -File -ErrorAction SilentlyContinue)){
        $h = $f.BaseName; $id = $null
        if($h -match '^[0-9a-f]{40}$'){
          $id = $h.Substring(0,8)+'-'+$h.Substring(8,4)+'-4'+$h.Substring(13,3)+'-8'+$h.Substring(17,3)+'-'+$h.Substring(20,12)
        } else { try { $id = (Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).id } catch {} }
        if($id -and (Test-Path $proot)){
          foreach($d in @(Get-ChildItem $proot -Directory -ErrorAction SilentlyContinue)){
            $jf = Join-Path $d.FullName ($id + '.jsonl')
            if(Test-Path $jf){ Remove-Item $jf -Force -ErrorAction SilentlyContinue; $sessions++ }
          }
        }
        Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue; $projects++
      }
    }
    Set-Flash ("已清空只读查询记忆:" + $projects + " 个项目 / " + $sessions + " 个会话")
  } catch { Set-Flash ('清空查询出错: ' + $_.Exception.Message) }
})
$els.BtnAuthUsers.Add_Click({ Show-AuthWindow })
$els.BtnTour.Add_Click({
  try {
    # generate/refresh AI_GUIDE.md for the checked projects, in a background runspace (each ~1-3 min,
    # runs claude headless via Invoke-ProjectTour). Progress goes to the run log; UI stays responsive.
    if($script:tourHandle -and -not $script:tourHandle.IsCompleted){ Set-Flash '导览更新进行中，请稍候…'; return }
    if($script:tourPS){ try { $script:tourPS.EndInvoke($script:tourHandle) } catch {}; try { $script:tourPS.Dispose() } catch {}; try { $script:tourRs.Close() } catch {}; $script:tourPS=$null }
    $sel = @(Get-Selected)
    if($sel.Count -eq 0){ Set-Flash '先勾选要更新导览的项目'; return }
    $model = 'sonnet'; try { $m=(Get-CcuConfig).resumeModel; if($m){ $model=$m } } catch {}
    $rs = [RunspaceFactory]::CreateRunspace(); $rs.ApartmentState='STA'; $rs.Open()
    $rs.SessionStateProxy.SetVariable('libPath', (Join-Path $PSScriptRoot 'lib.ps1'))
    $rs.SessionStateProxy.SetVariable('projects', $sel)
    $rs.SessionStateProxy.SetVariable('tourModel', $model)
    $ps = [PowerShell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript({
      . $libPath
      Write-CcuLog ('开始更新 ' + $projects.Count + ' 个项目的 AI 导览(模型 ' + $tourModel + ')') 'info'
      foreach($pr in $projects){
        Write-CcuLog ('更新导览 -> ' + $pr.name) 'launch'
        $r = Invoke-ProjectTour -Project $pr -Model $tourModel
        Write-CcuLog ($pr.name + ' 导览 -> ' + $r.status) $(if($r.status -eq 'success'){'ok'}else{'warn'})
      }
      Write-CcuLog '导览更新全部结束' 'ok'
    })
    $script:tourRs = $rs; $script:tourPS = $ps; $script:tourHandle = $ps.BeginInvoke()
    Set-Flash ('已开始更新 ' + $sel.Count + ' 个项目的导览(后台，看运行日志)')
  } catch { Set-Flash ('更新导览出错: ' + $_.Exception.Message) }
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

# ---- background probe runspace: runs a live probe on demand ($sync.req), never on a loop ----
# One probe when the window opens, and one each time 实探/reset-chip is clicked. Off the UI thread
# so the window never freezes during the (few-second) probe.
$rs = [runspacefactory]::CreateRunspace(); $rs.ApartmentState='MTA'; $rs.ThreadOptions='ReuseThread'; $rs.Open()
$rs.SessionStateProxy.SetVariable('sync',$sync)
$rs.SessionStateProxy.SetVariable('libPath',(Join-Path $PSScriptRoot 'lib.ps1'))
$ps = [powershell]::Create(); $ps.Runspace=$rs
[void]$ps.AddScript({
  . $libPath
  while($true){
    if($sync.req -and -not $sync.probing){
      $sync.req=$false; $sync.probing=$true; $sync.err=$null
      try {
        $cfg=Get-CcuConfig; $pr=Test-ClaudeReady -Model $cfg.probeModel
        $sync.limited=($pr.reason -eq 'limited'); $sync.ready=[bool]$pr.ready
        $sync.fhUtil=$pr.fiveHourUtil; $sync.sdUtil=$pr.sevenDayUtil
        $sync.fhReset = if($pr.fiveHourResetUtc){ $pr.fiveHourResetUtc } else { $null }
        $sync.sdReset = if($pr.sevenDayResetUtc){ $pr.sevenDayResetUtc } else { $null }
        $sync.probedAt=Get-Date
        try { $st=Get-CcuState; $st=Save-RealResetFromProbe -Probe $pr -State $st; Set-CcuState $st } catch {}
      } catch { $sync.err=$_.Exception.Message }
      $sync.probing=$false
    }
    Start-Sleep -Milliseconds 400
  }
})
$hb = $ps.BeginInvoke()

# ---- UI timer: repaint every second (fast reads only; probe runs in the runspace) ----
$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(1)
$timer.Add_Tick({
  $nowU=[DateTimeOffset]::UtcNow
  if($sync.probing){
    $els.ResetText.Text='实探中…'
  } else {
    # show BOTH windows (5h first): a percentage when the server reports one, a precise
    # countdown once limited, and '低' for the 5h window while it's well below its limit
    # (the server only reports a window's number as it approaches, so no number yet = low).
    $probed = ($sync.probedAt -ne [datetime]::MinValue)
    $fh=''
    if($sync.limited -and $sync.fhReset -and $sync.fhReset -gt $nowU){ $fh='5h 限流 '+(Format-Countdown ($sync.fhReset-$nowU).TotalSeconds) }
    elseif($null -ne $sync.fhUtil){ $fh='5h '+[int][Math]::Round([double]$sync.fhUtil*100)+'%' }
    elseif($probed -and $sync.ready){ $fh='5h 低' }
    $sd=''
    if($sync.limited -and $sync.sdReset -and $sync.sdReset -gt $nowU){ $sd='7d 限流 '+(Format-Countdown ($sync.sdReset-$nowU).TotalSeconds) }
    elseif($null -ne $sync.sdUtil){ $sd='7d '+[int][Math]::Round([double]$sync.sdUtil*100)+'%' }
    $t = (@($fh,$sd) | Where-Object { $_ }) -join ' · '
    if(-not $t){
      try { $st=Get-CcuState
        if($st.realFiveHourResetUtc -and $st.realResetProbedUtc){
          $a=[DateTimeOffset]::FromUnixTimeSeconds([long]$st.realFiveHourResetUtc); $b=[DateTimeOffset]::FromUnixTimeSeconds([long]$st.realResetProbedUtc)
          if($a -gt $nowU -and ($nowU-$b).TotalHours -lt 5){ $t='5h 距重置 '+(Format-Countdown ($a-$nowU).TotalSeconds) }
        }
      } catch {}
    }
    if(-not $t){ $t = if($probed){ '空闲' } else { '点击实探' } }
    $els.ResetText.Text=$t
  }
  $lt = Read-LogTail
  if($lt -ne $script:lastLogText){ $script:lastLogText=$lt; Set-LogColored $els.LogText $lt; $els.LogScroll.ScrollToEnd() }
  if((Get-Date) -lt $script:flash.until){ Set-StatusLine $script:flash.text }
  else {
    $en=$false; try { $en=[bool](Get-CcuConfig).enabled } catch {}
    $ph='idle'; try { $ph="$((Get-CcuState).phase)" } catch {}
    Set-StatusLine (($(if($en){'● 已布防'}else{'○ 未布防'})) + ' · 引擎: ' + $ph)
  }
  # keep the interval/model chips in sync if changed externally (e.g. from Feishu)
  try { Update-IntervalChip; Update-ChatModelChip } catch {}
})
$timer.Start()
$win.Add_Closed({ try { $timer.Stop() } catch {}; try { if($script:logWin){ $script:logWin.Close() } } catch {}; try { $ps.Stop(); $rs.Close() } catch {}; try { if($script:instanceOwned){ $script:instanceMutex.ReleaseMutex() } } catch {} })
if($SelfTest){ $tt=New-Object Windows.Threading.DispatcherTimer; $tt.Interval=[TimeSpan]::FromMilliseconds(2500); $tt.Add_Tick({ $tt.Stop(); $win.Close() }); $tt.Start() }
[void]$win.ShowDialog()
