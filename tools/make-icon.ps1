# AI Resume 图标生成器(可重复运行,产出多尺寸 ICO)。
#
# 立意与控制面一致:一台机架式仪表。图标画的就是应用里那块 CRT 额度表——
# 骨白面板嵌一块深色屏,屏里一排磷光绿光柱,最后一格朱橙(表示"快烧完了")。
# **不画字母、不画渐变**:16px 下字母糊成一团,渐变糊成脏边;硬边色块才读得出。
#
# 用法:pwsh -File tools/make-icon.ps1 [-Out <path>]
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\csharp\src\AiResume.Gui\icon.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 与 index.html 的 :root 同一套色值,图标与界面不能是两种审美。
$panel     = [System.Drawing.Color]::FromArgb(255, 234, 230, 216)  # --panel-hi
$seam      = [System.Drawing.Color]::FromArgb(255, 180, 173, 152)  # --seam
$crt       = [System.Drawing.Color]::FromArgb(255,  12,  19,  16)  # --crt
$phos      = [System.Drawing.Color]::FromArgb(255,  70, 232, 136)  # --phos
$phosDim   = [System.Drawing.Color]::FromArgb(255,  31, 138,  78)  # --phos-dim
$vermilion = [System.Drawing.Color]::FromArgb(255, 226,  81,  43)  # --vermilion

function New-IconBitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.Clear([System.Drawing.Color]::Transparent)

    # 面板:整块底 + 一圈接缝线。小尺寸下留白会把图标缩得更小,所以铺满。
    $g.FillRectangle((New-Object System.Drawing.SolidBrush($panel)), 0, 0, $S, $S)
    $penSeam = New-Object System.Drawing.Pen($seam, [Math]::Max(1, [int]($S / 32)))
    $g.DrawRectangle($penSeam, 0, 0, $S - 1, $S - 1)

    # 小尺寸退化:任务栏/托盘的 16-20px 下,边距和底部色带会把屏体挤成一条缝。
    # 那个尺寸只需要读出「深色屏 + 绿光柱」这一个意思,其余装饰全部让路。
    $tiny = $S -le 24

    # CRT 屏:居中偏上,占面板约 72% 宽(小尺寸下几乎铺满)。
    $m  = if ($tiny) { 1 } else { [Math]::Max(2, [int]($S * 0.14)) }
    $sx = $m
    $sy = if ($tiny) { 2 } else { [Math]::Max(2, [int]($S * 0.20)) }
    $sw = $S - 2 * $m
    $sh = if ($tiny) { $S - $sy - 2 } else { [int]($S * 0.50) }
    $g.FillRectangle((New-Object System.Drawing.SolidBrush($crt)), $sx, $sy, $sw, $sh)

    # 光柱:格数随尺寸退化——再多就糊成一条线。
    $cells = if ($tiny) { 3 } elseif ($S -le 40) { 5 } elseif ($S -le 64) { 7 } else { 10 }
    $gap   = [Math]::Max(1, [int]($S / 48))
    $bx    = $sx + [Math]::Max(1, [int]($sw * 0.10))
    $bw    = $sw - 2 * [Math]::Max(1, [int]($sw * 0.10))
    $cellW = [Math]::Max(1, [int](($bw - ($cells - 1) * $gap) / $cells))
    $barH  = [Math]::Max(2, [int]($sh * 0.42))
    $by    = $sy + $sh - [Math]::Max(2, [int]($sh * 0.22)) - $barH

    for ($i = 0; $i -lt $cells; $i++) {
        # 最后一格朱橙:一眼读出"燃料快见底"。倒数第二格用暗绿做过渡。
        $c = if ($i -eq $cells - 1) { $vermilion } elseif ($i -eq $cells - 2) { $phosDim } else { $phos }
        $x = $bx + $i * ($cellW + $gap)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush($c)), $x, $by, $cellW, $barH)
    }

    # 面板下缘一条朱橙细带:铭牌感,同时在纯白背景上给图标一个落脚点。
    # 小尺寸不画——它占掉的那 1-2px 正是屏体最需要的。
    if (-not $tiny) {
        $stripeH = [Math]::Max(1, [int]($S * 0.055))
        $g.FillRectangle((New-Object System.Drawing.SolidBrush($vermilion)),
            $m, $S - $m - $stripeH, $S - 2 * $m, $stripeH)
    }

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

# ICO 封装。Vista 起所有尺寸都支持 PNG 负载,比 BMP+掩码简单且体积小。
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    # 宽高字段是 1 字节,256 用 0 表示。
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$p.Bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Flush()

$resolved = [System.IO.Path]::GetFullPath($Out)
[System.IO.File]::WriteAllBytes($resolved, $fs.ToArray())
$bw.Dispose(); $fs.Dispose()

Write-Output "已生成 $resolved ($([Math]::Round((Get-Item $resolved).Length / 1KB, 1)) KB, $($pngs.Count) 个尺寸)"

# 同步到其余需要图标的项目,避免三份各自漂移。
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
foreach ($rel in @('csharp\src\AiResume.Worker\icon.ico', 'src\icon.ico')) {
    $dst = Join-Path $repo $rel
    if (Test-Path (Split-Path $dst)) {
        Copy-Item $resolved $dst -Force
        Write-Output "已同步 $rel"
    }
}
