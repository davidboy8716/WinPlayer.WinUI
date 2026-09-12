# 从一张正方形 PNG 生成标准多尺寸 ICO（用于隐私模式图标 player.b.ico）
#
# 为什么不用现成工具：本机没有 ImageMagick/ffmpeg，而 .NET 自带的 System.Drawing 足够。
# 帧格式遵循 Windows 惯例：
#   - 16/20/24/32/40/48  → 32bpp DIB（GDI/GDI+ 与老外壳路径都能读）
#   - 64/96/128/256      → PNG 压缩帧（Vista+ 支持，体积小）
#
# 用法（在任意 PowerShell 7 / Windows PowerShell 中执行）：
#   pwsh -File docs\tools\build-privacy-icon.ps1
#   pwsh -File docs\tools\build-privacy-icon.ps1 -Source Images\player.b.png -Target Images\player.b.ico
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\..\Images\player.b.png'),
    [string]$Target = (Join-Path $PSScriptRoot '..\..\Images\player.b.ico'),
    [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
)

Add-Type -AssemblyName System.Drawing

function New-ScaledBitmap([System.Drawing.Image]$source, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.DrawImage($source, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
    } finally { $g.Dispose() }
    return $bmp
}

# 32bpp DIB 帧：BITMAPINFOHEADER（biHeight = 2×高，为 AND 掩码预留）+ 自下而上 BGRA 行 + AND 掩码
function ConvertTo-DibFrame([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $raw = New-Object byte[] ($data.Stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
        $stride = $data.Stride
    } finally { $bmp.UnlockBits($data) }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4)); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($raw, $y * $stride, $w * 4) }
    $maskStride = [int]([math]::Ceiling($w / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskStride * $h)), 0, $maskStride * $h)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$bytes
}

function ConvertTo-PngFrame([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

if (-not (Test-Path -LiteralPath $Source)) { throw "源图不存在: $Source" }
$src = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
Write-Output ("源图: {0} ({1}x{2})" -f (Resolve-Path -LiteralPath $Source).Path, $src.Width, $src.Height)

$frames = @()
foreach ($size in $Sizes) {
    $bmp = New-ScaledBitmap $src $size
    try {
        if ($size -le 48) { $bytes = ConvertTo-DibFrame $bmp; $kind = 'DIB' }
        else              { $bytes = ConvertTo-PngFrame $bmp; $kind = 'PNG' }
    } finally { $bmp.Dispose() }
    $frames += [pscustomobject]@{ Size = $size; Kind = $kind; Bytes = $bytes }
    Write-Output ("  {0,3}x{0,-3} {1}  {2,7:N0} 字节" -f $size, $kind, $bytes.Length)
}
$src.Dispose()

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    # 256 在 ICO 目录里用 0 表示；注意不能用 (if ...) 表达式，该语法在 PowerShell 中不可用
    $dim = 0
    if ($f.Size -lt 256) { $dim = $f.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$f.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes, 0, $f.Bytes.Length) }
$bw.Flush()
$ico = $ms.ToArray()
$bw.Dispose(); $ms.Dispose()

$targetPath = if ([System.IO.Path]::IsPathRooted($Target)) { $Target }
              else { Join-Path (Get-Location).Path $Target }
[System.IO.File]::WriteAllBytes($targetPath, $ico)
Write-Output ("`n写出: {0}" -f $targetPath)
Write-Output ("大小: {0:N0} 字节" -f $ico.Length)
