# Makes the files the installer shows, from the repository itself:
#   build\wizard-large-*.bmp   the picture on the welcome and finish pages
#   build\wizard-small-*.bmp   the small picture at the top of the other pages
#   build\changelog.txt        CHANGELOG.md as plain text
#   build\credits.txt          the credits (THIRD_PARTY_NOTICES.md) as plain text
# Run by build-installer.cmd. The output folder is not committed.
param([string]$Version = "")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$out = Join-Path $here "build"
New-Item -ItemType Directory -Force -Path $out | Out-Null
if (-not $Version) { $Version = (Get-Content (Join-Path $root "version.txt") -Raw).Trim() }

# ---------- pictures ----------

function New-Gradient([System.Drawing.Rectangle]$rect, $from, $to, [single]$angle) {
    return New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $from, $to, $angle
}

function Draw-Logo($g, [int]$x, [int]$y, [int]$size, [bool]$square = $false) {
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = if ($square) { 0 } else { [int]($size * 0.22) }; $d = [Math]::Max(1, $r * 2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90); $path.AddArc($x + $size - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $size - $d, $y + $size - $d, $d, $d, 0, 90); $path.AddArc($x, $y + $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.Rectangle $x, $y, $size, $size
    $brush = New-Gradient $rect ([System.Drawing.Color]::FromArgb(34,197,94)) ([System.Drawing.Color]::FromArgb(21,128,61)) 45
    $g.FillPath($brush, $path)
    $font = New-Object System.Drawing.Font 'Segoe UI Semibold', ($size * 0.40), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString('Aa', $font, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new($x, $y - $size * 0.03, $size, $size * 0.86), $fmt)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single]([Math]::Max(1.5, $size * 0.05)))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    [System.Drawing.PointF[]]$pts = @(
        [System.Drawing.PointF]::new($x + $size * 0.30, $y + $size * 0.80),
        [System.Drawing.PointF]::new($x + $size * 0.42, $y + $size * 0.88),
        [System.Drawing.PointF]::new($x + $size * 0.70, $y + $size * 0.74))
    $g.DrawLines($pen, $pts)
}

# The welcome and finish picture: dark green, rows of faint keys, the logo and the name.
function New-Large([int]$scalePercent) {
    $k = $scalePercent / 100.0
    $w = [int](164 * $k); $h = [int](314 * $k)
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $g.FillRectangle((New-Gradient $rect ([System.Drawing.Color]::FromArgb(5,46,28)) ([System.Drawing.Color]::FromArgb(21,128,61)) 70), $rect)

    # faint keyboard keys across the lower half
    $keyBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(22, 255, 255, 255))
    $keyPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 255, 255, 255)), ([single][Math]::Max(1, $k))
    $key = [int](26 * $k); $gap = [int](5 * $k)
    for ($row = 0; $row -lt 5; $row++) {
        $y = [int](178 * $k) + $row * ($key + $gap)
        $offset = ($row % 2) * [int](($key + $gap) / 2) - [int](12 * $k)
        for ($col = 0; $col -lt 7; $col++) {
            $x = $offset + $col * ($key + $gap)
            $path = New-Object System.Drawing.Drawing2D.GraphicsPath
            $rr = [int](5 * $k); $dd = $rr * 2
            $path.AddArc($x, $y, $dd, $dd, 180, 90); $path.AddArc($x + $key - $dd, $y, $dd, $dd, 270, 90)
            $path.AddArc($x + $key - $dd, $y + $key - $dd, $dd, $dd, 0, 90); $path.AddArc($x, $y + $key - $dd, $dd, $dd, 90, 90)
            $path.CloseFigure()
            $g.FillPath($keyBrush, $path); $g.DrawPath($keyPen, $path)
        }
    }
    # fade the keys out towards the bottom so the text stays readable
    $fade = New-Gradient (New-Object System.Drawing.Rectangle 0, ([int](150 * $k)), $w, ([int](164 * $k))) ([System.Drawing.Color]::FromArgb(0, 5, 46, 28)) ([System.Drawing.Color]::FromArgb(150, 5, 46, 28)) 90
    $g.FillRectangle($fade, 0, [int](150 * $k), $w, $h - [int](150 * $k))

    $size = [int](92 * $k)
    Draw-Logo $g ([int](($w - $size) / 2)) ([int](28 * $k)) $size

    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
    $title = New-Object System.Drawing.Font 'Segoe UI Semibold', (15 * $k), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sub = New-Object System.Drawing.Font 'Segoe UI', (11.5 * $k), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString("Keyboard Writer`nHelper", $title, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new(0, 130 * $k, $w, 50 * $k), $fmt)
    $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(215, 235, 245, 238))
    $g.DrawString("Spelling fixed as you type,`nin every program.", $sub, $soft, [System.Drawing.RectangleF]::new(0, 190 * $k, $w, 40 * $k), $fmt)
    $g.DrawString("Version $Version`nby YATESH ROHIT", $sub, $soft, [System.Drawing.RectangleF]::new(0, 268 * $k, $w, 40 * $k), $fmt)
    $g.Dispose()
    $bmp.Save((Join-Path $out ("wizard-large-$scalePercent.bmp")), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}

