param(
    [string]$InputPng = (Join-Path (Split-Path $PSScriptRoot -Parent) 'ScreenTask/Assets/ScreenTask.png'),
    [string]$OutputIco = (Join-Path (Split-Path $PSScriptRoot -Parent) 'ScreenTask/Assets/ScreenTask.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $InputPng).Path)
$container = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($container)
try {
    if ($source.Width -ne $source.Height -or $source.Width -lt 256) {
        throw 'The icon master must be square and at least 256 pixels wide.'
    }
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = foreach ($size in $sizes) {
        $frame = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($frame)
        $png = [System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
            $frame.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size = $size; Bytes = $png.ToArray() }
        } finally { $png.Dispose(); $graphics.Dispose(); $frame.Dispose() }
    }
    # PNG-backed 32-bit ICO frames preserve alpha on all supported Windows 10/11 hosts.
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($OutputIco), $container.ToArray())
    Write-Output "Created $OutputIco with $($sizes.Count) icon sizes."
} finally { $writer.Dispose(); $container.Dispose(); $source.Dispose() }
