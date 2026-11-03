# Draws the logo (green rounded square with "Aa") and writes logo.png, icon.ico and
# social-preview.png into this folder. Run: powershell -File assets\make-icons.ps1
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [int]($size * 0.22); $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0,0)), ([System.Drawing.Point]::new($size,$size)), ([System.Drawing.Color]::FromArgb(34,197,94)), ([System.Drawing.Color]::FromArgb(21,128,61))
    $g.FillPath($brush, $path)
    $font = New-Object System.Drawing.Font 'Segoe UI', ($size * 0.42), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString('Aa', $font, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new(0, [single]($size * 0.02), $size, $size), $fmt)
    # a small check mark: the correction
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single]($size * 0.05)); $pen.StartCap='Round'; $pen.EndCap='Round'; $pen.LineJoin='Round'
    $g.DrawLines($pen, @([System.Drawing.PointF]::new($size*0.30,$size*0.80), [System.Drawing.PointF]::new($size*0.42,$size*0.88), [System.Drawing.PointF]::new($size*0.70,$size*0.74)))
    $g.Dispose(); return $bmp
}

function Png-Bytes($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return $ms.ToArray() }

(New-Logo 512).Save((Join-Path $here 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = New-Object System.Collections.ArrayList
foreach ($s in $sizes) { [void]$images.Add([byte[]](Png-Bytes (New-Logo $s))) }
$ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[System.IO.File]::WriteAllBytes((Join-Path $here 'icon.ico'), $ms.ToArray())

# Social preview, 1280x640
$bmp = New-Object System.Drawing.Bitmap 1280, 640; $g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::FromArgb(24,26,30))
$g.DrawImage((New-Logo 320), 140, 160, 320, 320)
$f1 = New-Object System.Drawing.Font 'Segoe UI', 64, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$f2 = New-Object System.Drawing.Font 'Segoe UI', 32, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('Keyboard Writer Helper', $f1, [System.Drawing.Brushes]::White, 500, 230)
$g.DrawString('Fixes spelling in any Windows program as you type', $f2, [System.Drawing.Brushes]::LightGray, 504, 330)
$bmp.Save((Join-Path $here 'social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
