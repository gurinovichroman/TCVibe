<#
.SYNOPSIS
    Rebuild the app icon and the title bar logo from the original artwork.

.DESCRIPTION
    Reads src\TCV.App\Assets\Logo\tcv-logo.png (the artwork as delivered, kept unchanged) and
    writes beside it:
      tcv.ico            the exe's icon, 16-256 px: Explorer, taskbar, Alt+Tab, dialog captions,
                         and through vpk the portable stub and Update.exe
      tcv-logo-128.png       the mark in the main window's own title bar, light theme
      tcv-logo-128-dark.png  the same with a light blue rim, dark theme: the dark title bar
                             (#161719) swallows the logo's navy half, contrast about 1:1. The logo
                             is inset by the rim, or the rim would be cut off where it meets the frame
    Image generators hand out "transparent" PNGs with the checkerboard painted in: an opaque light
    grey backdrop reachable from the border is cut away, with the one-pixel fringe next to it.
    The artwork is then trimmed to its shape and centred on a square transparent canvas. Its alpha
    is snapped, so a shape that is 252-254 inside does not let the taskbar show through and faint
    specks around it go.

    Run it after replacing tcv-logo.png and commit the results; the build does not run it.

.EXAMPLE
    .\make-icons.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$Root    = Split-Path $PSScriptRoot -Parent
$LogoDir = Join-Path $Root "src\TCV.App\Assets\Logo"
$Source  = Join-Path $LogoDir "tcv-logo.png"

# Sizes Windows asks an icon for at 100-200 % scaling (16/20/24/32 small, 32/40/48/64 taskbar,
# 48-256 Explorer). Below 256 the frames are plain 32-bit DIBs, 256 is PNG: what every reader takes.
$IconSizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

# The logo's own light blue; 5 px of the 128 px frame is about 1 px in the 23 px title bar mark.
$RimColor  = [System.Drawing.Color]::FromArgb(170, 90, 150, 255)
$RimRadius = 5

# A million pixels is minutes in a PowerShell loop. Plain C# 5 over a byte[] of BGRA rows, so
# Windows PowerShell 5.1 compiles it too.
Add-Type -TypeDefinition @"
public static class TcvLogoBackdrop
{
    // Opaque, light and grey: white, or a square of a painted checkerboard. The logo's own whites
    // are not reached, they lie inside its shape.
    private static bool IsBackdrop(byte[] p, int i)
    {
        int b = p[i * 4], g = p[i * 4 + 1], r = p[i * 4 + 2], a = p[i * 4 + 3];
        int max = System.Math.Max(r, System.Math.Max(g, b));
        int min = System.Math.Min(r, System.Math.Min(g, b));
        return a == 255 && min >= 200 && max - min <= 24;
    }

    private static int Push(byte[] p, bool[] backdrop, int[] stack, int top, int i)
    {
        if (!backdrop[i] && IsBackdrop(p, i))
        {
            backdrop[i] = true;
            stack[top++] = i;
        }
        return top;
    }

    // Clears the backdrop reachable from the border and the fringe pixel next to it (half backdrop,
    // half logo, it would draw a light seam). Returns the number of pixels cleared.
    public static int Cut(byte[] p, int width, int height)
    {
        int n = width * height;
        bool[] backdrop = new bool[n];
        int[] stack = new int[n];
        int top = 0;
        for (int x = 0; x < width; x++)
        {
            top = Push(p, backdrop, stack, top, x);
            top = Push(p, backdrop, stack, top, n - width + x);
        }
        for (int y = 0; y < height; y++)
        {
            top = Push(p, backdrop, stack, top, y * width);
            top = Push(p, backdrop, stack, top, y * width + width - 1);
        }
        while (top > 0)
        {
            int i = stack[--top];
            int x = i % width;
            if (x > 0) top = Push(p, backdrop, stack, top, i - 1);
            if (x < width - 1) top = Push(p, backdrop, stack, top, i + 1);
            if (i >= width) top = Push(p, backdrop, stack, top, i - width);
            if (i < n - width) top = Push(p, backdrop, stack, top, i + width);
        }

        int cleared = 0;
        for (int i = 0; i < n; i++)
        {
            int x = i % width;
            bool clear = backdrop[i]
                || (x > 0 && backdrop[i - 1]) || (x < width - 1 && backdrop[i + 1])
                || (i >= width && backdrop[i - width]) || (i < n - width && backdrop[i + width]);
            if (clear && p[i * 4 + 3] != 0)
            {
                p[i * 4] = 0; p[i * 4 + 1] = 0; p[i * 4 + 2] = 0; p[i * 4 + 3] = 0;
                cleared++;
            }
        }
        return cleared;
    }

    // x, y, width, height of what survives the alpha snap (alpha 12 and up); null if nothing does.
    public static int[] Bounds(byte[] p, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (p[(y * width + x) * 4 + 3] < 12) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        return right < 0 ? null : new[] { left, top, right - left + 1, bottom - top + 1 };
    }
}
"@

# The artwork as 32-bit ARGB (an RGB source gets its alpha here), backdrop cut, trimmed to the shape.
function Read-Artwork([string]$path) {
    $loaded = New-Object System.Drawing.Bitmap($path)
    try {
        $w = $loaded.Width
        $h = $loaded.Height
        $argb = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($argb)
        try {
            $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $g.DrawImage($loaded, 0, 0, $w, $h)
        }
        finally { $g.Dispose() }
    }
    finally { $loaded.Dispose() }

    $whole = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $argb.LockBits($whole, [System.Drawing.Imaging.ImageLockMode]::ReadWrite,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        # 32 bpp rows are never padded: the stride is the width * 4.
        $pixels = New-Object byte[] ($data.Stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
        $cleared = [TcvLogoBackdrop]::Cut($pixels, $w, $h)
        $bounds = [TcvLogoBackdrop]::Bounds($pixels, $w, $h)
        [System.Runtime.InteropServices.Marshal]::Copy($pixels, 0, $data.Scan0, $pixels.Length)
    }
    finally { $argb.UnlockBits($data) }

    if ($null -eq $bounds) { $argb.Dispose(); throw "$path is all backdrop: nothing left to make an icon of." }
    if ($cleared -gt 0) { Write-Host "  backdrop cut: $cleared px" -ForegroundColor DarkGray }

    $shape = New-Object System.Drawing.Rectangle($bounds[0], $bounds[1], $bounds[2], $bounds[3])
    try { $argb.Clone($shape, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) }
    finally { $argb.Dispose() }
}

# a' = 1.06 a - 12: 252 and up becomes opaque, 11 and below transparent, the edges barely move.
function New-AlphaSnap {
    $m = New-Object System.Drawing.Imaging.ColorMatrix
    $m.Matrix33 = 1.06
    $m.Matrix43 = -12 / 255
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
    $attributes.SetColorMatrix($m)
    # Without it the resampler reads past the edge and leaves a grey seam on transparent sides.
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $attributes
}

# $inset keeps that many pixels clear on every side (room for a rim drawn around the shape later).
function New-Frame([System.Drawing.Bitmap]$art, [int]$size, [int]$inset = 0) {
    $frame = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($frame)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

        $scale = ($size - 2 * $inset) / [Math]::Max($art.Width, $art.Height)
        $w = $art.Width * $scale
        $h = $art.Height * $scale
        $dest = New-Object System.Drawing.RectangleF((($size - $w) / 2), (($size - $h) / 2), $w, $h)
        $snap = New-AlphaSnap
        try {
            $g.DrawImage($art, [System.Drawing.Rectangle]::Round($dest), 0, 0, $art.Width, $art.Height,
                [System.Drawing.GraphicsUnit]::Pixel, $snap)
        }
        finally { $snap.Dispose() }
    }
    finally { $g.Dispose() }
    $frame
}

# GDI+ has no dilation: the silhouette, recoloured, is stamped around a circle of the rim's radius,
# laid under the frame at the rim's alpha.
function New-RimmedFrame([System.Drawing.Bitmap]$frame, [System.Drawing.Color]$rim, [float]$radius) {
    $size = $frame.Width
    $whole = New-Object System.Drawing.RectangleF(0, 0, $size, $size)

    $tint = New-Object System.Drawing.Imaging.ColorMatrix
    $tint.Matrix00 = 0; $tint.Matrix11 = 0; $tint.Matrix22 = 0
    $tint.Matrix40 = $rim.R / 255; $tint.Matrix41 = $rim.G / 255; $tint.Matrix42 = $rim.B / 255
    $tinted = New-Object System.Drawing.Imaging.ImageAttributes
    $tinted.SetColorMatrix($tint)

    $ring = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($ring)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $steps = 32
        for ($i = 0; $i -lt $steps; $i++) {
            $dx = $radius * [Math]::Cos(2 * [Math]::PI * $i / $steps)
            $dy = $radius * [Math]::Sin(2 * [Math]::PI * $i / $steps)
            $corners = [System.Drawing.PointF[]]@(
                (New-Object System.Drawing.PointF($dx, $dy)),
                (New-Object System.Drawing.PointF(($dx + $size), $dy)),
                (New-Object System.Drawing.PointF($dx, ($dy + $size))))
            $g.DrawImage($frame, $corners, $whole, [System.Drawing.GraphicsUnit]::Pixel, $tinted)
        }
    }
    finally { $g.Dispose(); $tinted.Dispose() }

    $fade = New-Object System.Drawing.Imaging.ColorMatrix
    $fade.Matrix33 = $rim.A / 255
    $faded = New-Object System.Drawing.Imaging.ImageAttributes
    $faded.SetColorMatrix($fade)

    $rimmed = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($rimmed)
    try {
        $g.DrawImage($ring, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)), 0, 0, $size, $size,
            [System.Drawing.GraphicsUnit]::Pixel, $faded)
        $g.DrawImage($frame, 0, 0, $size, $size)
    }
    finally { $g.Dispose(); $faded.Dispose(); $ring.Dispose() }
    $rimmed
}

