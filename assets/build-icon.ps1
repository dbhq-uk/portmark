# Renders the Portmark icon to PNG at several sizes and packs them into a Windows .ico.
#
# assets/icon.svg is the design source. This script reproduces the same geometry with GDI+ because
# no SVG rasteriser is installed on the build machine. Keep the two in step by eye.
#
#   pwsh -File assets/build-icon.ps1
#
# The design is a USB-C receptacle with a signal trace knocked out of it. A trace rather than a
# tick, because the product measures rather than approves: it tells you what a link is actually
# doing, including when that is worse than you expected.
#
# The trace uses fewer, thicker segments below 48px. The full six-point waveform is legible at 256
# and becomes an unreadable smudge in a system tray, and an icon that cannot be read at 16px is
# not an icon.

Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot 'icons'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$background = [System.Drawing.Color]::FromArgb(255, 11, 18, 32)
$tealTop    = [System.Drawing.Color]::FromArgb(255, 45, 212, 191)
$tealBottom = [System.Drawing.Color]::FromArgb(255, 14, 116, 144)

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

    $tile = New-RoundedPath 0 0 $size $size (56 * $s)
    $tileBrush = New-Object System.Drawing.SolidBrush($background)
    $g.FillPath($tileBrush, $tile)

    # The receptacle, filled so the trace can be knocked out of it. A knockout survives small sizes
    # where an outline plus a separate mark collapses into noise.
    $port = New-RoundedPath (32 * $s) (88 * $s) (192 * $s) (80 * $s) (40 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, (88 * $s))),
        (New-Object System.Drawing.PointF(0, (168 * $s))),
        $tealTop, $tealBottom)
    $g.FillPath($grad, $port)

    # The trace must be a typed array. An untyped @() binds the Point[] overload of DrawLines,
    # which throws while the surrounding Save still succeeds, producing icons with nothing drawn on
    # them and reporting success. That shipped once; hence the comment.
    if ($size -ge 48) {
        $pen = New-Object System.Drawing.Pen($background, (16 * $s))
        [System.Drawing.PointF[]]$trace = @(
            [System.Drawing.PointF]::new([float](56 * $s),  [float](128 * $s)),
            [System.Drawing.PointF]::new([float](92 * $s),  [float](128 * $s)),
            [System.Drawing.PointF]::new([float](112 * $s), [float](100 * $s)),
            [System.Drawing.PointF]::new([float](140 * $s), [float](156 * $s)),
            [System.Drawing.PointF]::new([float](162 * $s), [float](128 * $s)),
            [System.Drawing.PointF]::new([float](200 * $s), [float](128 * $s)))
    } else {
        # More amplitude and a heavier stroke: at tray size the eye needs the peaks to clear the
        # port's edges, or the whole thing reads as a filled pill.
        $pen = New-Object System.Drawing.Pen($background, (28 * $s))
        [System.Drawing.PointF[]]$trace = @(
            [System.Drawing.PointF]::new([float](58 * $s),  [float](128 * $s)),
            [System.Drawing.PointF]::new([float](102 * $s), [float](92 * $s)),
            [System.Drawing.PointF]::new([float](154 * $s), [float](164 * $s)),
            [System.Drawing.PointF]::new([float](198 * $s), [float](128 * $s)))
    }

    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($pen, $trace)

    $pen.Dispose(); $grad.Dispose(); $port.Dispose(); $tileBrush.Dispose(); $tile.Dispose(); $g.Dispose()
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

# Pack the PNGs into one .ico. Windows Vista and later accept PNG-compressed icon entries, which
# keeps the file small and avoids hand-rolling BMP masks.
$icoPath = Join-Path $outDir 'portmark.ico'
$stream = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
$blobs = @()

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bytes = [System.IO.File]::ReadAllBytes($pngPaths[$i])
    $blobs += ,$bytes
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }

    $writer.Write([byte]$dim)
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $bytes.Length
}

foreach ($blob in $blobs) { $writer.Write($blob) }

$writer.Flush(); $writer.Close(); $stream.Close()
Write-Output "wrote $icoPath"
