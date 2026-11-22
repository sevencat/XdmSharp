#Requires -Version 7.0
<#
.SYNOPSIS
    由仓库根 logo.png 生成程序用图标资源（透明底）。

.DESCRIPTION
    产物：
      src/Xdm.Ui/Assets/xdm-logo.png   256×256，透明底，裁到内容外围，窗口 Icon + 标题栏 / 关于页
      src/Xdm.App/Assets/xdm.ico       16/20/24/32/40/48/64/96/128/256 十帧 PNG 压缩图标（exe / 任务栏）

    处理方式：
      1. 从四边洪泛标记「与白色连通」的背景（原图底色是 240~255 的渐变，不能用固定阈值一刀切）；
      2. 背景全透明；箭头本体（min 通道 ≤ 200）保留原配色；
      3. 中间那圈浅色辉光按「白色混入」给半透明并反合成剥离白分量——否则放到深色标题栏上会浮出白雾；
      4. alpha 做一遍 3×3 均值消掉洪泛边界的碎齿，再按内容包围盒裁成正方形缩到目标尺寸。

    标题栏底色是深色（#1F2B3B），所以 logo 必须是透明底；不要再用白底圆角牌。

.EXAMPLE
    pwsh -NoProfile -File tools/make-icons.ps1
#>
param(
    [string] $Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'logo.png'),
    [string] $LogoOut = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Xdm.Ui/Assets/xdm-logo.png'),
    [string] $IconOut = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Xdm.App/Assets/xdm.ico'),
    [int] $LogoSize = 256
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-TransparentLogo {
    param(
        [System.Drawing.Image] $Image,
        [int] $Size
    )

    $width = $Image.Width
    $height = $Image.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $width, $height)
    $source = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($source)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.DrawImage($Image, 0, 0, $width, $height)
    }
    finally {
        $graphics.Dispose()
    }

    try {
        $data = $source.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stride = [math]::Abs($data.Stride)
        $length = $stride * $height
        $buf = [byte[]]::new($length)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $length)
        $source.UnlockBits($data)

        # 1) 从四边洪泛：min 通道 ≥ 236 视为「背景色」（原图白底带 240~255 渐变）
        $isBackground = [bool[]]::new($width * $height)
        $stack = [System.Collections.Generic.Stack[int]]::new()
        for ($x = 0; $x -lt $width; $x++) {
            $stack.Push($x)
            $stack.Push(($height - 1) * $width + $x)
        }
        for ($y = 0; $y -lt $height; $y++) {
            $stack.Push($y * $width)
            $stack.Push($y * $width + $width - 1)
        }
        while ($stack.Count -gt 0) {
            $index = $stack.Pop()
            if ($isBackground[$index]) { continue }
            $x = $index % $width
            $y = [int]($index / $width)
            $offset = $y * $stride + $x * 4
            $min = [math]::Min([math]::Min($buf[$offset], $buf[$offset + 1]), $buf[$offset + 2])
            if ($min -lt 236) { continue }
            $isBackground[$index] = $true
            if ($x -gt 0) { $stack.Push($index - 1) }
            if ($x -lt $width - 1) { $stack.Push($index + 1) }
            if ($y -gt 0) { $stack.Push($index - $width) }
            if ($y -lt $height - 1) { $stack.Push($index + $width) }
        }

        # 2) 逐像素定 alpha：背景 0、本体原色不透明、浅色辉光半透明且去白
        $minX = $width; $minY = $height; $maxX = -1; $maxY = -1
        for ($y = 0; $y -lt $height; $y++) {
            for ($x = 0; $x -lt $width; $x++) {
                $offset = $y * $stride + $x * 4
                $index = $y * $width + $x
                if ($isBackground[$index]) {
                    $buf[$offset + 3] = 0
                    continue
                }

                $min = [math]::Min([math]::Min($buf[$offset], $buf[$offset + 1]), $buf[$offset + 2])
                if ($min -le 200) {
                    $buf[$offset + 3] = 255
                }
                else {
                    $white = $min / 255.0
                    $alpha = [int]([math]::Round((255 - $min) * 3.0))
                    $alpha = [math]::Max(0, [math]::Min(255, $alpha))
                    if ($alpha -gt 0 -and $white -lt 0.97) {
                        for ($channel = 0; $channel -lt 3; $channel++) {
                            # 反合成：C = α·F + (1-α)·255 → F = (C - (1-α)·255) / α
                            $value = ($buf[$offset + $channel] - $white * 255) / (1 - $white)
                            $buf[$offset + $channel] = [byte][math]::Max(0, [math]::Min(255, [math]::Round($value)))
                        }
                    }
                    $buf[$offset + 3] = [byte]$alpha
                }

                if ($buf[$offset + 3] -gt 12) {
                    if ($x -lt $minX) { $minX = $x }
                    if ($x -gt $maxX) { $maxX = $x }
                    if ($y -lt $minY) { $minY = $y }
                    if ($y -gt $maxY) { $maxY = $y }
                }
            }
        }

        # 3) alpha 3×3 均值：洪泛边界切换太硬，小图上会有碎齿
        $alphaCopy = [byte[]]::new($width * $height)
        for ($y = 0; $y -lt $height; $y++) {
            for ($x = 0; $x -lt $width; $x++) {
                $alphaCopy[$y * $width + $x] = $buf[$y * $stride + $x * 4 + 3]
            }
        }
        for ($y = 0; $y -lt $height; $y++) {
            for ($x = 0; $x -lt $width; $x++) {
                $sum = 0
                $count = 0
                for ($dy = -1; $dy -le 1; $dy++) {
                    $neighborY = $y + $dy
                    if ($neighborY -lt 0 -or $neighborY -ge $height) { continue }
                    for ($dx = -1; $dx -le 1; $dx++) {
                        $neighborX = $x + $dx
                        if ($neighborX -lt 0 -or $neighborX -ge $width) { continue }
                        $sum += $alphaCopy[$neighborY * $width + $neighborX]
                        $count++
                    }
                }
                $buf[$y * $stride + $x * 4 + 3] = [byte][math]::Round($sum / $count)
            }
        }

        $masked = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $write = $masked.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        [System.Runtime.InteropServices.Marshal]::Copy($buf, 0, $write.Scan0, $length)
        $masked.UnlockBits($write)
    }
    finally {
        $source.Dispose()
    }

    try {
        if ($maxX -lt 0) { throw 'logo 全是背景色，没找到图案' }

        # 内容包围盒中心，边长多留 6% 呼吸空间，避免箭头贴边
        $side = [int][math]::Round([math]::Max($maxX - $minX + 1, $maxY - $minY + 1) * 1.06)
        $left = [int][math]::Round(($minX + $maxX + 1) / 2.0 - $side / 2)
        $top = [int][math]::Round(($minY + $maxY + 1) / 2.0 - $side / 2)

        $result = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $output = [System.Drawing.Graphics]::FromImage($result)
        try {
            $output.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $output.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $output.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $output.Clear([System.Drawing.Color]::Transparent)
            $output.DrawImage(
                $masked,
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Rectangle]::new($left, $top, $side, $side),
                [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $output.Dispose()
        }

        return $result
    }
    finally {
        $masked.Dispose()
    }
}

