$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
[xml]$vector = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'KeepQuiet.svg') -Raw -Encoding UTF8
$sizes = @(16,20,24,32,48,64,128,256)
$images = @()
foreach ($size in $sizes) {
    $canvas = [System.Drawing.Bitmap]::new($size * 4, $size * 4)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($vector.svg.stroke), [single]$vector.svg.'stroke-width')
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $final = [System.Drawing.Graphics]::FromImage($bitmap)
    $stream = [System.IO.MemoryStream]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform([single]($size * 4 / 120.0), [single]($size * 4 / 120.0))
        $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        foreach ($rect in $vector.svg.rect) {
            $outline = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $border = [System.Drawing.Pen]::new($pen.Color, [single]$rect.'stroke-width')
            try {
                $x = [single]$rect.x; $y = [single]$rect.y
                $width = [single]$rect.width; $height = [single]$rect.height; $diameter = [single]$rect.rx * 2
                $outline.AddArc($x, $y, $diameter, $diameter, 180, 90)
                $outline.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
                $outline.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
                $outline.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
                $outline.CloseFigure()
                $graphics.DrawPath($border, $outline)
            }
            finally { $border.Dispose(); $outline.Dispose() }
        }
        foreach ($line in $vector.svg.line) {
            $graphics.DrawLine($pen, [single]$line.x1, [single]$line.y1, [single]$line.x2, [single]$line.y2)
        }
        $final.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $final.DrawImage($canvas, 0, 0, $size, $size)
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += ,$stream.ToArray()
        if ($size -eq 256) { $bitmap.Save((Join-Path $PSScriptRoot 'KeepQuiet.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    }
    finally { $stream.Dispose(); $final.Dispose(); $bitmap.Dispose(); $pen.Dispose(); $graphics.Dispose(); $canvas.Dispose() }
}
$file = [System.IO.File]::Create((Join-Path $PSScriptRoot 'KeepQuiet.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
}
finally { $writer.Dispose() }
