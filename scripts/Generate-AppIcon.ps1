param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\src\CodexTokenMonitor.App\Assets")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing.Common

function New-RoundedPath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $margin = [float]($Size * 0.055)
    $tileSize = [float]($Size - ($margin * 2))
    $tileRadius = [float]($Size * 0.205)
    $tilePath = New-RoundedPath $margin $margin $tileSize $tileSize $tileRadius
    $tileBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 25, 51, 43))
    $graphics.FillPath($tileBrush, $tilePath)

    $outlineWidth = [Math]::Max([float]($Size * 0.026), 1.0)
    $outlinePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(220, 238, 239, 229), $outlineWidth)
    $outlinePen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
    $graphics.DrawPath($outlinePen, $tilePath)

    $barBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 242, 111, 54))
    $capBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 205, 119))
    $barWidth = [float]($Size * 0.13)
    $gap = [float]($Size * 0.055)
    $startX = [float]($Size * 0.245)
    $bottom = [float]($Size * 0.745)
    $heights = @([float]($Size * 0.22), [float]($Size * 0.35), [float]($Size * 0.49))

    for ($index = 0; $index -lt $heights.Count; $index++) {
        $height = $heights[$index]
        $x = $startX + (($barWidth + $gap) * $index)
        $y = $bottom - $height
        $barRadius = [Math]::Max([float]($barWidth * 0.26), 0.8)
        $barPath = New-RoundedPath $x $y $barWidth $height $barRadius
        $graphics.FillPath($(if ($index -eq 2) { $capBrush } else { $barBrush }), $barPath)
        $barPath.Dispose()
    }

    $baselinePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(190, 238, 239, 229), [Math]::Max([float]($Size * 0.018), 1.0))
    $baselinePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $baselinePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($baselinePen, [float]($Size * 0.225), [float]($Size * 0.795), [float]($Size * 0.775), [float]($Size * 0.795))

    $baselinePen.Dispose()
    $capBrush.Dispose()
    $barBrush.Dispose()
    $outlinePen.Dispose()
    $tileBrush.Dispose()
    $tilePath.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Convert-BitmapToPngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = [System.IO.MemoryStream]::new()
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return ,$bytes
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

$preview = New-IconBitmap 512
$previewPath = Join-Path $resolvedOutput "app-icon-preview.png"
$preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    try {
        [pscustomobject]@{ Size = $size; Bytes = (Convert-BitmapToPngBytes $bitmap) }
    }
    finally {
        $bitmap.Dispose()
    }
}

$iconPath = Join-Path $resolvedOutput "app.ico"
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { [byte]0 } else { [byte]$image.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) {
        $writer.Write($image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output "Generated $iconPath"
Write-Output "Generated $previewPath"
