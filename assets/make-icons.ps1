# Draws the logo (a green keycap with an A and a check badge) and writes logo.png,
# icon.ico and social-preview.png into this folder.
# Run: powershell -File assets\make-icons.ps1
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'logo-lib.ps1')

function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Logo $g 0 0 $size
    $g.Dispose()
    return $bmp
}

function Png-Bytes($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return $ms.ToArray() }

(New-LogoBitmap 1024).Save((Join-Path $here 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object System.Collections.ArrayList
foreach ($size in $sizes) { [void]$images.Add([byte[]](Png-Bytes (New-LogoBitmap $size))) }
$ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]; $b = if ($size -ge 256) { 0 } else { $size }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[System.IO.File]::WriteAllBytes((Join-Path $here 'icon.ico'), $ms.ToArray())

# Social preview, 1280x640
$bmp = New-Object System.Drawing.Bitmap 1280, 640
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::FromArgb(24,26,30))
Draw-Logo $g 130 160 320
$f1 = New-Object System.Drawing.Font 'Segoe UI Semibold', 64, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$f2 = New-Object System.Drawing.Font 'Segoe UI', 32, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('Keyboard Writer Helper', $f1, [System.Drawing.Brushes]::White, 500, 230)
$g.DrawString('Fixes spelling in any Windows program as you type', $f2, [System.Drawing.Brushes]::LightGray, 504, 330)
$bmp.Save((Join-Path $here 'social-preview.png'))
