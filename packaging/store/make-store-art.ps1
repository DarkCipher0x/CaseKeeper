<#
    Draws the optional Microsoft Store listing images from the app icon (assets\casekeeper-icon.png).

      powershell -ExecutionPolicy Bypass -File packaging\store\make-store-art.ps1

    Output goes to assets\store-art. Each file is named after the Partner Center box it belongs in
    (Store listings → Store logos / Store display images / Super hero art). The icon is 512×512, so
    where Partner Center accepts two sizes the smaller one is used and the icon is never enlarged
    past its own size: that keeps it sharp.

    Only the brand is shown: the CaseKeeper name, the icon and a one-line description. No people,
    no case material.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$out = Join-Path $root 'assets\store-art'
New-Item -ItemType Directory -Force $out | Out-Null
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\casekeeper-icon.png'))

# Brand colours, as in the app's dark theme
$bgTop    = [System.Drawing.Color]::FromArgb(255, 16, 42, 46)
$bgBottom = [System.Drawing.Color]::FromArgb(255, 9, 20, 23)
$teal     = [System.Drawing.Color]::FromArgb(255, 61, 214, 200)
$text     = [System.Drawing.Color]::FromArgb(255, 236, 244, 244)
$dim      = [System.Drawing.Color]::FromArgb(255, 150, 178, 180)

function New-Canvas([int]$w, [int]$h, [bool]$background) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    if ($background) {
        $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $bgTop, $bgBottom, 90
        $g.FillRectangle($brush, $rect); $brush.Dispose()
        # A soft teal glow behind where the icon sits
        $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
        $gw = [int]($w * 0.9); $gh = [int]($h * 0.6)
        $glow.AddEllipse([int](($w - $gw) / 2), [int]($h * 0.05), $gw, $gh)
        $pb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
        $pb.CenterColor = [System.Drawing.Color]::FromArgb(70, $teal)
        $pb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $teal))
        $g.FillPath($pb, $glow); $pb.Dispose(); $glow.Dispose()
    } else {
        $g.Clear([System.Drawing.Color]::Transparent)
    }
    return @($bmp, $g)
}

function Draw-Centered($g, [string]$s, [string]$font, [float]$size, [System.Drawing.FontStyle]$style, $color, [int]$w, [float]$y) {
    $f = New-Object System.Drawing.Font $font, $size, $style, ([System.Drawing.GraphicsUnit]::Pixel)
    $b = New-Object System.Drawing.SolidBrush $color
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString($s, $f, $b, (New-Object System.Drawing.RectangleF 0, $y, $w, ($size * 1.6)), $fmt)
    $f.Dispose(); $b.Dispose(); $fmt.Dispose()
}

function Save($bmp, $g, [string]$name) {
    $g.Dispose()
    $path = Join-Path $out $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "{0,-46} {1}" -f $name, ((Get-Item $path).Length.ToString('N0') + ' bytes')
}

$bold = [System.Drawing.FontStyle]::Bold
$regular = [System.Drawing.FontStyle]::Regular

# 9:16 Poster art, 720 x 1080: the main Store logo on Windows. Includes the title.
$c = New-Canvas 720 1080 $true; $g = $c[1]
$g.DrawImage($icon, 160, 190, 400, 400)
Draw-Centered $g 'CaseKeeper' 'Segoe UI Semibold' 84 $regular $text 720 650
Draw-Centered $g 'Family court documents,' 'Segoe UI' 36 $regular $dim 720 780
Draw-Centered $g 'organised and private' 'Segoe UI' 36 $regular $dim 720 828
Save $c[0] $g 'poster-art-9x16-720x1080.png'

# 1:1 Box art, 1080 x 1080. Includes the title.
$c = New-Canvas 1080 1080 $true; $g = $c[1]
$g.DrawImage($icon, 300, 170, 480, 480)
Draw-Centered $g 'CaseKeeper' 'Segoe UI Semibold' 104 $regular $text 1080 700
Draw-Centered $g 'Family court documents, organised and private' 'Segoe UI' 38 $regular $dim 1080 860
Save $c[0] $g 'box-art-1x1-1080x1080.png'

# 16:9 Super hero art, 1920 x 1080. Partner Center says it must NOT include the product's title.
$c = New-Canvas 1920 1080 $true; $g = $c[1]
$g.DrawImage($icon, 704, 284, 512, 512)
Save $c[0] $g 'super-hero-art-16x9-1920x1080.png'

# Store display images (1:1 app tile icons): the icon alone on a transparent background
foreach ($s in 300, 150, 71) {
    $c = New-Canvas $s $s $false; $g = $c[1]
    $g.DrawImage($icon, 0, 0, $s, $s)
    Save $c[0] $g "app-tile-icon-${s}x${s}.png"
}
$icon.Dispose()
