Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$size = 256
$dv = New-Object System.Windows.Media.DrawingVisual
$dc = $dv.RenderOpen()

# Background card with rounded corners
$bgBrush = New-Object System.Windows.Media.LinearGradientBrush(
    [System.Windows.Media.Color]::FromArgb(255, 18, 18, 24),
    [System.Windows.Media.Color]::FromArgb(255, 30, 30, 42),
    45.0
)
$rect = New-Object System.Windows.Rect(8, 8, 240, 240)
$borderBrush = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(255, 75, 80, 110))
$dc.DrawRoundedRectangle($bgBrush, (New-Object System.Windows.Media.Pen($borderBrush, 3)), $rect, 36, 36)

# Terminal header bar
$headerBrush = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(255, 28, 28, 38))
$headerRect = New-Object System.Windows.Rect(8, 8, 240, 48)
$dc.DrawRoundedRectangle($headerBrush, $null, $headerRect, 36, 36)
$headerFill = New-Object System.Windows.Rect(8, 32, 240, 24)
$dc.DrawRectangle($headerBrush, $null, $headerFill)

# Window dots
$dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(255, 255, 95, 86))), $null, (New-Object System.Windows.Point(36, 32)), 6, 6)
$dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(255, 255, 189, 46))), $null, (New-Object System.Windows.Point(56, 32)), 6, 6)
$dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(255, 39, 201, 63))), $null, (New-Object System.Windows.Point(76, 32)), 6, 6)

# Prompt icon (>_)
$promptPen = New-Object System.Windows.Media.Pen(
    (New-Object System.Windows.Media.LinearGradientBrush(
        [System.Windows.Media.Color]::FromArgb(255, 99, 102, 241),
        [System.Windows.Media.Color]::FromArgb(255, 168, 85, 247),
        0.0
    )),
    14
)
$promptPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
$promptPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
$promptPen.LineJoin = [System.Windows.Media.PenLineJoin]::Round

# Drawing '>' symbol
$p1 = New-Object System.Windows.Point(44, 96)
$p2 = New-Object System.Windows.Point(84, 128)
$p3 = New-Object System.Windows.Point(44, 160)
$dc.DrawLine($promptPen, $p1, $p2)
$dc.DrawLine($promptPen, $p2, $p3)

# Drawing '_' cursor
$c1 = New-Object System.Windows.Point(100, 160)
$c2 = New-Object System.Windows.Point(140, 160)
$cursorPen = New-Object System.Windows.Media.Pen(
    (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(255, 56, 189, 248))),
    14
)
$cursorPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
$cursorPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
$dc.DrawLine($cursorPen, $c1, $c2)

# Split pane line divider
$panePen = New-Object System.Windows.Media.Pen(
    (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(100, 255, 255, 255))),
    4
)
$dc.DrawLine($panePen, (New-Object System.Windows.Point(164, 56)), (New-Object System.Windows.Point(164, 230)))

$dc.Close()

$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($dv)

$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))

$ms = New-Object System.IO.MemoryStream
$encoder.Save($ms)
$pngBytes = $ms.ToArray()
$ms.Close()

$icoPath = Join-Path $PSScriptRoot "app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]1)

# ICONDIRENTRY
$bw.Write([byte]0)   # 256 px
$bw.Write([byte]0)   # 256 px
$bw.Write([byte]0)
$bw.Write([byte]0)
$bw.Write([uint16]1)
$bw.Write([uint16]32)
$bw.Write([uint32]$pngBytes.Length)
$bw.Write([uint32]22)

# Image Data (PNG)
$bw.Write($pngBytes)
$bw.Close()
$fs.Close()
Write-Host "Created icon successfully at $icoPath"
