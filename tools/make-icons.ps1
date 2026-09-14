<#
    Generates the ribbon icons as 32-bit ICO files with a real alpha channel.

    Run once; the results are checked in under NwTagger\Images and copied next
    to the plugin DLL at build time.

    System.Drawing's Icon.FromHandle(...).Save() loses the alpha channel and
    leaves black fringing on the ribbon, so the ICO is written by hand here:
    ICONDIR -> ICONDIRENTRY -> BITMAPINFOHEADER -> bottom-up BGRA -> AND mask.
#>

[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'NwTagger\Images' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Write-Ico {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    # Bottom-up BGRA pixel data.
    $xor = New-Object byte[] ($w * $h * 4)
    for ($y = 0; $y -lt $h; $y++) {
        $srcY = $h - 1 - $y
        for ($x = 0; $x -lt $w; $x++) {
            $c = $Bitmap.GetPixel($x, $srcY)
            $i = ($y * $w + $x) * 4
            $xor[$i]     = $c.B
            $xor[$i + 1] = $c.G
            $xor[$i + 2] = $c.R
            $xor[$i + 3] = $c.A
        }
    }

    # 1bpp AND mask, rows padded to 4 bytes. Zeroed: alpha does the masking.
    $maskRow = [int][Math]::Floor((($w + 31) / 32)) * 4
    $andMask = New-Object byte[] ($maskRow * $h)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $imageSize = 40 + $xor.Length + $andMask.Length

    # ICONDIR
    $bw.Write([uint16]0)          # reserved
    $bw.Write([uint16]1)          # type: icon
    $bw.Write([uint16]1)          # image count

    # ICONDIRENTRY
    $bw.Write([byte]$(if ($w -ge 256) { 0 } else { $w }))
    $bw.Write([byte]$(if ($h -ge 256) { 0 } else { $h }))
    $bw.Write([byte]0)            # palette colours
    $bw.Write([byte]0)            # reserved
    $bw.Write([uint16]1)          # colour planes
    $bw.Write([uint16]32)         # bits per pixel
    $bw.Write([uint32]$imageSize)
    $bw.Write([uint32]22)         # offset: 6 + 16

    # BITMAPINFOHEADER - height is doubled to cover XOR + AND
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)          # BI_RGB
    $bw.Write([uint32]($xor.Length + $andMask.Length))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    $bw.Write($xor)
    $bw.Write($andMask)
    $bw.Flush()

    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

function New-Glyph {
    param(
        [int]$Size,
        [string]$Text,
        [System.Drawing.Color]$Fill,
        [System.Drawing.Color]$TextColor
    )

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($Size * 0.08))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($Size - 2 * $pad), ($Size - 2 * $pad))

    $brush = New-Object System.Drawing.SolidBrush($Fill)
    $g.FillEllipse($brush, $rect)
    $brush.Dispose()

    $fontSize = [float]($Size * 0.52)
    $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'
    $fmt.LineAlignment = 'Center'

    $textBrush = New-Object System.Drawing.SolidBrush($TextColor)
    $layout = New-Object System.Drawing.RectangleF(0, 0, $Size, $Size)
    $g.DrawString($Text, $font, $textBrush, $layout, $fmt)

    $textBrush.Dispose(); $font.Dispose(); $fmt.Dispose(); $g.Dispose()
    return $bmp
}

function Get-TrimmedBounds {
    param([System.Drawing.Bitmap]$Bitmap, [int]$AlphaThreshold = 8)

    $minX = $Bitmap.Width; $minY = $Bitmap.Height; $maxX = -1; $maxY = -1

    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -le $AlphaThreshold) { continue }
            if ($x -lt $minX) { $minX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }

    # Fully opaque image: nothing to trim.
    if ($maxX -lt 0) { return New-Object System.Drawing.Rectangle(0, 0, $Bitmap.Width, $Bitmap.Height) }

    return New-Object System.Drawing.Rectangle($minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
}

