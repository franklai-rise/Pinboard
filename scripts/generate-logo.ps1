param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Pinboard.App\Assets")
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function New-PinboardLogoPng {
    param([Parameter(Mandatory = $true)][int]$Size)

    $scale = $Size / 1024.0
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))

    $blue = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0, 122, 255))
    $white = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Colors]::White)
    $blue.Freeze()
    $white.Freeze()

    $drawing.DrawRoundedRectangle(
        $blue,
        $null,
        [System.Windows.Rect]::new(64, 64, 896, 896),
        210,
        210)

    $drawing.DrawRoundedRectangle(
        $white,
        $null,
        [System.Windows.Rect]::new(254, 248, 516, 420),
        92,
        92)
    $drawing.DrawRoundedRectangle($blue, $null, [System.Windows.Rect]::new(326, 342, 372, 44), 22, 22)
    $drawing.DrawRoundedRectangle($blue, $null, [System.Windows.Rect]::new(326, 446, 246, 38), 19, 19)

    $stem = [System.Windows.Media.StreamGeometry]::new()
    $stemContext = $stem.Open()
    $stemContext.BeginFigure([System.Windows.Point]::new(463, 640), $true, $true)
    $stemContext.LineTo([System.Windows.Point]::new(561, 640), $true, $false)
    $stemContext.LineTo([System.Windows.Point]::new(561, 746), $true, $false)
    $stemContext.LineTo([System.Windows.Point]::new(512, 840), $true, $false)
    $stemContext.LineTo([System.Windows.Point]::new(463, 746), $true, $false)
    $stemContext.Close()
    $stem.Freeze()
    $drawing.DrawGeometry($white, $null, $stem)

    $drawing.Pop()
    $drawing.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size,
        $Size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return $bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[object]]::new()

foreach ($size in $sizes) {
    $images.Add([pscustomobject]@{
        Size = $size
        Bytes = New-PinboardLogoPng -Size $size
    })
}

$masterBytes = New-PinboardLogoPng -Size 1024
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory "PinboardLogo.png"), $masterBytes)
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory "PinboardLogo-32.png"), ($images | Where-Object Size -eq 32).Bytes)

$iconPath = Join-Path $OutputDirectory "Pinboard.ico"
$iconStream = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($iconStream)

try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

Write-Host "Generated Pinboard logo assets in $OutputDirectory"
