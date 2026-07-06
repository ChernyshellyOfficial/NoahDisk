# Generates gui/app.ico (multi-size treemap-style icon).
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File gui\make-icon.ps1
# Tweak colors in the $cells list below, then rebuild the project.

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x,[float]$y,[float]$w,[float]$h,[float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    if ($d -gt $w) { $d = $w }
    if ($d -gt $h) { $d = $h }
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [float]($S * 0.06)
    $cw = [float]($S - 2*$m)
    $cont = New-RoundedPath $m $m $cw $cw ([float]($S * 0.22))
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,27,36,51))  # container
    $g.FillPath($bg, $cont)
    $g.SetClip($cont)

    $gap = [float]($S * 0.05)
    $br  = [float]($S * 0.05)
    $cells = @(
      @{x=0.00; y=0.00; w=0.56; h=1.00; c=@(61,139,253)},   # blue
      @{x=0.56; y=0.00; w=0.44; h=0.54; c=@(91,209,139)},   # green
      @{x=0.56; y=0.54; w=0.26; h=0.46; c=@(245,160,91)},   # orange
      @{x=0.82; y=0.54; w=0.18; h=0.46; c=@(156,124,255)}   # purple
    )
    foreach ($cell in $cells) {
      $bx = $m + $cell.x * $cw + $gap/2
      $by = $m + $cell.y * $cw + $gap/2
      $bw = $cell.w * $cw - $gap
      $bh = $cell.h * $cw - $gap
      if ($bw -le 0 -or $bh -le 0) { continue }
      $path = New-RoundedPath $bx $by $bw $bh $br
      $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, $cell.c[0], $cell.c[1], $cell.c[2]))
      $g.FillPath($brush, $path)
      $brush.Dispose(); $path.Dispose()
    }
    $g.ResetClip(); $bg.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = @(16,24,32,48,64,128,256)
$pngs = @()
foreach ($s in $sizes) {
   $bmp = Render $s
   $ms = New-Object System.IO.MemoryStream
   $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
   $pngs += ,($ms.ToArray())
   $bmp.Dispose(); $ms.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
   $s = $sizes[$i]; $data = $pngs[$i]
   $wb = if ($s -ge 256) {0} else {$s}
   $bw.Write([Byte]$wb); $bw.Write([Byte]$wb); $bw.Write([Byte]0); $bw.Write([Byte]0)
   $bw.Write([UInt16]1); $bw.Write([UInt16]32)
   $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
   $offset += $data.Length
}
foreach ($data in $pngs) { $bw.Write($data) }
$bw.Flush()
$dest = Join-Path $PSScriptRoot 'app.ico'
[System.IO.File]::WriteAllBytes($dest, $out.ToArray())
$out.Dispose()
Write-Host "Wrote $dest"