# The small picture on the other pages: the logo filling the square.
function New-Small([int]$scalePercent) {
    $size = [int](55 * $scalePercent / 100.0)
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    # full-bleed, so there is no white edge in the dark wizard
    Draw-Logo $g 0 0 $size $true
    $g.Dispose()
    $bmp.Save((Join-Path $out ("wizard-small-$scalePercent.bmp")), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}

New-Large 100; New-Large 200
New-Small 100; New-Small 200

# ---------- text ----------

# The installer pages read plain ASCII text, so typographic characters become plain ones.
function To-Ascii([string]$text) {
    $map = @{ [string][char]0x2014 = '-'; [string][char]0x2013 = '-'; [string][char]0x2026 = '...'; [string][char]0x2192 = '->';
              [string][char]0x21D2 = '=>'; [string][char]0x2019 = "'"; [string][char]0x2018 = "'"; [string][char]0x201C = '"';
              [string][char]0x201D = '"'; [string][char]0x00D7 = 'x'; [string][char]0x00B7 = '-' }
    foreach ($key in $map.Keys) { $text = $text.Replace($key, $map[$key]) }
    return -join ($text.ToCharArray() | ForEach-Object { if ([int]$_ -lt 128) { $_ } else { '?' } })
}

function Wrap([string]$text, [int]$width, [string]$indent) {
    $words = $text -split '\s+' | Where-Object { $_ }
    $lines = @(); $line = ""
    foreach ($word in $words) {
        if (($line + " " + $word).Trim().Length -gt $width -and $line) { $lines += $line; $line = $word }
        else { $line = ($line + " " + $word).Trim() }
    }
    if ($line) { $lines += $line }
    return ($lines | ForEach-Object { $indent + $_ }) -join "`r`n"
}

# CHANGELOG.md -> plain text. Lines wrapped in the markdown are joined first, then wrapped again.
$md = Get-Content (Join-Path $root "CHANGELOG.md") -Raw -Encoding UTF8
$result = New-Object System.Collections.Generic.List[string]
$bullet = $null
function Flush-Bullet {
    if ($script:bullet -ne $null) {
        $wrapped = Wrap $script:bullet 72 "    "
        $result.Add("  - " + $wrapped.Substring(4))
        $script:bullet = $null
    }
}
foreach ($line in ($md -split "`r?`n")) {
    $clean = $line -replace '\*\*', '' -replace '`', ''
    if ($clean -match '^# ') { continue }
    if ($result.Count -eq 0 -and $clean -notmatch '^## ') { continue }
    if ($clean -match '^## (.+)$') {
        Flush-Bullet
        if ($result.Count -gt 0) { $result.Add("") }
        $result.Add("VERSION " + $Matches[1].Trim())
        $result.Add("-" * 40)
        continue
    }
    if ($clean -match '^\s*-\s+(.*)$') { Flush-Bullet; $script:bullet = $Matches[1].Trim(); continue }
    if ($clean -match '^\s{2,}(\S.*)$' -and $script:bullet -ne $null) { $script:bullet += " " + $Matches[1].Trim(); continue }
    Flush-Bullet
    if ($clean.Trim() -ne "") { $result.Add((Wrap $clean.Trim() 76 "")) }
}
Flush-Bullet
[IO.File]::WriteAllText((Join-Path $out "changelog.txt"), (To-Ascii ($result -join "`r`n")), [Text.Encoding]::ASCII)

# THIRD_PARTY_NOTICES.md -> credits
$credits = New-Object System.Collections.Generic.List[string]
$credits.Add("Keyboard Writer Helper")
$credits.Add("Made by YATESH ROHIT")
$credits.Add("")
$credits.Add("Spelling data and word lists come from open projects. Thank you to")
$credits.Add("everyone who built and shared them:")
$credits.Add("")
foreach ($line in ((Get-Content (Join-Path $root "THIRD_PARTY_NOTICES.md") -Raw -Encoding UTF8) -split "`r?`n")) {
    if ($line -notmatch '^\|\s*\*\*') { continue }
    $cells = ($line.Trim().Trim('|') -split '\|') | ForEach-Object { ($_ -replace '\*\*', '' -replace '\*([^*]+)\*', '$1' -replace '`', '' -replace '<(https?://[^>]+)>', '$1').Trim() }
    if ($cells.Count -lt 4) { continue }
    $credits.Add("* " + $cells[0])
    $credits.Add((Wrap ("Credit: " + $cells[2]) 74 "    "))
    $credits.Add((Wrap ("Licence: " + $cells[3]) 74 "    "))
    $credits.Add("")
}
$credits.Add("The full list is in THIRD_PARTY_NOTICES.md, installed next to the program.")
[IO.File]::WriteAllText((Join-Path $out "credits.txt"), (To-Ascii ($credits -join "`r`n")), [Text.Encoding]::ASCII)

Write-Host "Installer files written to $out"
