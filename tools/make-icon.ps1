# 生成 src\ezViewer\Assets\ezViewer.ico（需要 Windows PowerShell 5.1：powershell.exe -File tools\make-icon.ps1）
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "..\src\ezViewer\Assets\ezViewer.ico"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(0.5, $s * 0.03)
    $bg = New-RoundedRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 0x7C, 0xA6, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x3A, 0x63, 0xE8))
    $g.FillPath($grad, $bg)

    # 摊开的漫画书：左右两页，每页画几格分镜
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $panel = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x9F, 0xBC, 0xFF))
    $left = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.17), ($s * 0.27)),
        (New-Object System.Drawing.PointF ($s * 0.485), ($s * 0.33)),
        (New-Object System.Drawing.PointF ($s * 0.485), ($s * 0.80)),
        (New-Object System.Drawing.PointF ($s * 0.17), ($s * 0.74)))
    $right = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.515), ($s * 0.33)),
        (New-Object System.Drawing.PointF ($s * 0.83), ($s * 0.27)),
        (New-Object System.Drawing.PointF ($s * 0.83), ($s * 0.74)),
        (New-Object System.Drawing.PointF ($s * 0.515), ($s * 0.80)))
    $g.FillPolygon($white, $left)
    $g.FillPolygon($white, $right)

    if ($s -ge 24) {
        $g.FillRectangle($panel, $s * 0.22, $s * 0.36, $s * 0.22, $s * 0.15)
        $g.FillRectangle($panel, $s * 0.22, $s * 0.545, $s * 0.09, $s * 0.15)
        $g.FillRectangle($panel, $s * 0.335, $s * 0.545, $s * 0.105, $s * 0.17)
        $g.FillRectangle($panel, $s * 0.56, $s * 0.36, $s * 0.10, $s * 0.32)
        $g.FillRectangle($panel, $s * 0.685, $s * 0.345, $s * 0.10, $s * 0.15)
        $g.FillRectangle($panel, $s * 0.685, $s * 0.525, $s * 0.10, $s * 0.16)
    }

    $g.Dispose()
    return $bmp
}

# 大尺寸用 PNG 压缩
function New-IconPng([int]$s) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# 小尺寸用传统 DIB（32 位 BGRA + AND 掩码），兼容所有读取图标的接口
function New-IconDib([int]$s) {
    $bmp = New-IconBitmap $s
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()

    $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]($s * 4 * $s + $maskStride * $s))
    $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $s * 4) }
    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $s; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) { $row[$x -shr 3] = [byte]($row[$x -shr 3] -bor (0x80 -shr ($x -band 7))) }
        }
        $bw.Write($row)
    }
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @($sizes | ForEach-Object { if ($_ -le 64) { , (New-IconDib $_) } else { , (New-IconPng $_) } })

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"icon written: $out"
