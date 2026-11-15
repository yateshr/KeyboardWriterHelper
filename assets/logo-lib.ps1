# The logo, drawn from shapes (no font), so it is sharp at every size:
# a green keycap with a white letter A whose crossbar is a check mark.
# Dot-source this file, then call Draw-Logo. Used by make-icons.ps1 and installer\prepare.ps1.
Add-Type -AssemblyName System.Drawing

function New-RoundPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = [Math]::Max(1, $r * 2)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Draws the logo into the square (x, y, size, size) of graphics $g, supersampled 4x.
# $flat = a plain green tile without the keycap depth (for very small sizes).
function Draw-Logo($g, [int]$x, [int]$y, [int]$size, [bool]$square = $false) {
    $ss = 4
    $n = $size * $ss
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $t = [System.Drawing.Graphics]::FromImage($bmp)
    $t.SmoothingMode = 'AntiAlias'; $t.PixelOffsetMode = 'HighQuality'
    $t.Clear([System.Drawing.Color]::Transparent)

    # base of the key
    $baseR = if ($square) { 0 } else { $n * 0.225 }
    $base = New-RoundPath 0 0 $n $n $baseR
    $baseBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, $n)), ([System.Drawing.Color]::FromArgb(22, 120, 62)), ([System.Drawing.Color]::FromArgb(6, 70, 38))
    $t.FillPath($baseBrush, $base)

    # top face, lighter, with a thin highlight along its upper edge
    $fx = $n * 0.065; $fy = $n * 0.045; $fw = $n * 0.87; $fh = $n * 0.80
    $face = New-RoundPath $fx $fy $fw $fh ($n * 0.17)
    $faceBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, $fy)), ([System.Drawing.PointF]::new(0, $fy + $fh)), ([System.Drawing.Color]::FromArgb(84, 222, 134)), ([System.Drawing.Color]::FromArgb(26, 166, 80))
    $t.FillPath($faceBrush, $face)
    $hi = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 255, 255, 255)), ([single]($n * 0.012))
    $t.DrawPath($hi, $face)

    # the letter A with a normal crossbar (a soft shadow first), and a check badge on the corner
    foreach ($pass in 0, 1) {
        $dy = if ($pass -eq 0) { $n * 0.016 } else { 0 }
        $col = if ($pass -eq 0) { [System.Drawing.Color]::FromArgb(70, 4, 50, 24) } else { [System.Drawing.Color]::White }
        $pen = New-Object System.Drawing.Pen $col, ([single]($n * 0.088))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
        [System.Drawing.PointF[]]$a = @(
            [System.Drawing.PointF]::new($n * 0.255, $n * 0.690 + $dy),
            [System.Drawing.PointF]::new($n * 0.455, $n * 0.215 + $dy),
            [System.Drawing.PointF]::new($n * 0.655, $n * 0.690 + $dy))
        $t.DrawLines($pen, $a)
        $bar = New-Object System.Drawing.Pen $col, ([single]($n * 0.072))
        $bar.StartCap = 'Round'; $bar.EndCap = 'Round'
        $t.DrawLine($bar, [single]($n * 0.352), [single]($n * 0.535 + $dy), [single]($n * 0.558), [single]($n * 0.535 + $dy))
    }
    # the badge: dark green disc, white ring, white check
    $bx = $n * 0.735; $by = $n * 0.715; $br = $n * 0.185
    $t.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 4, 50, 24))), [single]($bx - $br), [single]($by - $br + $n * 0.016), [single](2 * $br), [single](2 * $br))
    $t.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(9, 92, 50))), [single]($bx - $br), [single]($by - $br), [single](2 * $br), [single](2 * $br))
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single]($n * 0.026))
    $t.DrawEllipse($ring, [single]($bx - $br), [single]($by - $br), [single](2 * $br), [single](2 * $br))
    $ck = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single]($n * 0.056))
    $ck.StartCap = 'Round'; $ck.EndCap = 'Round'; $ck.LineJoin = 'Round'
    [System.Drawing.PointF[]]$c = @(
        [System.Drawing.PointF]::new($bx - $br * 0.50, $by + $br * 0.02),
        [System.Drawing.PointF]::new($bx - $br * 0.12, $by + $br * 0.42),
        [System.Drawing.PointF]::new($bx + $br * 0.55, $by - $br * 0.38))
    $t.DrawLines($ck, $c)
    $t.Dispose()

    $old = $g.InterpolationMode; $oldPx = $g.PixelOffsetMode
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($bmp, [System.Drawing.Rectangle]::new($x, $y, $size, $size))
    $g.InterpolationMode = $old; $g.PixelOffsetMode = $oldPx
    $bmp.Dispose()
}
