<#
  picker.ps1 - "AI Resume" GUI (config + monitor). WPF/XAML, Windows PowerShell 5.1 STA.
  The GUI never does long work: it selects projects, writes config, and monitors the
  Scheduled-Task checker via state.json + the log. -RenderTo <png> snapshots headless.
  Save UTF-8 WITH BOM.
#>
param([string]$RenderTo = '', [string]$AISettingsRenderTo = '', [switch]$SelfTest, [switch]$SessionSelfTest)
Set-StrictMode -Off
$ErrorActionPreference = 'Stop'
$script:selfTestState = @{ failed=$false; opened=$false; validated=$false }
$script:isUiTest = [bool]($RenderTo -or $AISettingsRenderTo -or $SelfTest -or $SessionSelfTest)
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
if(-not $script:instanceOwned -and -not $RenderTo -and -not $AISettingsRenderTo -and -not $SelfTest -and -not $SessionSelfTest){
  try {
    $other = Get-Process -Name powershell,pwsh -ErrorAction SilentlyContinue |
             Where-Object { $_.Id -ne $PID -and $_.MainWindowTitle -eq 'AI Resume' -and $_.MainWindowHandle -ne 0 } |
             Select-Object -First 1
    if($other){ [void][Win32.Native]::ShowWindow($other.MainWindowHandle, 9); [void][Win32.Native]::SetForegroundWindow($other.MainWindowHandle) }  # 9 = SW_RESTORE
  } catch {}
  return
}

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="AI Resume" WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize" Width="1120" Height="760" WindowStartupLocation="CenterScreen"
        FontFamily="Microsoft YaHei UI, Segoe UI" UseLayoutRounding="True" SnapsToDevicePixels="True">
  <Window.Resources>
    <SolidColorBrush x:Key="Canvas" Color="#FF101214"/>
    <SolidColorBrush x:Key="Panel" Color="#FF171A1D"/>
    <SolidColorBrush x:Key="PanelHover" Color="#FF1D2226"/>
    <SolidColorBrush x:Key="Ink" Color="#FFF4F6F7"/>
    <SolidColorBrush x:Key="Ink2" Color="#FFC8CED2"/>
    <SolidColorBrush x:Key="Muted" Color="#FF929BA2"/>
    <SolidColorBrush x:Key="Border0" Color="#FF2A3035"/>
    <SolidColorBrush x:Key="Accent" Color="#FFFF6B2C"/>
    <SolidColorBrush x:Key="AccentSoft" Color="#2BFF6B2C"/>
    <SolidColorBrush x:Key="Green" Color="#FF2DCB83"/>
    <SolidColorBrush x:Key="Blue" Color="#FF4B8DFF"/>
    <SolidColorBrush x:Key="Danger" Color="#FFFF6B72"/>
    <Style x:Key="Chk" TargetType="CheckBox">
      <Setter Property="Cursor" Value="Hand"/><Setter Property="Focusable" Value="True"/>
      <Setter Property="Template"><Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <Border x:Name="bx" Width="22" Height="22" CornerRadius="5" Background="#FF0F1113" BorderBrush="{StaticResource Border0}" BorderThickness="1.5">
            <Viewbox Width="12" Height="12" HorizontalAlignment="Center" VerticalAlignment="Center">
              <Path x:Name="ck" Data="M0,5 L4,9 L11,1" Stroke="White" StrokeThickness="1.8" Visibility="Collapsed" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
            </Viewbox>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="bx" Property="Background" Value="{StaticResource Accent}"/><Setter TargetName="bx" Property="BorderBrush" Value="{StaticResource Accent}"/><Setter TargetName="ck" Property="Visibility" Value="Visible"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="bx" Property="BorderBrush" Value="White"/><Setter TargetName="bx" Property="BorderThickness" Value="2"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key="BtnPrimary" TargetType="Button">
      <Setter Property="Foreground" Value="White"/><Setter Property="FontWeight" Value="SemiBold"/><Setter Property="FontSize" Value="13.5"/><Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
        <Border x:Name="b" CornerRadius="7" Background="{StaticResource Accent}" BorderBrush="{StaticResource Accent}" BorderThickness="1" Padding="18,0" Height="42"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="#FFFF7E48"/></Trigger>
          <Trigger Property="IsPressed" Value="True"><Setter TargetName="b" Property="Background" Value="#FFE95820"/></Trigger>
          <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="White"/><Setter TargetName="b" Property="BorderThickness" Value="2"/></Trigger>
          <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.4"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="BtnGhost" TargetType="Button">
      <Setter Property="Foreground" Value="{StaticResource Ink2}"/><Setter Property="FontSize" Value="13"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Padding" Value="14,0"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
        <Border x:Name="b" CornerRadius="7" Background="Transparent" BorderBrush="{StaticResource Border0}" BorderThickness="1" Padding="{TemplateBinding Padding}" Height="40"><ContentPresenter x:Name="contentHost" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{StaticResource PanelHover}"/><Setter TargetName="b" Property="BorderBrush" Value="#FF4B535A"/></Trigger>
          <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="White"/><Setter TargetName="b" Property="BorderThickness" Value="2"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="LinkBtn" TargetType="Button">
      <Setter Property="Foreground" Value="{StaticResource Muted}"/><Setter Property="FontSize" Value="12"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Background" Value="Transparent"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="b" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="4" Padding="5,3"><ContentPresenter VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{StaticResource Ink}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{StaticResource Accent}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="ChipBtn" TargetType="Button" BasedOn="{StaticResource BtnGhost}">
      <Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Padding" Value="12,0"/>
    </Style>
    <Style TargetType="ScrollBar">
      <Setter Property="Width" Value="9"/><Setter Property="Background" Value="{StaticResource Canvas}"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
        <Grid Background="{TemplateBinding Background}"><Track x:Name="PART_Track" IsDirectionReversed="True"><Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0"/></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="#FF424950" CornerRadius="4" Margin="2,0"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0"/></Track.IncreaseRepeatButton></Track></Grid>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
  </Window.Resources>

  <Border CornerRadius="12" Background="{StaticResource Canvas}" BorderBrush="{StaticResource Border0}" BorderThickness="1">
    <Border.Effect><DropShadowEffect Color="#000000" Direction="270" ShadowDepth="12" BlurRadius="38" Opacity="0.55"/></Border.Effect>
    <Grid Margin="22,18,22,16">
      <Grid.RowDefinitions><RowDefinition Height="46"/><RowDefinition Height="74"/><RowDefinition Height="*"/><RowDefinition Height="112"/><RowDefinition Height="28"/></Grid.RowDefinitions>

      <Grid x:Name="TitleBar" Grid.Row="0" Background="Transparent">
        <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
          <Border Width="34" Height="34" CornerRadius="7" Background="{StaticResource Accent}"><TextBlock Text="AR" Foreground="White" FontWeight="Bold" FontSize="12" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
          <StackPanel Margin="10,0,0,0" VerticalAlignment="Center"><TextBlock Text="AI Resume" Foreground="{StaticResource Ink}" FontWeight="SemiBold" FontSize="14"/><TextBlock Text="本地多 AI 工作台" Foreground="{StaticResource Muted}" FontSize="10.5"/></StackPanel>
        </StackPanel>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,76,0">
          <Button x:Name="BtnSessions" Style="{StaticResource LinkBtn}" Content="会话" Margin="0,0,16,0" ToolTip="查看、归档、恢复或删除 AI 会话"/>
          <Button x:Name="BtnAuthUsers" Style="{StaticResource LinkBtn}" Content="飞书权限" ToolTip="管理可修改项目的飞书用户"/>
        </StackPanel>
        <Button x:Name="BtnClose" Content="&#xE8BB;" FontFamily="Segoe MDL2 Assets" FontSize="11" Foreground="{StaticResource Muted}" HorizontalAlignment="Right" Width="30" Height="30" Cursor="Hand" Background="Transparent" BorderThickness="0" ToolTip="关闭"/>
        <Button x:Name="BtnMin" Content="&#xE921;" FontFamily="Segoe MDL2 Assets" FontSize="11" Foreground="{StaticResource Muted}" HorizontalAlignment="Right" Margin="0,0,36,0" Width="30" Height="30" Cursor="Hand" Background="Transparent" BorderThickness="0" ToolTip="最小化"/>
      </Grid>

      <Grid Grid.Row="1" Margin="0,6,0,14">
        <StackPanel VerticalAlignment="Center"><TextBlock Text="自动续跑工作台" Foreground="{StaticResource Ink}" FontWeight="SemiBold" FontSize="24"/><TextBlock x:Name="Subtitle" Text="选择项目，额度恢复后按顺序继续" Foreground="{StaticResource Muted}" FontSize="12.5" Margin="0,4,0,0"/></StackPanel>
        <Border HorizontalAlignment="Right" VerticalAlignment="Center" MaxWidth="420" Background="{StaticResource Panel}" BorderBrush="{StaticResource Border0}" BorderThickness="1" CornerRadius="7" Padding="12,8">
          <StackPanel Orientation="Horizontal"><Ellipse Width="7" Height="7" Fill="{StaticResource Green}" Margin="0,0,8,0"/><TextBlock x:Name="StatusText" Text="未布防" Foreground="{StaticResource Ink2}" FontSize="12.5" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/></StackPanel>
        </Border>
      </Grid>

      <Grid Grid.Row="2">
        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="318"/></Grid.ColumnDefinitions>
        <Grid Grid.Column="0" Margin="0,0,18,0">
          <Grid.RowDefinitions><RowDefinition Height="42"/><RowDefinition Height="*"/></Grid.RowDefinitions>
          <Grid Grid.Row="0"><TextBlock Text="项目" Foreground="{StaticResource Ink2}" FontWeight="SemiBold" FontSize="13" VerticalAlignment="Center"/><StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center"><Button x:Name="BtnAll" Style="{StaticResource LinkBtn}" Content="全选"/><Button x:Name="BtnNone" Style="{StaticResource LinkBtn}" Content="取消" Margin="10,0,0,0"/><Button x:Name="BtnAdd" Style="{StaticResource BtnGhost}" Content="添加文件夹" Width="96" Margin="12,0,0,0"/><Button x:Name="BtnTour" Style="{StaticResource BtnGhost}" Content="更新导览" Width="88" Margin="8,0,0,0" ToolTip="刷新所选项目的 AI_GUIDE.md"/></StackPanel></Grid>
          <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" Padding="0,0,5,0"><StackPanel x:Name="ProjectList"/></ScrollViewer>
        </Grid>

        <Border Grid.Column="1" Background="{StaticResource Panel}" BorderBrush="{StaticResource Border0}" BorderThickness="1" CornerRadius="8" Padding="16">
          <StackPanel>
            <TextBlock Text="当前 AI" Foreground="{StaticResource Muted}" FontSize="11" FontWeight="SemiBold"/>
            <Button x:Name="ChatModelChip" Style="{StaticResource ChipBtn}" Margin="0,8,0,0" ToolTip="选择默认 AI，并配置 OpenAI / DeepSeek / Claude" AutomationProperties.Name="打开 AI 服务与模型设置">
              <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><Border x:Name="ModelAccent" Width="8" Height="28" CornerRadius="4" Background="{StaticResource Blue}"/><TextBlock x:Name="ChatModelText" Grid.Column="1" Text="GPT-5.6 Sol" Foreground="{StaticResource Ink}" FontSize="14" FontWeight="SemiBold" VerticalAlignment="Center" Margin="12,0,8,0" TextTrimming="CharacterEllipsis"/><TextBlock Grid.Column="2" Text="&#xE70D;" FontFamily="Segoe MDL2 Assets" Foreground="{StaticResource Muted}" VerticalAlignment="Center"/></Grid>
            </Button>
            <Grid Margin="0,12,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><TextBlock Text="OpenAI" Foreground="{StaticResource Ink2}" FontSize="12"/><TextBlock x:Name="OpenAIStateText" Grid.Column="1" Text="未配置" Foreground="{StaticResource Muted}" FontSize="11.5"/></Grid>
            <Grid Margin="0,7,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><TextBlock Text="DeepSeek" Foreground="{StaticResource Ink2}" FontSize="12"/><TextBlock x:Name="DeepSeekStateText" Grid.Column="1" Text="未配置" Foreground="{StaticResource Muted}" FontSize="11.5"/></Grid>
            <Grid Margin="0,7,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><TextBlock Text="Claude" Foreground="{StaticResource Ink2}" FontSize="12"/><TextBlock x:Name="ClaudeStateText" Grid.Column="1" Text="本机登录" Foreground="{StaticResource Muted}" FontSize="11.5"/></Grid>

            <Border Height="1" Background="{StaticResource Border0}" Margin="0,15,0,14"/>
            <TextBlock Text="额度与检查" Foreground="{StaticResource Muted}" FontSize="11" FontWeight="SemiBold"/>
            <Button x:Name="ResetChip" Style="{StaticResource ChipBtn}" Margin="0,8,0,0" ToolTip="重新实探三家 AI；此处同时显示 Claude 5h / 7d 额度" AutomationProperties.Name="立即实探 AI 可用性与额度"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><TextBlock Text="&#xE72C;" FontFamily="Segoe MDL2 Assets" Foreground="{StaticResource Accent}" VerticalAlignment="Center"/><TextBlock x:Name="ResetText" Grid.Column="1" Text="等待实探" Foreground="{StaticResource Ink2}" FontSize="12.5" FontWeight="SemiBold" Margin="12,0,10,0" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/><TextBlock Grid.Column="2" Text="立即刷新" Foreground="{StaticResource Muted}" FontSize="11" VerticalAlignment="Center"/></Grid></Button>
            <Button x:Name="IntervalChip" Style="{StaticResource ChipBtn}" Margin="0,8,0,0" ToolTip="切换可用状态下的额度检查间隔" AutomationProperties.Name="切换检查间隔"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><TextBlock Text="检查间隔" Foreground="{StaticResource Ink2}" FontSize="12.5" VerticalAlignment="Center"/><TextBlock x:Name="IntervalText" Grid.Column="1" Text="15 分钟" Margin="12,0,0,0" Foreground="{StaticResource Muted}" FontSize="12.5" VerticalAlignment="Center"/></Grid></Button>

            <Border Height="1" Background="{StaticResource Border0}" Margin="0,15,0,14"/>
            <TextBlock Text="自动续跑" Foreground="{StaticResource Muted}" FontSize="11" FontWeight="SemiBold"/>
            <Button x:Name="BtnArm" Style="{StaticResource BtnPrimary}" Content="布防所选项目" Margin="0,9,0,0"/>
            <Grid Margin="0,8,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="8"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Button x:Name="BtnPreview" Style="{StaticResource BtnGhost}" Content="预演"/><Button x:Name="BtnDisarm" Grid.Column="2" Style="{StaticResource BtnGhost}" Content="解除布防"/></Grid>
          </StackPanel>
        </Border>
      </Grid>

      <Border Grid.Row="3" Background="#FF0C0E10" BorderBrush="{StaticResource Border0}" BorderThickness="1" CornerRadius="8" Margin="0,14,0,0" Padding="14,10">
        <DockPanel><Grid DockPanel.Dock="Top" Margin="0,0,0,7"><TextBlock Text="运行日志" Foreground="{StaticResource Muted}" FontSize="11" FontWeight="SemiBold"/><StackPanel Orientation="Horizontal" HorizontalAlignment="Right"><Button x:Name="BtnClearLog" Style="{StaticResource LinkBtn}" Content="清空"/><Button x:Name="BtnExportLog" Style="{StaticResource LinkBtn}" Content="导出" Margin="10,0,0,0"/><Button x:Name="BtnPopLog" Style="{StaticResource LinkBtn}" Content="大窗" Margin="10,0,0,0"/></StackPanel></Grid><ScrollViewer x:Name="LogScroll" VerticalScrollBarVisibility="Auto"><TextBlock x:Name="LogText" FontFamily="Cascadia Mono, Cascadia Code, Consolas" FontSize="12" TextWrapping="Wrap"/></ScrollViewer></DockPanel>
      </Border>

      <Grid Grid.Row="4" Margin="0,6,2,0"><Button x:Name="FooterPath" Style="{StaticResource LinkBtn}" HorizontalAlignment="Left" FontSize="10.5" ToolTip="打开项目源目录"/><TextBlock HorizontalAlignment="Right" VerticalAlignment="Center" Text="闲聊/查询 14 天归档 · 30 天删除 · 工作会话仅手动归档" FontSize="10.5" Foreground="{StaticResource Muted}"/></Grid>

      <StackPanel Visibility="Collapsed"><Button x:Name="BtnForgetChat"/><Button x:Name="BtnClearQuery"/></StackPanel>
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
foreach($n in 'TitleBar','BtnClose','BtnMin','Subtitle','ResetText','ResetChip','ChatModelChip','ChatModelText','ModelAccent','OpenAIStateText','DeepSeekStateText','ClaudeStateText','IntervalChip','IntervalText','ProjectList','LogText','LogScroll','StatusText','BtnPopLog','BtnAll','BtnNone','BtnAdd','BtnClearLog','BtnExportLog','BtnForgetChat','BtnClearQuery','BtnAuthUsers','BtnSessions','BtnTour','BtnPreview','BtnDisarm','BtnArm','FooterPath'){ $els[$n] = $win.FindName($n) }
# global UI-thread exception guard: never let a handler bug close the window
$win.Dispatcher.add_UnhandledException({ param($s,$e)
  try { [System.IO.File]::AppendAllText((Join-Path $env:LOCALAPPDATA 'ClaudeResume\logs\gui-error.log'), ((Get-Date).ToString('s') + "  " + $e.Exception.ToString() + "`r`n"), (New-Object System.Text.UTF8Encoding($false))) } catch {}
  $e.Handled = $true
})

