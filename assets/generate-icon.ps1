<#
    Generates assets/rolloutloud.ico and assets/rolloutloud-256.png.

    The mark is three chevrons climbing to the right, stepping cyan -> blue -> indigo. That is
    the product in one shape: keep going, and when going does not work, escalate a tier. The
    three colours are the three tiers of the escalation ladder.

    Each size is REDRAWN rather than resampled from a large bitmap. Resampling 256 -> 16 turns a
    three-chevron mark into a smudge; at 16 and 24 px the drawing collapses to a single thick
    chevron, which is what makes it readable in a taskbar and in a catalogue listing.

    Usage:  powershell -ExecutionPolicy Bypass -File assets\generate-icon.ps1
#>

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$icoPath = Join-Path $outDir 'rolloutloud.ico'
$pngPath = Join-Path $outDir 'rolloutloud-256.png'

$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-RolloutLoudBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0
    # 32 and below get the single chevron. I set this at 24 first and the 32 px entry came out
    # muddy — three 3-pixel strokes with the arms nearly touching, which is the size Windows
    # actually shows in a list view. Judged by rendering the .ico entries at 1:1, not by scaling
    # the 256 down.
    $simple = $size -le 32

    # --- rounded plate -----------------------------------------------------------------
    $radius = [Math]::Max(2.0, 56 * $s)
    $plate = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $plate.AddArc(0, 0, $d, $d, 180, 90)
    $plate.AddArc($size - $d, 0, $d, $d, 270, 90)
    $plate.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $plate.AddArc(0, $size - $d, $d, $d, 90, 90)
    $plate.CloseFigure()

    $plateBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 22, 26, 40),
        [System.Drawing.Color]::FromArgb(255, 10, 12, 20))
    $g.FillPath($plateBrush, $plate)

    # --- the mark: three chevrons climbing right -----------------------------------------
    #
    # Colour carries the ladder, not opacity. The first attempt faded the lower chevrons with
    # alpha and they went muddy grey against the plate — a dimmed blue is not a darker blue, it
    # is a washed-out one. Three solid steps along the cyan-to-indigo ramp read as a progression
    # at any size, and stay saturated at 16 px where a 45% alpha would disappear entirely.
    #
    # Deliberately not amber: red and amber are the elevated-launch colours in the app, and a
    # brand mark sharing them would dilute a signal that has to stay unambiguous.
    if ($simple) {
        # One chevron below 32 px. Three at that size is four grey pixels and a smudge.
        $chevrons = New-Object 'object[]' 1
        $chevrons[0] = @(92, 128, 68, 46)          # x, centreY, width, halfHeight
        $colours = @(([System.Drawing.Color]::FromArgb(255, 56, 189, 248)))
        $stroke = 32
    }
    else {
        $chevrons = New-Object 'object[]' 3
        # halfHeight is 33, not 42. At 42 the upper arm of each chevron crosses the lower arm
        # of the one before it and the mark reads as a tangled zigzag rather than three steps.
        $chevrons[0] = @(50, 178, 52, 33)
        $chevrons[1] = @(102, 128, 52, 33)
        $chevrons[2] = @(154, 78, 52, 33)
        $colours = @(
            ([System.Drawing.Color]::FromArgb(255, 34, 211, 238)),   # cyan   — tier 0
            ([System.Drawing.Color]::FromArgb(255, 59, 130, 246)),   # blue   — tier 1
            ([System.Drawing.Color]::FromArgb(255, 129, 118, 245)))  # indigo — tier 2
        $stroke = 26
    }

    for ($i = 0; $i -lt $chevrons.Count; $i++) {
        $c = $chevrons[$i]
        $x = $c[0] * $s; $cy = $c[1] * $s; $w = $c[2] * $s; $hh = $c[3] * $s

        $pen = New-Object System.Drawing.Pen($colours[$i], ($stroke * $s))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

        $points = New-Object 'System.Drawing.PointF[]' 3
        $points[0] = New-Object System.Drawing.PointF($x, ($cy - $hh))
        $points[1] = New-Object System.Drawing.PointF(($x + $w), $cy)
        $points[2] = New-Object System.Drawing.PointF($x, ($cy + $hh))
        $g.DrawLines($pen, $points)
        $pen.Dispose()
    }

    $plateBrush.Dispose()
    $plate.Dispose()
    $g.Dispose()
    return $bmp
}

# ---------------------------------------------------------------------------
# Assemble the .ico from PNG entries — supported since Vista and far smaller than BMP.
# ---------------------------------------------------------------------------
$pngBlobs = @()
foreach ($size in $sizes) {
    $bmp = New-RolloutLoudBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBlobs += , @{ Size = $size; Bytes = $ms.ToArray() }

    if ($size -eq 256) { [System.IO.File]::WriteAllBytes($pngPath, $ms.ToArray()) }

    $ms.Dispose(); $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)

$w.Write([UInt16]0)                    # reserved
$w.Write([UInt16]1)                    # type 1 = icon
$w.Write([UInt16]$pngBlobs.Count)

$offset = 6 + (16 * $pngBlobs.Count)
foreach ($blob in $pngBlobs) {
    $dim = if ($blob.Size -ge 256) { 0 } else { $blob.Size }
    $w.Write([Byte]$dim)               # width (0 means 256)
    $w.Write([Byte]$dim)               # height
    $w.Write([Byte]0)                  # palette colours
    $w.Write([Byte]0)                  # reserved
    $w.Write([UInt16]1)                # planes
    $w.Write([UInt16]32)               # bits per pixel
    $w.Write([UInt32]$blob.Bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $blob.Bytes.Length
}

foreach ($blob in $pngBlobs) { $w.Write($blob.Bytes) }

$w.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$w.Dispose(); $out.Dispose()

Write-Output "Generated: $icoPath ($($pngBlobs.Count) sizes: $($sizes -join ', '))"
Write-Output "Generated: $pngPath"

# The site in docs/ needs the same drawing. GitHub Pages only serves what is inside the published
# folder, so `../assets/` does not resolve and the copy has to live there — and it is written
# from here rather than by hand, so the favicon can never drift from the application icon.
#
# This copy is what puts the icon in the catalogues. winstall.app runs get-website-favicon
# against the manifest's PackageUrl and keeps the highest-resolution favicon it finds, so the
# PackageUrl has to point at this site rather than at the repository page.
$docsDir = Join-Path (Split-Path -Parent $outDir) 'docs'
if (Test-Path $docsDir) {
    Copy-Item $icoPath (Join-Path $docsDir 'favicon.ico') -Force
    Copy-Item $pngPath (Join-Path $docsDir 'rolloutloud-256.png') -Force
    Write-Output "Copied:    $docsDir\favicon.ico"
    Write-Output "Copied:    $docsDir\rolloutloud-256.png"
}
