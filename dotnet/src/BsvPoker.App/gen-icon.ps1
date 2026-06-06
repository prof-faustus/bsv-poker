# Generates poker.ico — a clean, premium app icon (deep-green felt, gold rounded border, gold spade).
# Uses System.Drawing (Windows PowerShell). Produces a 256x256 PNG packed into a single-image .ico.
Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::Transparent)

# rounded-rect path
$r = 56
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(2, 2, $r, $r, 180, 90)
$path.AddArc($size - $r - 2, 2, $r, $r, 270, 90)
$path.AddArc($size - $r - 2, $size - $r - 2, $r, $r, 0, 90)
$path.AddArc(2, $size - $r - 2, $r, $r, 90, 90)
$path.CloseFigure()

# deep-green felt gradient fill
$rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 22, 92, 54), [System.Drawing.Color]::FromArgb(255, 7, 38, 23), 65)
$g.FillPath($bg, $path)

# gold border
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 201, 162, 39), 9)
$g.DrawPath($pen, $path)

# centered gold spade
$font = New-Object System.Drawing.Font("Segoe UI Symbol", 150, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$gold = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 235, 205, 110))
$layout = New-Object System.Drawing.RectangleF(0, -6, $size, $size)
$g.DrawString([string][char]0x2660, $font, $gold, $layout, $sf)
$g.Flush()

$pngPath = Join-Path $PSScriptRoot "poker256.png"
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# pack the PNG into a single-image .ico
$png = [System.IO.File]::ReadAllBytes($pngPath)
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)   # reserved, type=icon, count=1
$bw.Write([byte]0); $bw.Write([byte]0)                            # 0,0 => 256x256
$bw.Write([byte]0); $bw.Write([byte]0)                            # palette, reserved
$bw.Write([uint16]1); $bw.Write([uint16]32)                       # planes, bpp
$bw.Write([uint32]$png.Length)
$bw.Write([uint32]22)                                             # offset = 6 + 16
$bw.Write($png)
$bw.Flush()
$icoPath = Join-Path $PSScriptRoot "poker.ico"
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
Remove-Item $pngPath -ErrorAction SilentlyContinue
Write-Output "wrote $icoPath ($((Get-Item $icoPath).Length) bytes)"