$script:cards = @()
# Shared with the probe runspace. Normal startup performs real provider probes; screenshot and
# self-test modes remain offline and render a truthful "待检测" state instead of inventing success.
$probeOnOpen = -not $script:isUiTest
$initialProviderStatus = if($probeOnOpen){'checking'}else{'pending'}
$sync = [hashtable]::Synchronized(@{
  req=$probeOnOpen; probing=$false; providerReq=$probeOnOpen; providerProbing=$false
  fhReset=$null; fhUtil=$null; sdReset=$null; sdUtil=$null; limited=$false; ready=$false
  probedAt=[datetime]::MinValue; providerProbedAt=[datetime]::MinValue; err=$null
  claudeStatus=$initialProviderStatus; claudeReason=''; openaiStatus=$initialProviderStatus; openaiReason=''; openaiRoute=''
  deepseekStatus=$initialProviderStatus; deepseekReason=''; deepseekRoute=''
})
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
    $w.Title = 'AI Resume · 运行日志'; $w.Width = 1040; $w.Height = 720; $w.WindowStartupLocation='CenterScreen'
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
    $w.Title='AI Resume · 飞书权限'; $w.Width=660; $w.Height=560; $w.WindowStartupLocation='CenterScreen'
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
          $b=New-Object Windows.Controls.Border; $b.CornerRadius='8'; $b.Padding='12,9'; $b.Margin='0,0,0,7'
          $b.Background=(New-Brush '#FF1E1E1E'); $b.BorderBrush=(New-Brush '#FF2E2E2E'); $b.BorderThickness='1'
          $dp=New-Object Windows.Controls.DockPanel
          $rm=New-Object Windows.Controls.Button; $rm.Content='移除'; $rm.Foreground=(New-Brush '#FFE06C6C'); $rm.Background=[Windows.Media.Brushes]::Transparent; $rm.BorderThickness='0'; $rm.FontSize=12.5; $rm.Cursor='Hand'; $rm.VerticalAlignment='Center'; $rm.Margin='12,0,2,0'; $rm.Padding='6,3'; [Windows.Controls.DockPanel]::SetDock($rm,'Right')
          $thisId=$id
          $rm.Add_Click({
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
              [void](Update-CcuConfig { param($live)
                $live.feishuAuthOpenIds=@(@($live.feishuAuthOpenIds)|Where-Object{ $_ -and $_ -ne $thisId })
                $live.feishuViewerOpenIds=@(@($live.feishuViewerOpenIds)|Where-Object{ $_ -and $_ -ne $thisId })
              })
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

function Invoke-SessionManager {
  param([string[]]$Arguments)
  $node = Get-Command node -ErrorAction SilentlyContinue
  $manager = Join-Path $PSScriptRoot 'session-manager.js'
  if(-not $node -or -not (Test-Path $manager)){ throw '缺少 node 或 session-manager.js，请重新安装运行副本。' }
  $quoted = @($manager) + @($Arguments) | ForEach-Object { '"' + ("$_".Replace('"','\"')) + '"' }
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName=$node.Source; $psi.Arguments=($quoted -join ' '); $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
  $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true; $psi.StandardOutputEncoding=[Text.Encoding]::UTF8
  $p=[Diagnostics.Process]::Start($psi); $stdout=$p.StandardOutput.ReadToEnd(); $stderr=$p.StandardError.ReadToEnd(); $p.WaitForExit()
  $result=$null; try { $result=$stdout | ConvertFrom-Json } catch { throw ('会话管理返回格式错误: ' + $stderr) }
  if(-not $result.ok){ throw $(if($result.error){"$($result.error)"}elseif($stderr){$stderr}else{'会话管理失败'}) }
  return $result
}
function Format-SessionBytes([double]$n){ if($n -ge 1GB){return ('{0:N1} GB' -f ($n/1GB))}; if($n -ge 1MB){return ('{0:N1} MB' -f ($n/1MB))}; if($n -ge 1KB){return ('{0:N0} KB' -f ($n/1KB))}; return ('{0:N0} B' -f $n) }
function Show-SessionManagerWindow {
  try {
    $w=New-Object Windows.Window; $w.Title='AI Resume · 会话'; $w.Width=1060; $w.Height=680; $w.WindowStartupLocation='CenterOwner'; $w.Owner=$win; $w.Background=New-Brush '#FF101214'; $w.FontFamily='Segoe UI, Microsoft YaHei'
    $root=New-Object Windows.Controls.Grid; $root.Margin='24,20'
    @('Auto','Auto','*','Auto') | ForEach-Object { $rd=New-Object Windows.Controls.RowDefinition; $rd.Height="$_"; $root.RowDefinitions.Add($rd) }
    $head=New-Object Windows.Controls.Grid; [Windows.Controls.Grid]::SetRow($head,0)
    $title=New-Object Windows.Controls.TextBlock; $title.Text='会话'; $title.Foreground=New-Brush '#FFF4F6F7'; $title.FontSize=22; $title.FontWeight='SemiBold'
    $summary=New-Object Windows.Controls.TextBlock; $summary.HorizontalAlignment='Right'; $summary.VerticalAlignment='Center'; $summary.Foreground=New-Brush '#FF929BA2'; $summary.FontSize=12
    $head.Children.Add($title)|Out-Null; $head.Children.Add($summary)|Out-Null; $root.Children.Add($head)|Out-Null

    $tools=New-Object Windows.Controls.Grid; $tools.Margin='0,15,0,12'; [Windows.Controls.Grid]::SetRow($tools,1)
    $c1=New-Object Windows.Controls.ColumnDefinition; $c1.Width='*'; $c2=New-Object Windows.Controls.ColumnDefinition; $c2.Width='190'; $tools.ColumnDefinitions.Add($c1); $tools.ColumnDefinitions.Add($c2)
    $search=New-Object Windows.Controls.TextBox; $search.Height=36; $search.Padding='10,6'; $search.Background=New-Brush '#FF171A1D'; $search.Foreground=New-Brush '#FFF4F6F7'; $search.BorderBrush=New-Brush '#FF343A40'; $search.CaretBrush=New-Brush '#FFFFFFFF'; $search.ToolTip='按项目、标题、用户或 AI 搜索'
    $filter=New-Object Windows.Controls.ComboBox; [Windows.Controls.Grid]::SetColumn($filter,1); $filter.Margin='10,0,0,0'; $filter.Height=36; $filter.Background=New-Brush '#FF171A1D'; $filter.Foreground=New-Brush '#FFF4F6F7'
    [Windows.Automation.AutomationProperties]::SetName($search,'搜索会话'); [Windows.Automation.AutomationProperties]::SetName($filter,'会话类型筛选')
    foreach($f in @(@('all','全部会话'),@('chat','飞书闲聊'),@('query','飞书查询'),@('work','项目工作'),@('archived','已归档'))){ $it=New-Object Windows.Controls.ComboBoxItem; $it.Tag=$f[0]; $it.Content=$f[1]; $it.Foreground=New-Brush '#FFF4F6F7'; $it.Background=New-Brush '#FF171A1D'; $filter.Items.Add($it)|Out-Null }; $filter.SelectedIndex=0
    $tools.Children.Add($search)|Out-Null; $tools.Children.Add($filter)|Out-Null; $root.Children.Add($tools)|Out-Null

    $grid=New-Object Windows.Controls.DataGrid; [Windows.Controls.Grid]::SetRow($grid,2); $grid.AutoGenerateColumns=$false; $grid.IsReadOnly=$true; $grid.SelectionMode='Single'; $grid.HeadersVisibility='Column'; $grid.GridLinesVisibility='Horizontal'; $grid.RowHeight=42; $grid.ColumnHeaderHeight=34; $grid.EnableRowVirtualization=$true; $grid.Background=New-Brush '#FF101214'; $grid.Foreground=New-Brush '#FFE5E8EA'; $grid.BorderBrush=New-Brush '#FF2A3035'; $grid.HorizontalGridLinesBrush=New-Brush '#FF24292E'; $grid.RowBackground=New-Brush '#FF141719'; $grid.AlternatingRowBackground=New-Brush '#FF171A1D'
    [Windows.Automation.AutomationProperties]::SetName($grid,'AI 会话列表')
    foreach($col in @(@('状态','StateLabel',74),@('类型','KindLabel',88),@('AI','AI',150),@('项目 / 用户','Owner',150),@('标题','Title',220),@('最近使用','LastUsed',132),@('大小','Size',86))){ $dc=New-Object Windows.Controls.DataGridTextColumn; $dc.Header=$col[0]; $dc.Binding=New-Object Windows.Data.Binding($col[1]); $dc.Width=[Windows.Controls.DataGridLength]::new([double]$col[2]); $grid.Columns.Add($dc) }
    $root.Children.Add($grid)|Out-Null

    $bar=New-Object Windows.Controls.Grid; $bar.Margin='0,14,0,0'; [Windows.Controls.Grid]::SetRow($bar,3); $lc=New-Object Windows.Controls.ColumnDefinition; $lc.Width='*'; $rc=New-Object Windows.Controls.ColumnDefinition; $rc.Width='Auto'; $bar.ColumnDefinitions.Add($lc); $bar.ColumnDefinitions.Add($rc)
    $status=New-Object Windows.Controls.TextBlock; $status.Foreground=New-Brush '#FF929BA2'; $status.VerticalAlignment='Center'; $status.FontSize=12
    $buttons=New-Object Windows.Controls.StackPanel; $buttons.Orientation='Horizontal'; [Windows.Controls.Grid]::SetColumn($buttons,1)
    function New-SessionButton($text,$margin='8,0,0,0'){ $b=New-Object Windows.Controls.Button; $b.Content=$text; $b.MinWidth=82; $b.Height=34; $b.Margin=$margin; $b.Padding='12,0'; $b.Cursor='Hand'; return $b }
    $refresh=New-SessionButton '刷新' '0'; $cleanup=New-SessionButton '安全清理'; $archive=New-SessionButton '归档'; $restore=New-SessionButton '恢复'; $delete=New-SessionButton '永久删除'
    $delete.Foreground=New-Brush '#FFFF6B72'
    foreach($b in @($refresh,$cleanup,$archive,$restore,$delete)){ $buttons.Children.Add($b)|Out-Null }
    $bar.Children.Add($status)|Out-Null; $bar.Children.Add($buttons)|Out-Null; $root.Children.Add($bar)|Out-Null; $w.Content=$root

    $script:sessionRows=@(); $script:sessionReport=$null
    $applyFilter={
      $needle=$search.Text.Trim().ToLower(); $tag="$($filter.SelectedItem.Tag)"
      $items=@($script:sessionRows | Where-Object { ($tag -eq 'all' -or ($tag -eq 'archived' -and $_.State -eq 'archived') -or $_.Kind -eq $tag) -and (-not $needle -or (($_.SearchText).ToLower().Contains($needle))) })
      $grid.ItemsSource=$null; $grid.ItemsSource=$items
    }.GetNewClosure()
    $load={
      try {
        $status.Text='正在读取会话…'; $w.Dispatcher.Invoke([action]{},[Windows.Threading.DispatcherPriority]::Background)
        $r=Invoke-SessionManager @('report'); $script:sessionReport=$r
        $script:sessionRows=@($r.records | ForEach-Object {
          $kind=if($_.kind -eq 'chat'){'闲聊'}elseif($_.kind -eq 'query'){'查询'}else{'工作'}
          $state=if($_.state -eq 'archived'){'已归档'}else{'活动'}
          $owner=if($_.projectName){"$($_.projectName)"}elseif($_.openId){$id="$($_.openId)"; if($id.Length -gt 12){$id.Substring(0,6)+'…'+$id.Substring($id.Length-4)}else{$id}}else{'本机'}
          $last=if([double]$_.lastUsedAt -gt 0){[DateTimeOffset]::FromUnixTimeMilliseconds([long]$_.lastUsedAt).LocalDateTime.ToString('g')}else{'-'}
          [pscustomobject]@{ Key="$($_.key)"; State="$($_.state)"; Kind="$($_.kind)"; StateLabel=$state; KindLabel=$kind; AI="$($_.profileLabel)"; Owner=$owner; Title="$($_.title)"; LastUsed=$last; Size=Format-SessionBytes ([double]$_.sizeBytes); SearchText=("$owner $($_.title) $($_.profileLabel) $($_.projectPath) $($_.openId)") }
        })
        $s=$r.summary; $summary.Text="闲聊 $($s.activeChat) · 查询 $($s.activeQuery) · 工作 $($s.activeWork) · 归档 $($s.archived) · 已统计 $(Format-SessionBytes ([double]$s.sizeBytes))"
        $status.Text="自动规则：闲聊/查询 $($s.archiveDays) 天归档，$($s.deleteDays) 天删除；工作会话仅手动归档。"; & $applyFilter
      } catch { $status.Text='读取失败：'+$_.Exception.Message }
    }.GetNewClosure()
    $runAction={ param($verb,$key)
      try { $status.Text='正在执行…'; $w.Dispatcher.Invoke([action]{},[Windows.Threading.DispatcherPriority]::Background); $null=Invoke-SessionManager @($verb,$key); & $load }
      catch { $status.Text='操作失败：'+$_.Exception.Message }
    }.GetNewClosure()
    $refresh.Add_Click({ & $load }.GetNewClosure()); $search.Add_TextChanged({ & $applyFilter }.GetNewClosure()); $filter.Add_SelectionChanged({ & $applyFilter }.GetNewClosure())
    $cleanup.Add_Click({ try { $status.Text='正在清理安全垃圾与到期会话…'; $null=Invoke-SessionManager @('cleanup'); & $load } catch { $status.Text='清理失败：'+$_.Exception.Message } }.GetNewClosure())
    $archive.Add_Click({ $row=$grid.SelectedItem; if(-not $row -or $row.State -ne 'active'){ $status.Text='先选择一条活动会话。'; return }; $msg=if($row.Kind -eq 'work'){'归档这个项目工作会话？归档可恢复，且工作会话不会被自动删除。'}else{'归档这条飞书会话？归档后可恢复，超过 30 天未使用仍会自动删除。'}; if([Windows.MessageBox]::Show($msg,'归档会话','YesNo','Question') -eq 'Yes'){ & $runAction 'archive' $row.Key } }.GetNewClosure())
    $restore.Add_Click({ $row=$grid.SelectedItem; if(-not $row -or $row.State -ne 'archived'){ $status.Text='先选择一条已归档会话。'; return }; & $runAction 'restore' $row.Key }.GetNewClosure())
    $delete.Add_Click({ $row=$grid.SelectedItem; if(-not $row){$status.Text='先选择一条会话。';return}; if([Windows.MessageBox]::Show('永久删除所选会话及其底层记录？此操作无法撤销。','永久删除','YesNo','Warning') -eq 'Yes'){ & $runAction 'delete' $row.Key } }.GetNewClosure())
    & $load
    if($SessionSelfTest){ $st=New-Object Windows.Threading.DispatcherTimer; $st.Interval=[TimeSpan]::FromMilliseconds(700); $st.Add_Tick({$st.Stop();$w.Close()}); $st.Start() }
    $null=$w.ShowDialog()
  } catch { Set-Flash ('打开会话管理出错: ' + $_.Exception.Message) }
}

# Wipe every caller/profile query scratch session for one project through the shared manager.
function Clear-ProjectQuery($projPath){
  return [int](Invoke-SessionManager @('clear-query',$projPath,'','')).deleted
}
function New-ProjectCard($proj){
  $b = New-Object Windows.Controls.Border
  $b.CornerRadius='7'; $b.Padding='14,11'; $b.Margin='0,0,0,8'; $b.Focusable=$true
  $b.Background=$win.FindResource('Panel'); $b.BorderBrush=$win.FindResource('Border0'); $b.BorderThickness='1'
  $dp = New-Object Windows.Controls.DockPanel
  $chk = New-Object Windows.Controls.CheckBox
  $chk.Style=$win.FindResource('Chk'); $chk.VerticalAlignment='Center'; [Windows.Controls.DockPanel]::SetDock($chk,'Left'); $chk.Margin='0,0,14,0'
  $right = New-Object Windows.Controls.TextBlock
  $right.Text= $(if($proj.lastUsedUtc){ $proj.lastUsedUtc.ToLocalTime().ToString('g') } else { '' })
  $right.Foreground=$win.FindResource('Muted'); $right.FontSize=11.5; $right.VerticalAlignment='Center'; [Windows.Controls.DockPanel]::SetDock($right,'Right')
  $rm = New-Object Windows.Controls.Button
  $rm.Content=[string][char]0x2715; $rm.Style=$win.FindResource('LinkBtn'); $rm.Foreground=$win.FindResource('Muted'); $rm.FontSize=13; $rm.VerticalAlignment='Center'; $rm.Margin='12,0,0,0'; $rm.ToolTip='从列表移除'
  [Windows.Controls.DockPanel]::SetDock($rm,'Right')
  $rm.Add_Click({ Remove-ProjectCard $proj.path }.GetNewClosure())
  # per-project "清空只读查询会话" (this project only)
  $clr = New-Object Windows.Controls.Button
  $clr.Content='清空查询'; $clr.Style=$win.FindResource('LinkBtn'); $clr.Foreground=$win.FindResource('Muted'); $clr.FontSize=11.5; $clr.VerticalAlignment='Center'; $clr.Margin='12,0,0,0'; $clr.ToolTip='删除本项目所有飞书用户、所有 AI 的只读查询记忆'
  [Windows.Controls.DockPanel]::SetDock($clr,'Right')
  $clr.Add_Click({
    if([Windows.MessageBox]::Show(("清空「"+$proj.name+"」所有用户、所有 AI 的只读查询记忆？"),'清空查询','YesNo','Warning') -eq 'Yes'){
      $n = Clear-ProjectQuery $proj.path; Set-Flash ("已清空「" + $proj.name + "」的查询记忆(" + $n + " 个会话)")
    }
  }.GetNewClosure())
  $sp = New-Object Windows.Controls.StackPanel
  $nameRow = New-Object Windows.Controls.StackPanel; $nameRow.Orientation='Horizontal'
  $nm = New-Object Windows.Controls.TextBlock; $nm.Text=$proj.name; $nm.Foreground=$win.FindResource('Ink'); $nm.FontWeight='SemiBold'; $nm.FontSize=14
  $nameRow.Children.Add($nm) | Out-Null
  if($proj.isGit){
    $badge = New-Object Windows.Controls.Border; $badge.CornerRadius='4'; $badge.Background=$win.FindResource('AccentSoft'); $badge.Padding='7,1'; $badge.Margin='8,0,0,0'; $badge.VerticalAlignment='Center'
    $bt = New-Object Windows.Controls.TextBlock; $bt.Text='git'; $bt.Foreground=$win.FindResource('Accent'); $bt.FontSize=10; $badge.Child=$bt; $nameRow.Children.Add($badge) | Out-Null
  }
  $pt = New-Object Windows.Controls.TextBlock; $pt.Text=$proj.path; $pt.Foreground=$win.FindResource('Muted'); $pt.FontSize=12; $pt.TextTrimming='CharacterEllipsis'; $pt.Margin='0,2,0,0'
  $sp.Children.Add($nameRow) | Out-Null; $sp.Children.Add($pt) | Out-Null
  $dp.Children.Add($chk) | Out-Null; $dp.Children.Add($rm) | Out-Null; $dp.Children.Add($right) | Out-Null; $dp.Children.Add($clr) | Out-Null; $dp.Children.Add($sp) | Out-Null
  $b.Child=$dp
  $b.Add_KeyDown({ param($s,$e) if($e.Key -eq 'Space'){ $chk.IsChecked = -not $chk.IsChecked; $e.Handled=$true } }.GetNewClosure())
  $b.Add_MouseEnter({ $b.Background = $win.FindResource('PanelHover') }.GetNewClosure())
  $b.Add_MouseLeave({ $b.Background = $win.FindResource('Panel') }.GetNewClosure())
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
    [void](Update-CcuConfig { param($live) $live.customProjects=@(@($live.customProjects)|Where-Object{ $_.path -ne $path }) })
  } else {
    [void](Update-CcuConfig { param($live)
      $hid=@(); if($live.hiddenProjects){$hid=@($live.hiddenProjects)}; if($hid -notcontains $path){$hid+=$path}
      $live.hiddenProjects=$hid
    })
  }
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
$els.FooterPath.Content = "项目源: $projectHome    ·    运行副本: $PSScriptRoot"
$els.FooterPath.Add_Click({ $t = if(Test-Path $projectHome){ $projectHome } else { $PSScriptRoot }; Start-Process explorer.exe $t }.GetNewClosure())

# ---- screenshot mode ----
if($RenderTo){
  try {
    $m="$($cfg.feishuChatProfile)"; if(-not $m){$m='openai-sol'}
    $els.ChatModelText.Text = switch($m){ 'openai-sol'{'GPT-5.6 Sol'} 'deepseek-v4'{'DeepSeek V4'} 'deepseek-v4-pro'{'DeepSeek V4 Pro'} 'claude-default'{'Claude 默认'} 'claude-fable-5'{'Claude Fable 5'} 'claude-opus'{'Claude Opus'} 'claude-sonnet'{'Claude Sonnet'} 'claude-haiku'{'Claude Haiku'} default{$m} }
    $els.ModelAccent.Background=New-Brush $(if($m -like 'openai-*'){'#FF20B6A4'}elseif($m -like 'deepseek-*'){'#FF4B8DFF'}else{'#FFD8795B'})
    $els.OpenAIStateText.Text=if("$($cfg.openaiApiKey)"){'待检测'}else{'未配置'}; $els.OpenAIStateText.Foreground=$win.FindResource('Muted')
    $els.DeepSeekStateText.Text=if("$($cfg.deepseekApiKey)"){'待检测'}else{'未配置'}; $els.DeepSeekStateText.Foreground=$win.FindResource('Muted')
    $els.ClaudeStateText.Text=if(Get-ClaudeCmd){'待检测'}else{'未安装'}; $els.ClaudeStateText.Foreground=$win.FindResource('Muted')
    $iv=15; try{$iv=[int]$cfg.probeIntervalMinutes}catch{}; $els.IntervalText.Text="${iv} 分钟"
  } catch {}
  $win.WindowStartupLocation='Manual'; $win.Left=-12000; $win.Top=-12000; $win.ShowInTaskbar=$false
  $win.Show(); $win.Dispatcher.Invoke([action]{}, [System.Windows.Threading.DispatcherPriority]::Loaded)
  Start-Sleep -Milliseconds 500
  $win.Dispatcher.Invoke([action]{}, [System.Windows.Threading.DispatcherPriority]::ContextIdle)
  # render via VisualBrush: Render($win.Content) directly yields a blank bitmap for a
  # layered (AllowsTransparency) window parked off-screen
  $dv = New-Object Windows.Media.DrawingVisual
  $dc = $dv.RenderOpen()
  $vb = New-Object Windows.Media.VisualBrush $win.Content
  $dc.DrawRectangle($vb, $null, (New-Object Windows.Rect 0,0,1120,760))
  $dc.Close()
  $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap 1120,760,96,96,([Windows.Media.PixelFormats]::Pbgra32)
  $rtb.Render($dv)
  $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
  $fs = [IO.File]::Open($RenderTo,'Create'); $enc.Save($fs); $fs.Close(); $win.Close(); return
}

function Get-Selected { $script:cards | Where-Object { $_.check.IsChecked } | ForEach-Object { [pscustomobject]@{ name=$_.proj.name; path=$_.proj.path } } }
function Set-StatusLine($t){ $els.StatusText.Text = $t; $els.StatusText.ToolTip = $t }  # tooltip carries the full text when truncated
function Write-GuiError($context, $errorRecord){
  try {
    $detail = if($errorRecord.Exception){ $errorRecord.Exception.ToString() } else { "$errorRecord" }
    $line = (Get-Date).ToString('s') + '  [' + $context + '] ' + $detail + "`r`n"
    [System.IO.File]::AppendAllText((Join-Path $script:LogDir 'gui-error.log'), $line, (New-Object System.Text.UTF8Encoding($false)))
  } catch {}
}
function Assert-ChipContentFits($button, $name){
  $button.ApplyTemplate() | Out-Null; $button.UpdateLayout()
  $contentHost = $button.Template.FindName('contentHost', $button)
  if(-not $contentHost){ throw "$name 缺少 contentHost" }
  if($button.Content.ActualWidth -gt ($contentHost.ActualWidth + 1)){ throw "$name 内容被裁切: content=$([Math]::Round($button.Content.ActualWidth,1)), host=$([Math]::Round($contentHost.ActualWidth,1))" }
}

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
function Update-IntervalChip { $v = 15; try { $v = [int](Get-CcuConfig).probeIntervalMinutes } catch {}; if($v -lt 2){ $v = 15 }; $els.IntervalText.Text = "${v} 分钟" }
Update-IntervalChip

# AI selector: provider health determines which model options are selectable. Unavailable providers
# keep their configuration fields below, so a user can repair login/API keys without exposing dead
# models in the selector.
$script:AIModelDefinitions = @(
  [pscustomobject]@{ provider='openai'; providerLabel='OpenAI'; id='openai-sol'; label='GPT-5.6 Sol' },
  [pscustomobject]@{ provider='deepseek'; providerLabel='DeepSeek'; id='deepseek-v4'; label='V4' },
  [pscustomobject]@{ provider='deepseek'; providerLabel='DeepSeek'; id='deepseek-v4-pro'; label='V4 Pro' },
  [pscustomobject]@{ provider='claude'; providerLabel='Claude'; id='claude-default'; label='默认' },
  [pscustomobject]@{ provider='claude'; providerLabel='Claude'; id='claude-fable-5'; label='Fable 5' },
  [pscustomobject]@{ provider='claude'; providerLabel='Claude'; id='claude-opus'; label='Opus' },
  [pscustomobject]@{ provider='claude'; providerLabel='Claude'; id='claude-sonnet'; label='Sonnet' },
  [pscustomobject]@{ provider='claude'; providerLabel='Claude'; id='claude-haiku'; label='Haiku' }
)
function Get-ProviderHealthStatus([string]$Provider){
  switch($Provider){ 'openai' { "$($sync.openaiStatus)" } 'deepseek' { "$($sync.deepseekStatus)" } 'claude' { "$($sync.claudeStatus)" } default { 'unavailable' } }
}
function Set-AIModelOptions($Combo, [string]$Current, [bool]$ForceAll=$false){
  $before=''; try { $before="$($Combo.SelectedItem.Tag)" } catch {}
  $first=$null; $selected=$null; $availableProviders=@()
  foreach($provider in @('openai','deepseek','claude')){
    $show=$ForceAll -or (Get-ProviderHealthStatus $provider) -eq 'available'
    $label=(@($script:AIModelDefinitions | Where-Object { $_.provider -eq $provider })[0]).providerLabel
    if($show){ $availableProviders += $label }
    foreach($item in @($Combo.Items)){
      $tag="$($item.Tag)"
      $belongs=($tag -eq ('provider:'+$provider)) -or [bool]($script:AIModelDefinitions | Where-Object { $_.provider -eq $provider -and $_.id -eq $tag })
      if(-not $belongs){ continue }
      $item.Visibility=if($show){'Visible'}else{'Collapsed'}
      if($show -and $tag -notlike 'provider:*'){
        if(-not $first){ $first=$item }
        if($tag -eq $before -or (-not $before -and $tag -eq $Current)){ $selected=$item }
      }
    }
  }
  $Combo.IsEnabled=[bool]$first
  if($selected){ $Combo.SelectedItem=$selected } elseif($first){ $Combo.SelectedItem=$first }
  return $availableProviders
}
function Get-ModelLabel($m){ switch("$m".ToLower()){ 'openai-sol' { 'GPT-5.6 Sol' } 'deepseek-v4' { 'DeepSeek V4' } 'deepseek-v4-pro' { 'DeepSeek V4 Pro' } 'claude-default' { 'Claude 默认' } 'claude-fable-5' { 'Claude Fable 5' } 'claude-opus' { 'Claude Opus' } 'claude-sonnet' { 'Claude Sonnet' } 'claude-haiku' { 'Claude Haiku' } default { "$m" } } }
function Get-LegacyModel($p){ switch("$p".ToLower()){ 'claude-fable-5' { 'claude-fable-5' } 'claude-opus' { 'opus' } 'claude-sonnet' { 'sonnet' } 'claude-haiku' { 'haiku' } default { '' } } }
function Update-ChatModelChip {
  $m='openai-sol'; try { $m="$((Get-CcuConfig).feishuChatProfile)" } catch {}; if(-not $m){ $m='openai-sol' }
  $els.ChatModelText.Text = Get-ModelLabel $m
  try {
    $hex = if($m -like 'openai-*'){ '#FF20B6A4' } elseif($m -like 'deepseek-*'){ '#FF4B8DFF' } else { '#FFD8795B' }
    $els.ModelAccent.Background = New-Brush $hex
  } catch {}
}
function Get-ProviderPresentation {
  param([string]$Provider, [string]$Status, [string]$Reason, [bool]$Configured=$true, [string]$Route='')
  if($Provider -ne 'claude' -and -not $Configured){ return [pscustomobject]@{ text='未配置'; color='Muted'; tip='未配置 API Key' } }
  if($Provider -eq 'claude' -and -not $Configured){ return [pscustomobject]@{ text='未安装'; color='Muted'; tip='未找到 Claude CLI' } }
  switch($Status){
    'pending'     { return [pscustomobject]@{ text='待检测'; color='Muted'; tip='等待启动探测' } }
    'checking'    { return [pscustomobject]@{ text='检测中'; color='Blue'; tip='正在发起最小真实请求' } }
    'available'   {
      if($Route -eq 'direct'){ return [pscustomobject]@{ text='直连可用'; color='Green'; tip='最小真实请求已通过直连成功' } }
      if($Route -eq 'proxy'){ return [pscustomobject]@{ text='代理可用'; color='Green'; tip='直连网络失败后，最小真实请求已通过备用代理成功' } }
      return [pscustomobject]@{ text='可用'; color='Green'; tip='真实请求成功' }
    }
    'unconfigured'{ return [pscustomobject]@{ text='未配置'; color='Muted'; tip='未配置 API Key' } }
  }
  $isClaude = ($Provider -eq 'claude')
  switch($Reason){
    'auth'              { return [pscustomobject]@{ text=$(if($isClaude){'未登录'}else{'认证失败'}); color='Danger'; tip='真实请求认证失败' } }
    'billing'           { return [pscustomobject]@{ text='订阅不可用'; color='Danger'; tip='订阅、余额或计费状态不可用' } }
    'rate_limit'        { return [pscustomobject]@{ text='额度受限'; color='Danger'; tip='真实请求返回限流或额度不足' } }
    'limited'           { return [pscustomobject]@{ text='额度受限'; color='Danger'; tip='真实请求返回限流或额度不足' } }
    'proxy_unavailable' { return [pscustomobject]@{ text='代理异常'; color='Danger'; tip='直连失败，配置的备用代理也不可用' } }
    'transient'         { return [pscustomobject]@{ text='网络异常'; color='Danger'; tip='网络或服务暂时不可用' } }
    'timeout'           { return [pscustomobject]@{ text='检测超时'; color='Danger'; tip='真实请求超时' } }
    'model_unavailable' { return [pscustomobject]@{ text='模型不可用'; color='Danger'; tip='服务端拒绝当前模型' } }
    'command_missing'   { return [pscustomobject]@{ text='未安装'; color='Muted'; tip='未找到本地命令' } }
    'no-claude'         { return [pscustomobject]@{ text='未安装'; color='Muted'; tip='未找到 Claude CLI' } }
    default             { return [pscustomobject]@{ text='不可用'; color='Danger'; tip='真实请求未成功' } }
  }
}
function Set-ProviderPresentation($Control, $Presentation){
  $Control.Text = $Presentation.text
  $Control.Foreground = $win.FindResource($Presentation.color)
  $Control.ToolTip = $Presentation.tip
}
function Update-ProviderState {
  try {
    $c=Get-CcuConfig
    Set-ProviderPresentation $els.OpenAIStateText (Get-ProviderPresentation 'openai' "$($sync.openaiStatus)" "$($sync.openaiReason)" ([bool]"$($c.openaiApiKey)") "$($sync.openaiRoute)")
    Set-ProviderPresentation $els.DeepSeekStateText (Get-ProviderPresentation 'deepseek' "$($sync.deepseekStatus)" "$($sync.deepseekReason)" ([bool]"$($c.deepseekApiKey)") "$($sync.deepseekRoute)")
    Set-ProviderPresentation $els.ClaudeStateText (Get-ProviderPresentation 'claude' "$($sync.claudeStatus)" "$($sync.claudeReason)" ([bool](Get-ClaudeCmd)))
  } catch {}
}
function Update-ClaudeQuotaState {
  if($sync.probing){ $els.ResetText.Text='Claude 检测中…'; return }
  $probed = ($sync.probedAt -ne [datetime]::MinValue)
  if(-not $probed){ $els.ResetText.Text='等待实探'; return }
  if(-not $sync.ready){
    if("$($sync.claudeReason)" -in @('limited','rate_limit')){
      $nowU=[DateTimeOffset]::UtcNow; $parts=@()
      if($sync.fhReset -and $sync.fhReset -gt $nowU){ $parts += '5h 限流 '+(Format-Countdown ($sync.fhReset-$nowU).TotalSeconds) }
      elseif($null -ne $sync.fhUtil){ $parts += '5h '+[int][Math]::Round([double]$sync.fhUtil*100)+'%' }
      if($sync.sdReset -and $sync.sdReset -gt $nowU){ $parts += '7d 限流 '+(Format-Countdown ($sync.sdReset-$nowU).TotalSeconds) }
      elseif($null -ne $sync.sdUtil){ $parts += '7d '+[int][Math]::Round([double]$sync.sdUtil*100)+'%' }
      $els.ResetText.Text = if($parts.Count){ $parts -join ' · ' } else { 'Claude 额度受限' }
      return
    }
    $els.ResetText.Text = switch("$($sync.claudeReason)"){
      'auth' {'Claude 未登录'} 'billing' {'Claude 订阅不可用'} 'limited' {'Claude 额度受限'}
      'rate_limit' {'Claude 额度受限'} 'transient' {'Claude 网络异常'} 'timeout' {'Claude 检测超时'}
      'model_unavailable' {'Claude 模型不可用'} 'no-claude' {'Claude 未安装'} default {'Claude 不可用'}
    }
    return
  }
  $nowU=[DateTimeOffset]::UtcNow
  $fh=''; $sd=''
  if($sync.limited -and $sync.fhReset -and $sync.fhReset -gt $nowU){ $fh='5h 限流 '+(Format-Countdown ($sync.fhReset-$nowU).TotalSeconds) }
  elseif($null -ne $sync.fhUtil){ $fh='5h '+[int][Math]::Round([double]$sync.fhUtil*100)+'%' }
  else { $fh='5h 低' }
  if($sync.limited -and $sync.sdReset -and $sync.sdReset -gt $nowU){ $sd='7d 限流 '+(Format-Countdown ($sync.sdReset-$nowU).TotalSeconds) }
  elseif($null -ne $sync.sdUtil){ $sd='7d '+[int][Math]::Round([double]$sync.sdUtil*100)+'%' }
  $t = (@($fh,$sd) | Where-Object { $_ }) -join ' · '
  $els.ResetText.Text = if($t){ $t } else { 'Claude 可用' }
}
function Show-AISettingsWindow {
  try {
    [xml]$sx = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="AI 服务" Width="720" Height="650" WindowStartupLocation="CenterOwner" ResizeMode="NoResize" Background="#FF101214" FontFamily="Microsoft YaHei UI, Segoe UI" UseLayoutRounding="True" SnapsToDevicePixels="True">
  <Window.Resources>
    <Style TargetType="TextBox"><Setter Property="Background" Value="#FF171A1D"/><Setter Property="Foreground" Value="#FFF4F6F7"/><Setter Property="BorderBrush" Value="#FF343A40"/><Setter Property="CaretBrush" Value="White"/><Setter Property="Padding" Value="10,7"/><Setter Property="FontSize" Value="13"/></Style>
    <Style TargetType="PasswordBox"><Setter Property="Background" Value="#FF171A1D"/><Setter Property="Foreground" Value="#FFF4F6F7"/><Setter Property="BorderBrush" Value="#FF343A40"/><Setter Property="Padding" Value="10,7"/><Setter Property="FontSize" Value="13"/></Style>
    <Style TargetType="ComboBoxItem">
      <Setter Property="Foreground" Value="#FFF4F6F7"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="ItemBorder" Background="#FF171A1D" Padding="10,7"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemBorder" Property="Background" Value="#FF252B30"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="ItemBorder" Property="Background" Value="#FF164A46"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType="ComboBox">
      <Setter Property="Background" Value="#FF171A1D"/><Setter Property="Foreground" Value="#FFF4F6F7"/><Setter Property="BorderBrush" Value="#FF343A40"/><Setter Property="BorderThickness" Value="1"/><Setter Property="FontSize" Value="13"/><Setter Property="Padding" Value="11,0"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid x:Name="ComboRoot"><ToggleButton x:Name="DropDownToggle" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="ToggleBorder" CornerRadius="5" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ToggleBorder" Property="BorderBrush" Value="#FF56616A"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton><ContentPresenter Margin="12,0,38,0" VerticalAlignment="Center" IsHitTestVisible="False" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/><Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,14,0" Data="M 0 0 L 4 4 L 8 0" Stroke="#FF929BA2" StrokeThickness="1.5" IsHitTestVisible="False"/><Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade"><Border Width="{Binding ActualWidth, ElementName=ComboRoot}" MaxHeight="280" Margin="0,4,0,0" Background="#FF171A1D" BorderBrush="#FF343A40" BorderThickness="1" CornerRadius="5"><ScrollViewer VerticalScrollBarVisibility="Auto"><ItemsPresenter/></ScrollViewer></Border></Popup></Grid><ControlTemplate.Triggers><Trigger Property="HasItems" Value="False"><Setter TargetName="PART_Popup" Property="MinHeight" Value="30"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType="Button">
      <Setter Property="MinWidth" Value="84"/><Setter Property="Height" Value="34"/><Setter Property="Padding" Value="12,0"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Background" Value="#FF171A1D"/><Setter Property="Foreground" Value="#FFC8CED2"/><Setter Property="BorderBrush" Value="#FF343A40"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="ButtonBorder" CornerRadius="5" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ButtonBorder" Property="BorderBrush" Value="#FF56616A"/><Setter TargetName="ButtonBorder" Property="Background" Value="#FF20252A"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="ButtonBorder" Property="Opacity" Value="0.75"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
    </Style>
  </Window.Resources>
  <Grid Margin="28,24" Background="#FF101214"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <TextBlock Text="AI 服务与默认模型" Foreground="#FFF4F6F7" FontWeight="SemiBold" FontSize="22"/>
    <TextBlock Grid.Row="1" Margin="0,5,0,0" Text="飞书用户可以独立选择模型；默认模型只列出本机刚刚实测可用的 AI 服务。" Foreground="#FF929BA2" FontSize="12.5"/>
    <Grid Grid.Row="2" Margin="0,20,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="180"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Label Content="默认模型" Target="{Binding ElementName=ModelBox}" Foreground="#FFC8CED2" FontWeight="SemiBold" VerticalAlignment="Top" Padding="0,10,0,0"/><StackPanel Grid.Column="1"><ComboBox x:Name="ModelBox" Height="38" AutomationProperties.Name="默认 AI 模型"><ComboBoxItem Tag="provider:openai" Content="OpenAI" IsEnabled="False" FontWeight="SemiBold" Foreground="#FF8E9AA3" Opacity="1" Padding="10,8,10,4"/><ComboBoxItem Tag="openai-sol" Content="    GPT-5.6 Sol" Padding="14,7,10,7"/><ComboBoxItem Tag="provider:deepseek" Content="DeepSeek" IsEnabled="False" FontWeight="SemiBold" Foreground="#FF8E9AA3" Opacity="1" Padding="10,8,10,4"/><ComboBoxItem Tag="deepseek-v4" Content="    V4" Padding="14,7,10,7"/><ComboBoxItem Tag="deepseek-v4-pro" Content="    V4 Pro" Padding="14,7,10,7"/><ComboBoxItem Tag="provider:claude" Content="Claude" IsEnabled="False" FontWeight="SemiBold" Foreground="#FF8E9AA3" Opacity="1" Padding="10,8,10,4"/><ComboBoxItem Tag="claude-default" Content="    默认" Padding="14,7,10,7"/><ComboBoxItem Tag="claude-fable-5" Content="    Fable 5" Padding="14,7,10,7"/><ComboBoxItem Tag="claude-opus" Content="    Opus" Padding="14,7,10,7"/><ComboBoxItem Tag="claude-sonnet" Content="    Sonnet" Padding="14,7,10,7"/><ComboBoxItem Tag="claude-haiku" Content="    Haiku" Padding="14,7,10,7"/></ComboBox><TextBlock x:Name="ModelHint" Margin="2,7,0,0" Foreground="#FF929BA2" FontSize="11.5" TextWrapping="Wrap"/></StackPanel></Grid>
    <Border Grid.Row="3" Height="1" Background="#FF2A3035" Margin="0,20,0,18"/>
    <TextBlock Grid.Row="4" Text="OpenAI" Foreground="#FF20B6A4" FontWeight="SemiBold" FontSize="14"/>
    <Grid Grid.Row="5" Margin="0,10,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="180"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Label Content="Responses API 地址" Target="{Binding ElementName=OpenAIBaseBox}" Foreground="#FFC8CED2" VerticalAlignment="Center" Padding="0"/><TextBox x:Name="OpenAIBaseBox" Grid.Column="1" Height="38"/></Grid>
    <Grid Grid.Row="6" Margin="0,9,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="180"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Label Content="OpenAI API Key" Target="{Binding ElementName=OpenAIKeyBox}" Foreground="#FFC8CED2" VerticalAlignment="Center" Padding="0"/><PasswordBox x:Name="OpenAIKeyBox" Grid.Column="1" Height="38"/></Grid>
    <Grid Grid.Row="7" Margin="0,18,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="180"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Label Content="DeepSeek API Key" Target="{Binding ElementName=DeepSeekKeyBox}" Foreground="#FF4B8DFF" FontWeight="SemiBold" VerticalAlignment="Center" Padding="0"/><PasswordBox x:Name="DeepSeekKeyBox" Grid.Column="1" Height="38"/></Grid>
    <Grid Grid.Row="8" Margin="0,14,0,0"><Grid.ColumnDefinitions><ColumnDefinition Width="180"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Label Content="备用代理" Target="{Binding ElementName=ProxyBox}" Foreground="#FFC8CED2" VerticalAlignment="Center" Padding="0"/><TextBox x:Name="ProxyBox" Grid.Column="1" Height="38"/></Grid>
    <TextBlock Grid.Row="9" Margin="180,9,0,0" TextWrapping="Wrap" Foreground="#FF929BA2" FontSize="11.5" Text="可留空。OpenAI / DeepSeek 先直连，仅网络失败时尝试此代理；成功线路短期缓存，正式任务不会中途换线重放。不会修改 Windows、Clash 或系统代理。"/>
    <StackPanel Grid.Row="11" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,20,0,0"><Button x:Name="ClearBtn" Content="清空密钥" Margin="0,0,8,0"/><Button x:Name="CancelBtn" Content="取消" Margin="0,0,8,0"/><Button x:Name="SaveBtn" Content="保存设置" IsDefault="True" Background="#FFFF6B2C" Foreground="White" BorderBrush="#FFFF6B2C"/></StackPanel>
  </Grid>
</Window>
'@
    $sr = New-Object System.Xml.XmlNodeReader $sx; $sw = [Windows.Markup.XamlReader]::Load($sr); $sw.Owner=$win; $script:selfTestState.opened=$true
    $mb=$sw.FindName('ModelBox'); $mh=$sw.FindName('ModelHint'); $ob=$sw.FindName('OpenAIBaseBox'); $okb=$sw.FindName('OpenAIKeyBox'); $dkb=$sw.FindName('DeepSeekKeyBox'); $pb=$sw.FindName('ProxyBox'); $c=Get-CcuConfig
    $testState=$script:selfTestState
    $cur="$($c.feishuChatProfile)"; if(-not $cur){$cur='openai-sol'}
    $refreshModelOptions = {
      $providers=@(Set-AIModelOptions -Combo $mb -Current $cur -ForceAll ([bool]$script:isUiTest))
      if($script:isUiTest){ $mh.Text='界面自测：展开全部服务，验证分组、选择和宽度。'; return }
      $hidden=@('openai','deepseek','claude') | Where-Object { (Get-ProviderHealthStatus $_) -ne 'available' } | ForEach-Object { switch($_){ 'openai' {'OpenAI'} 'deepseek' {'DeepSeek'} 'claude' {'Claude'} } }
      if($providers.Count){ $mh.Text='可用：'+($providers -join '、')+$(if($hidden.Count){'；已隐藏：'+($hidden -join '、')}else{''}) }
      elseif(@('checking','pending') -contains (Get-ProviderHealthStatus 'openai') -or @('checking','pending') -contains (Get-ProviderHealthStatus 'deepseek') -or @('checking','pending') -contains (Get-ProviderHealthStatus 'claude')){ $mh.Text='正在检测可用 AI，完成后列表会自动更新。' }
      else { $mh.Text='当前没有通过真实请求的 AI 服务；可先更新下方登录或 API Key。' }
    }.GetNewClosure()
    & $refreshModelOptions
    $validateAISettings = {
      $sw.UpdateLayout()
      foreach($control in @($mb,$mh,$ob,$okb,$dkb,$pb,$sw.FindName('ClearBtn'),$sw.FindName('CancelBtn'),$sw.FindName('SaveBtn'))){ if(-not $control){ throw 'AI 设置窗口缺少关键控件' } }
      $tags=@($mb.Items | Where-Object { "$($_.Tag)" -and "$($_.Tag)" -notlike 'provider:*' } | ForEach-Object { "$($_.Tag)" })
      $expected=@('openai-sol','deepseek-v4','deepseek-v4-pro','claude-default','claude-fable-5','claude-opus','claude-sonnet','claude-haiku')
      if($tags.Count -ne $expected.Count -or (Compare-Object $expected $tags)){ throw ('AI 模型列表不完整: ' + ($tags -join ',')) }
      $headers=@($mb.Items | Where-Object { "$($_.Tag)" -like 'provider:*' })
      if($headers.Count -ne 3 -or @($headers | Where-Object { $_.IsEnabled }).Count){ throw 'AI 模型提供商分组标题无效' }
      $original=$mb.SelectedItem
      foreach($id in $expected){ $item=@($mb.Items | Where-Object { "$($_.Tag)" -eq $id })[0]; $mb.SelectedItem=$item; if("$($mb.SelectedItem.Tag)" -ne $id){ throw "AI 模型切换失败: $id" } }
      $mb.SelectedItem=$original
      $oldOpenAI=$sync.openaiStatus; $oldDeepSeek=$sync.deepseekStatus; $oldClaude=$sync.claudeStatus
      try {
        $sync.openaiStatus='available'; $sync.deepseekStatus='available'; $sync.claudeStatus='unavailable'
        [void](Set-AIModelOptions -Combo $mb -Current 'openai-sol' -ForceAll $false)
        $visible=@($mb.Items | Where-Object { $_.Visibility -eq 'Visible' -and "$($_.Tag)" -and "$($_.Tag)" -notlike 'provider:*' } | ForEach-Object { "$($_.Tag)" })
        if($visible -join ',' -ne 'openai-sol,deepseek-v4,deepseek-v4-pro'){ throw ('不可用 Claude 未从模型列表隐藏: '+($visible -join ',')) }
      } finally {
        $sync.openaiStatus=$oldOpenAI; $sync.deepseekStatus=$oldDeepSeek; $sync.claudeStatus=$oldClaude
        [void](Set-AIModelOptions -Combo $mb -Current $cur -ForceAll $true)
      }
      $testState.validated=$true
    }.GetNewClosure()
    $ob.Text="$($c.openaiBaseUrl)"; $okb.Password="$($c.openaiApiKey)"; $dkb.Password="$($c.deepseekApiKey)"; $pb.Text="$($c.aiProxy)"
    $modelTimer=New-Object Windows.Threading.DispatcherTimer; $modelTimer.Interval=[TimeSpan]::FromMilliseconds(500); $lastModelHealth=''
    $modelTimer.Add_Tick({ $sig=(Get-ProviderHealthStatus 'openai')+'|'+(Get-ProviderHealthStatus 'deepseek')+'|'+(Get-ProviderHealthStatus 'claude'); if($sig -ne $lastModelHealth){ $lastModelHealth=$sig; & $refreshModelOptions } }.GetNewClosure())
    $sw.Add_Closed({ try { $modelTimer.Stop() } catch {} }.GetNewClosure()); if(-not $script:isUiTest){ $modelTimer.Start() }
    $sw.FindName('CancelBtn').Add_Click({ $sw.Close() }.GetNewClosure())
    $sw.FindName('ClearBtn').Add_Click({
      if([Windows.MessageBox]::Show('清空输入框中的 OpenAI 和 DeepSeek 密钥？只有点击「保存设置」后才会写入。','清空密钥','YesNo','Warning') -eq 'Yes'){ $okb.Password=''; $dkb.Password='' }
    }.GetNewClosure())
    $sw.FindName('SaveBtn').Add_Click({
      $baseUrl=$ob.Text.Trim(); if(-not $baseUrl){$baseUrl='https://api.openai.com/v1'}
      $picked="$($mb.SelectedItem.Tag)"; $openKey=$okb.Password; $deepKey=$dkb.Password; $proxy=$pb.Text.Trim()
      $cc=Update-CcuConfig { param($live)
        $choice=$picked; if(-not $choice){$choice="$($live.feishuChatProfile)"}; if(-not $choice){$choice='openai-sol'}
        $live.openaiBaseUrl=$baseUrl; $live.feishuChatProfile=$choice; $live.feishuChatModel=Get-LegacyModel $choice
        $live.openaiApiKey=$openKey; $live.openaiReasoning='xhigh'; $live.deepseekApiKey=$deepKey; $live.aiProxy=$proxy
      }
      $sync.openaiStatus=if("$($cc.openaiApiKey)"){'checking'}else{'unconfigured'}; $sync.openaiReason=''; $sync.openaiRoute=''
      $sync.deepseekStatus=if("$($cc.deepseekApiKey)"){'checking'}else{'unconfigured'}; $sync.deepseekReason=''; $sync.deepseekRoute=''
      $sync.claudeStatus='checking'; $sync.claudeReason=''
      if(-not $script:isUiTest){ $sync.providerReq=$true; $sync.req=$true }
      $sw.DialogResult=$true; $sw.Close()
    }.GetNewClosure())
    if($AISettingsRenderTo){
      $sw.WindowStartupLocation='Manual'; $sw.Left=-12000; $sw.Top=-12000; $sw.ShowInTaskbar=$false; $sw.Show()
      $sw.Dispatcher.Invoke([action]{},[System.Windows.Threading.DispatcherPriority]::Loaded); & $validateAISettings
      $visual=$sw.Content; $visual.UpdateLayout(); $w=[int][Math]::Ceiling($visual.ActualWidth); $h=[int][Math]::Ceiling($visual.ActualHeight)
      $dv=New-Object Windows.Media.DrawingVisual; $dc=$dv.RenderOpen(); $vb=New-Object Windows.Media.VisualBrush $visual; $dc.DrawRectangle($vb,$null,(New-Object Windows.Rect 0,0,$w,$h)); $dc.Close()
      $rtb=New-Object Windows.Media.Imaging.RenderTargetBitmap $w,$h,96,96,([Windows.Media.PixelFormats]::Pbgra32); $rtb.Render($dv)
      $enc=New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb)); $fs=[IO.File]::Open($AISettingsRenderTo,'Create'); $enc.Save($fs); $fs.Close(); $sw.Close(); return
    }
    if($SelfTest){
      $at=New-Object Windows.Threading.DispatcherTimer; $at.Interval=[TimeSpan]::FromMilliseconds(500)
      $at.Add_Tick({ try { $at.Stop(); & $validateAISettings } catch { $testState.failed=$true; Write-GuiError 'AI settings self-test' $_ } finally { $sw.Close() } }.GetNewClosure()); $at.Start()
    }
    if($sw.ShowDialog()){ Update-ChatModelChip; Update-ProviderState; Set-Flash 'AI 服务设置已保存' }
  } catch {
    $script:selfTestState.failed=$true; Write-GuiError 'AI settings' $_
    Set-Flash 'AI 服务窗口打开失败，详情已写入 gui-error.log'
  }
}
Update-ChatModelChip
Update-ProviderState
$els.ChatModelChip.Add_Click({ Show-AISettingsWindow })
$els.IntervalChip.Add_Click({
  try {
    $c=Update-CcuConfig { param($live)
      $cur=15; try{$cur=[int]$live.probeIntervalMinutes}catch{}
      $live.probeIntervalMinutes=if($cur -lt 15){15}elseif($cur -lt 30){30}else{5}
    }
    $next=[int]$c.probeIntervalMinutes
    Update-IntervalChip
    Set-Flash "实探间隔 ${next}m"
  } catch { Set-Flash ('设置出错: ' + $_.Exception.Message) }
})
# The refresh chip rechecks all providers; Claude additionally returns its exact quota windows.
$els.ResetChip.Add_Click({
  if(-not $script:isUiTest){
    $sync.claudeStatus='checking'; $sync.claudeReason=''
    $sync.openaiStatus='checking'; $sync.openaiReason=''; $sync.openaiRoute=''; $sync.deepseekStatus='checking'; $sync.deepseekReason=''; $sync.deepseekRoute=''
    $sync.req=$true; $sync.providerReq=$true; Set-Flash '正在实探三家 AI…'
  }
})
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
      [void](Update-CcuConfig { param($live)
        $cust=@(); if($live.customProjects){$cust=@($live.customProjects)}
        if(-not ($cust|Where-Object{$_.path -eq $path})){$cust+=[pscustomobject]@{name=$proj.name;path=$path}}
        $live.customProjects=$cust
      })
      Set-Flash "已添加并勾选: $($proj.name)"
    }
  } catch { Set-Flash ('添加出错: ' + $_.Exception.Message) }
})
$els.BtnArm.Add_Click({
  try {
    $sel = @(Get-Selected)
    if($sel.Count -eq 0){ Set-Flash '请先勾选至少一个项目'; return }
    $cycle=New-CcuCycleId
    $c=Update-CcuConfig { param($live) $live.enabled=$true; $live.armed=$true; $live.armCycleId=$cycle; $live.selected=$sel; $live.skipPermissions=$true; $live.dirtyGuard='stash' }
    # fresh cycle: stale sawLimited would fire instantly, stale projectStatus would skip projects
    $st = Get-CcuState; $st.cycleId=$c.armCycleId; $st.sawLimited=$false; $st.projectStatus=@{}; $st.phase='waiting'; $st.firedForId=$null; Set-CcuState $st -Force
    Set-Flash "已布防 · $($sel.Count) 个项目"
  } catch { Set-Flash ('布防出错: ' + $_.Exception.Message) }
})
$els.BtnDisarm.Add_Click({
  try {
    $cycle=New-CcuCycleId
    $c=Update-CcuConfig { param($live) $live.enabled=$false; $live.armed=$false; $live.armCycleId=$cycle }
    $st=Get-CcuState; $st.cycleId=$c.armCycleId; $st.phase='idle'; Set-CcuState $st -Force
    Set-Flash '已解除布防 · 正在停止当前续跑'
  }
  catch { Set-Flash ('解除出错: ' + $_.Exception.Message) }
})
$els.BtnClearLog.Add_Click({
  try {
    if([Windows.MessageBox]::Show('清空当前运行日志？此操作无法撤销。','清空日志','YesNo','Warning') -ne 'Yes'){ return }
    $lf = Get-CurLogFile; if($lf){ [System.IO.File]::WriteAllText($lf.FullName, '') }
    $els.LogText.Inlines.Clear(); $script:lastLogText = $null
    Set-Flash '日志已清空'
  } catch { Set-Flash ('清空出错: ' + $_.Exception.Message) }
})
$els.BtnPopLog.Add_Click({ Show-LogWindow })
$els.BtnForgetChat.Add_Click({
  try {
    if([Windows.MessageBox]::Show('清空所有飞书用户、所有 AI 的闲聊记忆？底层 Claude/Codex 会话也会永久删除。','清空闲聊','YesNo','Warning') -ne 'Yes'){ return }
    $r=Invoke-SessionManager @('forget-chat',''); Set-Flash ("已清空全部飞书闲聊记忆("+$r.deleted+" 个会话)")
  } catch { Set-Flash ('清空出错: ' + $_.Exception.Message) }
})
$els.BtnClearQuery.Add_Click({
  try {
    if([Windows.MessageBox]::Show('清空所有项目、所有飞书用户、所有 AI 的只读查询记忆？底层会话也会永久删除。','清空查询','YesNo','Warning') -ne 'Yes'){ return }
    $r=Invoke-SessionManager @('clear-query','','',''); Set-Flash ("已清空全部查询记忆("+$r.deleted+" 个会话)")
  } catch { Set-Flash ('清空查询出错: ' + $_.Exception.Message) }
})
$els.BtnAuthUsers.Add_Click({ Show-AuthWindow })
$els.BtnSessions.Add_Click({ Show-SessionManagerWindow })
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
    $dlg.FileName = 'AI-Resume-日志-' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.log'
    $dlg.InitialDirectory = [Environment]::GetFolderPath('Desktop')
    $dlg.Filter = '日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*'
    if($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK){ Set-Flash '已取消导出'; return }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('AI Resume 日志导出 · ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
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
    $sel=@(Get-Selected); [void](Update-CcuConfig { param($live) $live.selected=$sel })
    Set-Flash '预演中(只算不跑)…'
    # a marker guarantees a fresh visible line even if the checker path changes
    Write-CcuLog ('----- 预演 @ ' + (Get-Date).ToString('HH:mm:ss') + '  (' + $sel.Count + ' 个项目已选) -----') 'info'
    Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'checker.ps1'),'-DryRun' -WindowStyle Hidden -Wait
    $els.LogText.Text = Read-LogTail; $els.LogScroll.ScrollToEnd()
    Set-Flash '预演完成 · 见下方日志'
  } catch { Set-Flash ('预演出错: ' + $_.Exception.Message) }
})

