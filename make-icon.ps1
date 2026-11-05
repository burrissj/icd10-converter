# Generates app.ico (blue rounded square with "ICD" text). Run once; app.ico is committed alongside.
Add-Type -AssemblyName System.Drawing
$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 52; $d = $r * 2
$path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($size - $d, 0, $d, $d, 270, 90)
$path.AddArc($size - $d, $size - $d, $d, $d, 0, 90); $path.AddArc(0, $size - $d, $d, $d, 90, 90); $path.CloseFigure()
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0,0)), ([System.Drawing.Point]::new($size,$size)), ([System.Drawing.Color]::FromArgb(37,99,235)), ([System.Drawing.Color]::FromArgb(15,23,42))
$g.FillPath($brush, $path)
$font = New-Object System.Drawing.Font 'Segoe UI', 74, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
$g.DrawString('ICD', $font, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new(0, 52, $size, 100), $sf)
$small = New-Object System.Drawing.Font 'Segoe UI', 54, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('{ 10 }', $small, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(147,197,253))), [System.Drawing.RectangleF]::new(0, 140, $size, 80), $sf)
$g.Dispose()
$ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $png = $ms.ToArray()
$fs = [System.IO.File]::Create((Join-Path $PSScriptRoot 'app.ico')); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)
$bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
$bw.Write([UInt32]$png.Length); $bw.Write([UInt32]22); $bw.Write($png); $bw.Close()