function New-LogoIcon {
    param([string]$SourcePath, [int]$Size)

    $source = [System.Drawing.Bitmap]::FromFile($SourcePath)
    try {
        # Trim the transparent margin so the artwork fills the icon rather than
        # shrinking into the middle of it. The threshold ignores the faint halo
        # around the artwork, which would otherwise defeat the trim.
        $bounds = Get-TrimmedBounds -Bitmap $source -AlphaThreshold 24

        # Fit inside the square preserving aspect - the artwork is wider than it
        # is tall, and stretching it to a square visibly distorts the lettering.
        $scale = [Math]::Min($Size / $bounds.Width, $Size / $bounds.Height)
        $w = [int][Math]::Round($bounds.Width * $scale)
        $h = [int][Math]::Round($bounds.Height * $scale)
        $x = [int][Math]::Round(($Size - $w) / 2)
        $y = [int][Math]::Round(($Size - $h) / 2)

        $target = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($target)
        $g.CompositingMode = 'SourceOver'
        $g.CompositingQuality = 'HighQuality'
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.SmoothingMode = 'AntiAlias'
        $g.PixelOffsetMode = 'HighQuality'
        $g.Clear([System.Drawing.Color]::Transparent)

        $dest = New-Object System.Drawing.Rectangle($x, $y, $w, $h)
        $g.DrawImage($source, $dest, $bounds.X, $bounds.Y, $bounds.Width, $bounds.Height, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()

        return $target
    }
    finally {
        $source.Dispose()
    }
}

# The product logo, downscaled for the ribbon.
$logoSource = Join-Path $OutputDirectory 'logo.png'
if (Test-Path $logoSource) {
    foreach ($size in 16, 32) {
        $bmp = New-LogoIcon -SourcePath $logoSource -Size $size
        Write-Ico -Bitmap $bmp -Path (Join-Path $OutputDirectory "logo_$size.ico")
        $bmp.Dispose()
    }
    Write-Host "Generated logo_16.ico and logo_32.ico from logo.png" -ForegroundColor Green
} else {
    Write-Warning "logo.png not found in $OutputDirectory - skipping the logo icons."
}

$white = [System.Drawing.Color]::White

$icons = @(
    @{ name = 'tag';      text = 'T';  fill = [System.Drawing.Color]::FromArgb(10, 102, 194) },
    @{ name = 'enable';   text = 'E';  fill = [System.Drawing.Color]::FromArgb(0, 150, 70)   },
    @{ name = 'disable';  text = 'D';  fill = [System.Drawing.Color]::FromArgb(200, 40, 40)  },
    @{ name = 'panel';    text = 'P';  fill = [System.Drawing.Color]::FromArgb(70, 90, 115)  },
    @{ name = 'export';   text = 'X';  fill = [System.Drawing.Color]::FromArgb(16, 124, 65)  },
    @{ name = 'clear';    text = 'C';  fill = [System.Drawing.Color]::FromArgb(120, 120, 128) },
    @{ name = 'linkedin'; text = 'in'; fill = [System.Drawing.Color]::FromArgb(10, 102, 194) }
)

foreach ($icon in $icons) {
    foreach ($size in 16, 32) {
        $bmp = New-Glyph -Size $size -Text $icon.text -Fill $icon.fill -TextColor $white
        $path = Join-Path $OutputDirectory ("{0}_{1}.ico" -f $icon.name, $size)
        Write-Ico -Bitmap $bmp -Path $path
        $bmp.Dispose()
    }
}

Write-Host "Wrote $($icons.Count * 2) icons to $OutputDirectory" -ForegroundColor Green

# Verify each one round-trips through the Icon loader.
$bad = @()
Get-ChildItem $OutputDirectory -Filter *.ico | ForEach-Object {
    try {
        $ico = New-Object System.Drawing.Icon($_.FullName)
        if ($ico.Width -eq 0) { $bad += $_.Name }
        $ico.Dispose()
    } catch {
        $bad += "$($_.Name): $($_.Exception.Message)"
    }
}

if ($bad.Count -gt 0) {
    Write-Warning ("Invalid icons: " + ($bad -join ', '))
    exit 1
}

Write-Host 'All icons load correctly.' -ForegroundColor Green