function Get-PngBytes([System.Drawing.Bitmap]$frame) {
    $ms = New-Object System.IO.MemoryStream
    try {
        $frame.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    }
    finally { $ms.Dispose() }
}

# BITMAPINFOHEADER (height doubled for the AND mask), BGRA rows bottom-up, then the AND mask:
# 1 bit per pixel, rows padded to 4 bytes, set where the pixel is fully transparent.
function Get-DibBytes([System.Drawing.Bitmap]$frame) {
    $size = $frame.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $frame.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $size * 4
        $pixels = New-Object byte[] ($stride * $size)
        for ($y = 0; $y -lt $size; $y++) {
            [System.Runtime.InteropServices.Marshal]::Copy(
                [IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $pixels, $y * $stride, $stride)
        }
    }
    finally { $frame.UnlockBits($data) }

    $maskStride = [int]([Math]::Ceiling($size / 32) * 4)
    $mask = New-Object byte[] ($maskStride * $size)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    try {
        $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2))
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
        $bw.Write([int]($pixels.Length + $mask.Length))
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            $bw.Write($pixels, $y * $stride, $stride)
            $maskRow = ($size - 1 - $y) * $maskStride
            for ($x = 0; $x -lt $size; $x++) {
                if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) {
                    $mask[$maskRow + ($x -shr 3)] = $mask[$maskRow + ($x -shr 3)] -bor (0x80 -shr ($x -band 7))
                }
            }
        }
        $bw.Write($mask)
        $bw.Flush()
        , $ms.ToArray()
    }
    finally { $bw.Dispose() }
}

