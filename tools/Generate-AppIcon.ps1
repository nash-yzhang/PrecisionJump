param(
    [string]$OutputPath = "$PSScriptRoot\..\Assets\MouseAccelerator.ico"
)

Add-Type -AssemblyName System.Drawing

$bitmap = [System.Drawing.Bitmap]::new(64, 64)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

$bounds = [System.Drawing.RectangleF]::new(2, 2, 60, 60)
$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
$diameter = 30
$path.AddArc($bounds.Left, $bounds.Top, $diameter, $diameter, 180, 90)
$path.AddArc($bounds.Right - $diameter, $bounds.Top, $diameter, $diameter, 270, 90)
$path.AddArc($bounds.Right - $diameter, $bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
$path.AddArc($bounds.Left, $bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
$path.CloseFigure()

$background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Rectangle]::new(0, 0, 64, 64),
    [System.Drawing.Color]::FromArgb(17, 36, 39),
    [System.Drawing.Color]::FromArgb(23, 157, 141),
    45
)
$graphics.FillPath($background, $path)

$cursor = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(20, 12),
    [System.Drawing.PointF]::new(46, 35),
    [System.Drawing.PointF]::new(34, 38),
    [System.Drawing.PointF]::new(40, 51),
    [System.Drawing.PointF]::new(32, 55),
    [System.Drawing.PointF]::new(26, 41),
    [System.Drawing.PointF]::new(18, 50)
)
$white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
$graphics.FillPolygon($white, $cursor)

$movementPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 3)
$movementPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$movementPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($movementPen, 7, 18, 14, 18)
$graphics.DrawLine($movementPen, 6, 27, 14, 27)
$graphics.DrawLine($movementPen, 8, 36, 15, 36)

$pngStream = [System.IO.MemoryStream]::new()
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$file = [System.IO.File]::Open(
    $resolvedOutput,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write
)
$writer = [System.IO.BinaryWriter]::new($file)

# ICONDIR
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)

# ICONDIRENTRY: one PNG-compressed 64×64, 32-bit image.
$writer.Write([byte]64)
$writer.Write([byte]64)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$pngBytes.Length)
$writer.Write([uint32]22)
$writer.Write($pngBytes)

$writer.Dispose()
$file.Dispose()
$pngStream.Dispose()
$movementPen.Dispose()
$white.Dispose()
$background.Dispose()
$path.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

Write-Output $resolvedOutput
