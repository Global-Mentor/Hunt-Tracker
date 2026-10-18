$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$assets=Join-Path $root 'src/hunt-tracker-desktop/assets'
Add-Type -AssemblyName PresentationCore,WindowsBase
[xml]$svg=Get-Content -LiteralPath (Join-Path $assets 'app-icon.svg') -Raw
$frames=@()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $visual=[Windows.Media.DrawingVisual]::new()
    $drawing=$visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
    foreach ($shape in $svg.DocumentElement.ChildNodes) {
        $geometry=[Windows.Media.Geometry]::Parse($shape.GetAttribute('d'))
        $brush=[Windows.Media.BrushConverter]::new().ConvertFromInvariantString($shape.GetAttribute('fill'))
        $drawing.DrawGeometry($brush,$null,$geometry)
    }
    $drawing.Pop(); $drawing.Close()
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $buffer=[IO.MemoryStream]::new()
    try { $encoder.Save($buffer); $frames+=,[pscustomobject]@{Size=$size;Bytes=$buffer.ToArray()} } finally { $buffer.Dispose() }
}
$stream=[IO.File]::Create((Join-Path $assets 'app-icon.ico'))
$writer=[IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset=6+16*$frames.Count
    foreach ($frame in $frames) {
        $dimension=if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset+=$frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