function Write-Icon([System.Drawing.Bitmap]$art, [int[]]$sizes, [string]$path) {
    # @() keeps a list of byte arrays even for one size: bare, a single frame would be the byte[] itself.
    $images = @(foreach ($size in $sizes) {
        $frame = New-Frame $art $size
        try {
            if ($size -ge 256) { , (Get-PngBytes $frame) } else { , (Get-DibBytes $frame) }
        }
        finally { $frame.Dispose() }
    })

    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        # ICONDIR, then one ICONDIRENTRY per frame; 0 in a size byte means 256.
        $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $side = [byte]($sizes[$i] % 256)
            $bw.Write($side); $bw.Write($side); $bw.Write([byte]0); $bw.Write([byte]0)
            $bw.Write([int16]1); $bw.Write([int16]32)
            $bw.Write([int]$images[$i].Length); $bw.Write([int]$offset)
            $offset += $images[$i].Length
        }
        foreach ($image in $images) { $bw.Write($image) }
    }
    finally { $bw.Dispose() }
}

$art = Read-Artwork $Source
try {
    $ico = Join-Path $LogoDir "tcv.ico"
    Write-Icon $art $IconSizes $ico
    Write-Host "  $ico ($($IconSizes -join ', ') px)" -ForegroundColor Green

    $png = Join-Path $LogoDir "tcv-logo-128.png"
    $darkPng = Join-Path $LogoDir "tcv-logo-128-dark.png"
    $frame = New-Frame $art 128
    try { $frame.Save($png, [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $frame.Dispose() }

    # The logo spans the frame edge to edge along its long side: drawn full size, its rim would fall
    # outside the canvas there, and the navy half's outer edge (the one the rim is for) would stay bare.
    $inner = New-Frame $art 128 $RimRadius
    try {
        $rimmed = New-RimmedFrame $inner $RimColor $RimRadius
        try { $rimmed.Save($darkPng, [System.Drawing.Imaging.ImageFormat]::Png) }
        finally { $rimmed.Dispose() }
    }
    finally { $inner.Dispose() }
    Write-Host "  $png" -ForegroundColor Green
    Write-Host "  $darkPng" -ForegroundColor Green
}
finally { $art.Dispose() }
