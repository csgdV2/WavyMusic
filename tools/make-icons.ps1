[CmdletBinding()]
param(
    [string]$MainSource = "$HOME\Downloads\icon.png",
    [string]$WaveSource = "$HOME\Downloads\New Project (1).png"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\MusicApp\Assets'))
if (-not (Test-Path $assets)) { New-Item -ItemType Directory -Path $assets | Out-Null }

function Get-PngBytes {
    param([System.Drawing.Image]$Image, [int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($Image, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)))
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

if (-not (Test-Path $MainSource)) { throw "Main icon source not found: $MainSource" }
$src = [System.Drawing.Image]::FromFile((Resolve-Path $MainSource).Path)
Write-Host ("main source: {0}x{1} {2}" -f $src.Width, $src.Height, $src.PixelFormat)

[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.png'), (Get-PngBytes -Image $src -Size 512))

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($s in $sizes) { Get-PngBytes -Image $src -Size $s }
$src.Dispose()

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0)
$w.Write([uint16]1)
$w.Write([uint16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0)
    $w.Write([byte]0)
    $w.Write([uint16]1)
    $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $ico.ToArray())
$w.Dispose()
Write-Host ("app.ico: {0} entries, {1} bytes" -f $sizes.Count, (Get-Item (Join-Path $assets 'app.ico')).Length)

if (-not (Test-Path $WaveSource)) { throw "Wave source not found: $WaveSource" }
$wave = New-Object System.Drawing.Bitmap ([System.Drawing.Image]::FromFile((Resolve-Path $WaveSource).Path))
Write-Host ("wave source: {0}x{1} {2}" -f $wave.Width, $wave.Height, $wave.PixelFormat)

$rect = New-Object System.Drawing.Rectangle(0, 0, $wave.Width, $wave.Height)
$data = $wave.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bytes = New-Object byte[] ($data.Stride * $wave.Height)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$stride = $data.Stride
$wave.UnlockBits($data)

$minX = $wave.Width; $minY = $wave.Height; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $wave.Height; $y++) {
    $row = $y * $stride
    for ($x = 0; $x -lt $wave.Width; $x++) {
        if ($bytes[$row + ($x * 4) + 3] -gt 8) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
if ($maxX -lt 0) { throw 'Wave source is fully transparent.' }

$crop = New-Object System.Drawing.Rectangle($minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
$cropped = $wave.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$wave.Dispose()
$cropped.Save((Join-Path $assets 'wave.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host ("wave.png: {0}x{1} (cropped from {2})" -f $cropped.Width, $cropped.Height, $crop)
$cropped.Dispose()
