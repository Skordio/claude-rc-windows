# Draws the tray/app icon (a plain terminal prompt, no external branding) and writes it as a
# multi-size .ico with PNG entries (16, 20, 24, 32, 40, 48, 64, 256 px).
# Run with PowerShell 7 on Windows:  pwsh tools/make-tray-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot '..\src\ClaudeRc.Tray\Assets\tray.ico'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-IconPng([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded dark tile.
    $r = [Math]::Max(2, [int]($size * 0.2))
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = 2 * $r
    $w = $size - 1
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $tile = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 38, 50, 66))
    $g.FillPath($tile, $path)

    # Prompt chevron ">" and underscore "_" in white.
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1.6, $size * 0.11))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $s = [single]$size
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($s * 0.24, $s * 0.28),
        [System.Drawing.PointF]::new($s * 0.46, $s * 0.48),
        [System.Drawing.PointF]::new($s * 0.24, $s * 0.68)))
    $g.DrawLine($pen, $s * 0.52, $s * 0.70, $s * 0.76, $s * 0.70)

    # Green "running" dot, top right.
    $dot = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 46, 204, 113))
    $ds = $s * 0.26
    $g.FillEllipse($dot, $s * 0.68, $s * 0.08, $ds, $ds)

    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }

$ico = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($ico)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf), $ico.ToArray())
Write-Output "wrote $Out ($($ico.Length) bytes)"