# ---- background health runspace: real probes on startup and on demand, never on a timer loop ----
# Claude uses its stream-json quota probe. OpenAI and DeepSeek run concurrent, no-tool "OK"
# requests through their production runners. The UI thread only reads synchronized results.
$rs = [runspacefactory]::CreateRunspace(); $rs.ApartmentState='MTA'; $rs.ThreadOptions='ReuseThread'; $rs.Open()
$rs.SessionStateProxy.SetVariable('sync',$sync)
$rs.SessionStateProxy.SetVariable('libPath',(Join-Path $PSScriptRoot 'lib.ps1'))
$rs.SessionStateProxy.SetVariable('providerHealthPath',(Join-Path $PSScriptRoot 'provider-health.js'))
$ps = [powershell]::Create(); $ps.Runspace=$rs
[void]$ps.AddScript({
  . $libPath
  while($true){
    if($sync.req -and -not $sync.probing){
      $sync.req=$false; $sync.probing=$true; $sync.err=$null
      $sync.claudeStatus='checking'; $sync.claudeReason=''
      try {
        $cfg=Get-CcuConfig; $pr=Test-ClaudeReady -Model $cfg.probeModel
        $sync.limited=($pr.reason -eq 'limited'); $sync.ready=[bool]$pr.ready
        $sync.claudeStatus=if($pr.ready){'available'}else{'unavailable'}; $sync.claudeReason="$($pr.reason)"
        $sync.fhUtil=$pr.fiveHourUtil; $sync.sdUtil=$pr.sevenDayUtil
        $sync.fhReset = if($pr.fiveHourResetUtc){ $pr.fiveHourResetUtc } else { $null }
        $sync.sdReset = if($pr.sevenDayResetUtc){ $pr.sevenDayResetUtc } else { $null }
        $sync.probedAt=Get-Date
        try { $st=Get-CcuState; $st=Save-RealResetFromProbe -Probe $pr -State $st; Set-CcuState $st } catch {}
      } catch {
        $sync.err=$_.Exception.Message; $sync.ready=$false; $sync.claudeStatus='unavailable'; $sync.claudeReason='unknown'; $sync.probedAt=Get-Date
      }
      $sync.probing=$false
    }
    if($sync.providerReq -and -not $sync.providerProbing){
      $sync.providerReq=$false; $sync.providerProbing=$true
      $sync.openaiStatus='checking'; $sync.openaiReason=''; $sync.openaiRoute=''; $sync.deepseekStatus='checking'; $sync.deepseekReason=''; $sync.deepseekRoute=''
      try {
        if(-not (Test-Path $providerHealthPath)){ throw 'provider-health.js missing' }
        $raw = (& node $providerHealthPath 2>$null | Out-String).Trim()
        if($LASTEXITCODE -ne 0 -or -not $raw){ throw 'provider health probe failed' }
        $health = $raw | ConvertFrom-Json
        foreach($name in @('openai','deepseek')){
          $item=$health.providers.$name
          if(-not $item){ throw "provider result missing: $name" }
          $sync[($name+'Status')]="$($item.status)"; $sync[($name+'Reason')]="$($item.reason)"; $sync[($name+'Route')]="$($item.route)"
        }
        $sync.providerProbedAt=Get-Date
      } catch {
        $sync.openaiStatus='unavailable'; $sync.openaiReason='transient'; $sync.openaiRoute=''
        $sync.deepseekStatus='unavailable'; $sync.deepseekReason='transient'; $sync.deepseekRoute=''
        $sync.providerProbedAt=Get-Date
      }
      $sync.providerProbing=$false
    }
    Start-Sleep -Milliseconds 400
  }
})
$hb = $ps.BeginInvoke()

