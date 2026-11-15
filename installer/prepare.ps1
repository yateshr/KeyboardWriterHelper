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

. (Join-Path $root 'assets\logo-lib.ps1')

# The welcome and finish picture: dark green, rows of soft keycaps, the logo and the name.
# Drawn three times larger and scaled down, so edges and text are smooth at every size.
function New-Large([int]$scalePercent) {
    $k = $scalePercent / 100.0
    $w = [int][Math]::Round(164 * $k); $h = [int][Math]::Round(314 * $k)
    $ss = 3
    $kk = $k * $ss
    $BW = $w * $ss; $BH = $h * $ss
    $big = New-Object System.Drawing.Bitmap $BW, $BH, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $rect = New-Object System.Drawing.Rectangle 0, 0, $BW, $BH
    $g.FillRectangle((New-Gradient $rect ([System.Drawing.Color]::FromArgb(4,40,24)) ([System.Drawing.Color]::FromArgb(22,128,62)) 70), $rect)

    # soft keycaps across the lower part, fading out towards the bottom
    $capFill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(26, 255, 255, 255))
    $capEdge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(46, 255, 255, 255)), ([single]([Math]::Max(1, 1.2 * $kk)))
    $key = 27 * $kk; $gap = 6 * $kk; $round = 6 * $kk
    for ($row = 0; $row -lt 5; $row++) {
        $y = 182 * $kk + $row * ($key + $gap)
        $offset = ($row % 2) * (($key + $gap) / 2) - 14 * $kk
        for ($col = 0; $col -lt 7; $col++) {
            $x = $offset + $col * ($key + $gap)
            $path = New-Object System.Drawing.Drawing2D.GraphicsPath
            $d = $round * 2
            $path.AddArc($x, $y, $d, $d, 180, 90); $path.AddArc($x + $key - $d, $y, $d, $d, 270, 90)
            $path.AddArc($x + $key - $d, $y + $key - $d, $d, $d, 0, 90); $path.AddArc($x, $y + $key - $d, $d, $d, 90, 90)
            $path.CloseFigure()
            $g.FillPath($capFill, $path); $g.DrawPath($capEdge, $path)
        }
    }
    $fadeRect = New-Object System.Drawing.Rectangle 0, ([int](150 * $kk)), $BW, ([int](170 * $kk))
    $fade = New-Gradient $fadeRect ([System.Drawing.Color]::FromArgb(0, 4, 40, 24)) ([System.Drawing.Color]::FromArgb(170, 4, 40, 24)) 90
    $g.FillRectangle($fade, 0, [int](150 * $kk), $BW, $BH - [int](150 * $kk))

    # the logo, large and centred, with a soft glow behind it
    $size = [int](100 * $kk)
    $lx = [int](($BW - $size) / 2); $ly = [int](24 * $kk)
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse([int]($lx - 26 * $kk), [int]($ly - 20 * $kk), [int]($size + 52 * $kk), [int]($size + 52 * $kk))
    $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
    $glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(90, 74, 222, 128)
    $glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 74, 222, 128))
    $g.FillPath($glowBrush, $glow)
    Draw-Logo $g $lx $ly $size

    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
    $title = New-Object System.Drawing.Font 'Segoe UI Semibold', (16 * $kk), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sub = New-Object System.Drawing.Font 'Segoe UI', (11.5 * $kk), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString("Keyboard Writer`nHelper", $title, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new(0, 140 * $kk, $BW, 56 * $kk), $fmt)
    $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(225, 236, 247, 240))
    $g.DrawString("Spelling fixed as you type,`nin every program.", $sub, $soft, [System.Drawing.RectangleF]::new(0, 204 * $kk, $BW, 40 * $kk), $fmt)
    $dim = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(190, 236, 247, 240))
    $g.DrawString("Version $Version`nby YATESH ROHIT", $sub, $dim, [System.Drawing.RectangleF]::new(0, 272 * $kk, $BW, 40 * $kk), $fmt)
    $g.Dispose()

    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $gs = [System.Drawing.Graphics]::FromImage($bmp)
    $gs.InterpolationMode = 'HighQualityBicubic'; $gs.PixelOffsetMode = 'HighQuality'; $gs.SmoothingMode = 'HighQuality'
    $gs.DrawImage($big, 0, 0, $w, $h)
    $gs.Dispose(); $big.Dispose()
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

# One exact-size picture per Windows scaling level, so it is never stretched.
foreach ($pct in 100, 125, 150, 175, 200, 250, 300) { New-Large $pct; New-Small $pct }

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