function Resize-Transparent {
    param(
        [System.Drawing.Image] $Image,
        [int] $Size
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($Image, 0, 0, $Size, $Size)
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap] $Bitmap)

    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        # 逗号包一层：否则 byte[] 会被 PowerShell 拆成 object[]，后面 BinaryWriter 写不出正确的二进制
        return , [byte[]]$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

# ico 里放哪些尺寸：小尺寸给列表/标题栏，大尺寸给资源管理器大图标与任务栏
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

foreach ($target in @($LogoOut, $IconOut)) {
    $dir = Split-Path -Parent $target
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

$image = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
try {
    if ($image.Width -ne $image.Height) {
        throw "logo 必须是正方形（当前 $($image.Width)×$($image.Height)）"
    }

    $master = New-TransparentLogo -Image $image -Size $LogoSize
    try {
        $master.Save($LogoOut, [System.Drawing.Imaging.ImageFormat]::Png)
        "logo  -> $LogoOut ($($master.Width)x$($master.Height), 透明底)"
    }
    finally {
        # ico 帧还要用 master，延后释放
    }

    # 手工拼 ico：ICONDIR + 每帧 ICONDIRENTRY + PNG 负载（Vista 起支持 PNG 压缩帧）
    $frames = foreach ($size in $iconSizes) {
        $frameBitmap = Resize-Transparent -Image $master -Size $size
        try {
            [pscustomobject]@{ Size = $size; Bytes = [byte[]](ConvertTo-PngBytes -Bitmap $frameBitmap) }
        }
        finally {
            $frameBitmap.Dispose()
        }
    }
    $master.Dispose()

    $file = [System.IO.File]::Create($IconOut)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)                 # reserved
        $writer.Write([uint16]1)                 # type: icon
        $writer.Write([uint16]$frames.Count)     # 帧数

        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }   # 256 在目录项里记 0
            $writer.Write([byte]$dim)            # width
            $writer.Write([byte]$dim)            # height
            $writer.Write([byte]0)               # 调色板色数
            $writer.Write([byte]0)               # reserved
            $writer.Write([uint16]1)             # planes
            $writer.Write([uint16]32)            # bpp
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }

        foreach ($frame in $frames) {
            $writer.Write([byte[]]$frame.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }

    "icon  -> $IconOut ($($frames.Count) 帧: $($iconSizes -join '/'))"
}
finally {
    $image.Dispose()
}