# ---- UI timer: repaint every second (fast reads only; probe runs in the runspace) ----
$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(1)
$timer.Add_Tick({
  Update-ClaudeQuotaState
  $lt = Read-LogTail
  if($lt -ne $script:lastLogText){ $script:lastLogText=$lt; Set-LogColored $els.LogText $lt; $els.LogScroll.ScrollToEnd() }
  if((Get-Date) -lt $script:flash.until){ Set-StatusLine $script:flash.text }
  else {
    $en=$false; try { $en=[bool](Get-CcuConfig).enabled } catch {}
    $ph='idle'; try { $ph="$((Get-CcuState).phase)" } catch {}
    Set-StatusLine (($(if($en){'● 已布防'}else{'○ 未布防'})) + ' · 引擎: ' + $ph)
  }
  # keep the interval/model chips in sync if changed externally (e.g. from Feishu)
  try { Update-IntervalChip; Update-ChatModelChip; Update-ProviderState } catch {}
})
$timer.Start()
$win.Add_Closed({ try { $timer.Stop() } catch {}; try { if($script:logWin){ $script:logWin.Close() } } catch {}; try { $ps.Stop(); $rs.Close() } catch {}; try { if($script:instanceOwned){ $script:instanceMutex.ReleaseMutex() } } catch {} })
if($SelfTest -or $AISettingsRenderTo){
  $testState=$script:selfTestState
  $tt=New-Object Windows.Threading.DispatcherTimer; $tt.Interval=[TimeSpan]::FromMilliseconds(250)
  $tt.Add_Tick({
    $tt.Stop()
    try {
      $badClaude=Get-ProviderPresentation 'claude' 'unavailable' 'auth' $true
      if($badClaude.text -ne '未登录' -or $badClaude.color -ne 'Danger'){ throw 'Claude 认证失败状态未映射为红色「未登录」' }
      $goodOpenAI=Get-ProviderPresentation 'openai' 'available' 'ok' $true 'direct'
      if($goodOpenAI.text -ne '直连可用' -or $goodOpenAI.color -ne 'Green'){ throw 'OpenAI 直连成功状态未映射为绿色「直连可用」' }
      $goodProxy=Get-ProviderPresentation 'deepseek' 'available' 'ok' $true 'proxy'
      if($goodProxy.text -ne '代理可用' -or $goodProxy.color -ne 'Green'){ throw 'DeepSeek 代理成功状态未映射为绿色「代理可用」' }
      $sync.ready=$false; $sync.claudeReason='limited'; $sync.limited=$true; $sync.fhReset=[DateTimeOffset]::UtcNow.AddMinutes(30); $sync.probedAt=Get-Date
      Update-ClaudeQuotaState
      if($els.ResetText.Text -notlike '5h 限流*'){ throw 'Claude 限流状态丢失精确重置倒计时' }
      $sync.ready=$false; $sync.claudeStatus='unavailable'; $sync.claudeReason='auth'; $sync.probedAt=Get-Date
      Update-ProviderState; Update-ClaudeQuotaState
      if($els.ClaudeStateText.Text -ne '未登录' -or $els.ResetText.Text -ne 'Claude 未登录'){ throw 'GUI 未使用 Claude 实探失败结果' }
      Assert-ChipContentFits $els.ChatModelChip '当前 AI'; Assert-ChipContentFits $els.ResetChip '额度实探'; Assert-ChipContentFits $els.IntervalChip '检查间隔'
      $els.ChatModelChip.RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Button]::ClickEvent)))
    } catch { $testState.failed=$true; Write-GuiError 'main chip self-test' $_ }
    finally { $win.Close() }
  }.GetNewClosure()); $tt.Start()
}
elseif($SessionSelfTest){ $tt=New-Object Windows.Threading.DispatcherTimer; $tt.Interval=[TimeSpan]::FromMilliseconds(250); $tt.Add_Tick({ $tt.Stop(); Show-SessionManagerWindow; $win.Close() }); $tt.Start() }
[void]$win.ShowDialog()
if(($SelfTest -or $AISettingsRenderTo) -and ($script:selfTestState.failed -or -not $script:selfTestState.opened -or -not $script:selfTestState.validated)){
  [Console]::Error.WriteLine("GUI self-test failed: error=$($script:selfTestState.failed) opened=$($script:selfTestState.opened) validated=$($script:selfTestState.validated)")
  exit 1
}
