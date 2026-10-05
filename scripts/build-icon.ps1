# Render the same three C channels as ConduitWordmark into Windows icon sizes.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = foreach ($size in $sizes) {
    $bmp = [Drawing.Bitmap]::new($size * 4, $size * 4)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform(4,4)
    $g.Clear([Drawing.Color]::Transparent)
    $background = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(23,25,35))
    $outline = [Drawing.Drawing2D.GraphicsPath]::new()
    $radius = [single]($size * .36)
    $edge = [single]($size - 1)
    $outline.AddArc(0,0,$radius,$radius,180,90)
    $outline.AddArc($edge-$radius,0,$radius,$radius,270,90)
    $outline.AddArc($edge-$radius,$edge-$radius,$radius,$radius,0,90)
    $outline.AddArc(0,$edge-$radius,$radius,$radius,90,90)
    $outline.CloseFigure(); $g.FillPath($background,$outline)
    $channel = $size * .70; $origin = ($size - $channel) / 2
    for ($ring = 0; $ring -lt 3; $ring++) {
        $inset = $ring * $channel * .14; $diameter = $channel - 2 * $inset
        $rect = [Drawing.RectangleF]::new($origin+$inset,$origin+$inset,$diameter,$diameter)
        $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new($rect,[Drawing.Color]::FromArgb(112,185,216),[Drawing.Color]::FromArgb(211,157,185),[single]35)
        $pen = [Drawing.Pen]::new($gradient,[single]([Math]::Max(.9,$channel*.055)))
        $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($pen,$rect,42,276)
        $angle = (180 + [Math]::Sin(-$ring*.7)*108)*[Math]::PI/180
        $x = $rect.Left+$diameter/2+[Math]::Cos($angle)*$diameter/2
        $y = $rect.Top+$diameter/2+[Math]::Sin($angle)*$diameter/2
        $dot = [Math]::Max(.8,$channel*.065)
        $light = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(225,224,237))
        $g.FillEllipse($light,[single]($x-$dot/2),[single]($y-$dot/2),[single]$dot,[single]$dot)
        $light.Dispose(); $pen.Dispose(); $gradient.Dispose()
    }
    $g.Dispose(); $background.Dispose(); $outline.Dispose()
    $small = [Drawing.Bitmap]::new($size,$size)
    $sg = [Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.DrawImage($bmp,0,0,$size,$size); $sg.Dispose(); $bmp.Dispose()
    $stream = [IO.MemoryStream]::new()
    $small.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray(); $small.Dispose(); $stream.Dispose()
    [pscustomobject]@{Size=$size; Bytes=$bytes}
}
$output = [IO.File]::Create((Join-Path $assets 'Conduit.ico'))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Host 'Rendered Conduit.ico: 16 through 256 pixels.'
