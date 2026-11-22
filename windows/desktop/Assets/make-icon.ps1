<#
.SYNOPSIS
    Regenerates CaboCam.ico (multi-size, PNG entries) from the iOS AppIcon.png.
    Small sizes crop the margin so the lens stays readable.
    Usage: powershell -ExecutionPolicy Bypass -File windows\desktop\Assets\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$src  = Join-Path $root 'ios\CaboCam\Assets.xcassets\AppIcon.appiconset\AppIcon.png'
$dst  = Join-Path $PSScriptRoot 'CaboCam.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$source = [System.Drawing.Image]::FromFile($src)
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square clip (22% radius).
    $r = [Math]::Max(2, [int]($s * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.SetClip($path)

    # Crop more at small sizes so the lens fills the icon.
    $crop = if ($s -le 32) { 150 } elseif ($s -le 64) { 90 } else { 0 }
    $srcRect = New-Object System.Drawing.Rectangle($crop, $crop, (1024 - 2 * $crop), (1024 - 2 * $crop))
    $g.DrawImage($source, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)), $srcRect, 'Pixel')
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $entries += ,@($s, $ms.ToArray())
}
$source.Dispose()

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $s = $e[0]; $bytes = $e[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$bytes.Length); $w.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($e in $entries) { $w.Write([byte[]]$e[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes($dst, $out.ToArray())
Write-Host "Wrote $dst ($($out.Length) bytes)"
