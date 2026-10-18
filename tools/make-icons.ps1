<#
  Generates assets/app.ico and assets/tray-{green,orange,red,grey}.ico with System.Drawing.
  Radar concept: rounded dark-navy square, green rings, one sweep line, blips.
  At small sizes (<= 24 px) rings are dropped and only the sweep plus one blip remain.
  Usage: powershell -File tools/make-icons.ps1
#>
Add-Type -AssemblyName System.Drawing
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
New-Item -ItemType Directory -Force $out | Out-Null

function New-Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
$navy   = New-Color '#0F1B2D'
$ring   = New-Color '#2E9E5B'
$colors = @{ green = '#2E9E5B'; orange = '#E08A1E'; red = '#D63A3A'; grey = '#7A7F87' }

function Get-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

# $state: 'app' draws three blips (green/orange/red). Otherwise a single blip in the state colour.
function New-RadarBitmap([int]$size, [string]$state) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]$size

    $path = Get-RoundedPath 0 0 $s $s ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush $navy), $path)

    $cx = $s / 2; $cy = $s / 2
    $small = $size -le 24
    $accent = if ($state -eq 'app') { $ring } else { New-Color $colors[$state] }

    if (-not $small) {
        foreach ($f in 0.42, 0.28, 0.14) {
            $r = $s * $f
            $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, $ring)), ([Math]::Max(1.0, $s / 64))
            $g.DrawEllipse($pen, $cx - $r, $cy - $r, $r * 2, $r * 2)
        }
    }

    # crosshair and a fading sweep wedge trailing the line (large sizes only)
    if (-not $small) {
        $cross = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, $ring)), ([Math]::Max(1.0, $s / 96))
        $g.DrawLine($cross, $cx, $s * 0.08, $cx, $s * 0.92)
        $g.DrawLine($cross, $s * 0.08, $cy, $s * 0.92, $cy)
        $wr = $s * 0.42
        $deg = [Math]::Atan2(-0.62, 0.78) * 180 / [Math]::PI      # sweep angle, GDI (clockwise from +x)
        foreach ($span in 60, 40, 20) {
            $wb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(34, $accent))
            $g.FillPie($wb, $cx - $wr, $cy - $wr, $wr * 2, $wr * 2, [float]($deg - $span), [float]$span)
        }
    }

    # sweep line toward upper right
    $sweepW = if ($small) { [Math]::Max(1.6, $s / 7) } else { [Math]::Max(1.5, $s / 26) }
    $sweep = New-Object System.Drawing.Pen $accent, $sweepW
    $sweep.StartCap = 'Round'; $sweep.EndCap = 'Round'
    $len = $s * 0.40
    $ex = $cx + $len * 0.78; $ey = $cy - $len * 0.62
    $g.DrawLine($sweep, $cx, $cy, $ex, $ey)

    function Blip([float]$bx, [float]$by, [float]$br, $col) {
        if ($size -gt 24) {
            $glow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, $col))
            $g.FillEllipse($glow, $bx - $br * 1.9, $by - $br * 1.9, $br * 3.8, $br * 3.8)
        }
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $col), $bx - $br, $by - $br, $br * 2, $br * 2)
    }
    if ($state -eq 'app') {
        $br = $s * 0.065
        Blip ($cx + $s * 0.20) ($cy - $s * 0.18) $br (New-Color $colors.green)
        Blip ($cx - $s * 0.22) ($cy + $s * 0.10) $br (New-Color $colors.orange)
        Blip ($cx + $s * 0.12) ($cy + $s * 0.26) $br (New-Color $colors.red)
    } else {
        $br = if ($small) { $s * 0.18 } else { $s * 0.09 }
        Blip ($cx - $s * 0.14) ($cy + $s * 0.18) $br $accent
    }
    # light rim: keeps the navy tile visible against a dark taskbar
    $rimW = [Math]::Max(1.0, $s / 40)
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 120, 150, 185)), $rimW
    $rimPath = Get-RoundedPath ($rimW / 2) ($rimW / 2) ($s - $rimW) ($s - $rimW) ($s * 0.22 - $rimW / 2)
    $g.DrawPath($rim, $rimPath)
    $g.Dispose()
    $bmp
}

# Multi-size ICO container with PNG-compressed images.
function Write-Ico([string]$path, [int[]]$sizes, [string]$state) {
    $pngs = foreach ($sz in $sizes) {
        $bmp = New-RadarBitmap $sz $state
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]
        $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
        $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Dispose(); $fs.Dispose()
}

Write-Ico (Join-Path $out 'app.ico') @(16, 24, 32, 48, 64, 256) 'app'
foreach ($name in $colors.Keys) {
    Write-Ico (Join-Path $out "tray-$name.ico") @(16, 20, 24, 32) $name
}

# Preview strip for eyeballing: each state at 16/32/64 px on dark and light backgrounds.
$names = @('app') + @($colors.Keys)
$prev = New-Object System.Drawing.Bitmap (($names.Count * 80)), 220
$pg = [System.Drawing.Graphics]::FromImage($prev)
$pg.Clear([System.Drawing.Color]::FromArgb(32, 32, 32))
$pg.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 240, 240))), 0, 110, $prev.Width, 110)
$pg.InterpolationMode = 'NearestNeighbor'
for ($i = 0; $i -lt $names.Count; $i++) {
    foreach ($row in 0, 1) {
        $y0 = 6 + 110 * $row
        $b16 = New-RadarBitmap 16 $names[$i]; $b32 = New-RadarBitmap 32 $names[$i]; $b64 = New-RadarBitmap 64 $names[$i]
        $pg.DrawImage($b16, $i * 80 + 8, $y0, 16, 16)
        $pg.DrawImage($b32, $i * 80 + 8, $y0 + 22, 32, 32)
        $pg.DrawImage($b64, $i * 80 + 8, $y0 + 44, 64, 64)
    }
}
$prev.Save((Join-Path $out 'preview.png'))
Write-Host "Icons written to $out"
