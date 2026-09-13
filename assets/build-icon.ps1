# Renders the Portmark icon to PNG at several sizes and packs them into a Windows .ico.
#
# The SVG is the source of truth for the design; this script reproduces the same geometry with
# GDI+ because no SVG rasteriser is installed. Keep the two in step by eye if the design changes.
#
#   pwsh -File assets/build-icon.ps1

Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot 'icons'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$background = [System.Drawing.Color]::FromArgb(255, 11, 18, 32)
$tealTop    = [System.Drawing.Color]::FromArgb(255, 45, 212, 191)
$tealBottom = [System.Drawing.Color]::FromArgb(255, 14, 116, 144)
$markColour = [System.Drawing.Color]::FromArgb(255, 248, 250, 252)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Render-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0

    # Rounded background tile.
    $bgPath = New-RoundedPath 0 0 $size $size (56 * $s)
    $bgBrush = New-Object System.Drawing.SolidBrush($background)
    $g.FillPath($bgBrush, $bgPath)

    # The USB-C receptacle as a solid stadium, with the tick knocked out of it. A knockout reads
    # far better at 16px than an outline plus a separate mark, which turns to noise.
    $portPath = New-RoundedPath (32 * $s) (88 * $s) (192 * $s) (80 * $s) (40 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, (88 * $s))),
        (New-Object System.Drawing.PointF(0, (168 * $s))),
        $tealTop, $tealBottom)
    $g.FillPath($grad, $portPath)
    $grad.Dispose(); $portPath.Dispose()

    # The mark is cut out in the background colour, so it is the port itself that carries it.
    $markPen = New-Object System.Drawing.Pen($background, (20 * $s))
    $markPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $markPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $markPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # The array must be typed. An untyped @() makes PowerShell bind the Point[] overload of
    # DrawLines, which throws and leaves an icon with no tick on it at all.
    if ($size -lt 32) { $markPen.Width = 26 * $s }
    [System.Drawing.PointF[]]$tick = @(
        [System.Drawing.PointF]::new([float](78 * $s), [float](128 * $s)),
        [System.Drawing.PointF]::new([float](108 * $s), [float](150 * $s)),
        [System.Drawing.PointF]::new([float](178 * $s), [float](106 * $s)))
    $g.DrawLines($markPen, $tick)

    $markPen.Dispose(); $bgBrush.Dispose(); $bgPath.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = @(16, 32, 48, 64, 128, 256)
$pngPaths = @()

foreach ($size in $sizes) {
    $bmp = Render-Icon $size
    $path = Join-Path $outDir "portmark-$size.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngPaths += $path
    Write-Output "wrote $path"
}

# Pack the PNGs into a single .ico. Windows Vista and later accept PNG-compressed icon entries,
# which keeps the file small and avoids hand-rolling BMP masks.
$icoPath = Join-Path $outDir 'portmark.ico'
$stream = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([uint16]0)                  # reserved
$writer.Write([uint16]1)                  # type: icon
$writer.Write([uint16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
$blobs = @()

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bytes = [System.IO.File]::ReadAllBytes($pngPaths[$i])
    $blobs += ,$bytes
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }

    $writer.Write([byte]$dim)             # width, 0 means 256
    $writer.Write([byte]$dim)             # height
    $writer.Write([byte]0)                # palette size
    $writer.Write([byte]0)                # reserved
    $writer.Write([uint16]1)              # colour planes
    $writer.Write([uint16]32)             # bits per pixel
    $writer.Write([uint32]$bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $bytes.Length
}

foreach ($blob in $blobs) { $writer.Write($blob) }

$writer.Flush(); $writer.Close(); $stream.Close()
Write-Output "wrote $icoPath"
