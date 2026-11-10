# Builds dist\ : Fedatather.exe + the two WebView2 SDK DLLs it needs (~1 MB total, portable).
# Needs only what ships with Windows 10/11: the .NET Framework 4.x C# compiler and the Edge WebView2 Runtime.
# The WebView2 SDK is fetched from nuget.org on the first build and cached in build\.

param([switch]$PublicRelease)

$ErrorActionPreference = 'Stop'
$WebView2Version = '1.0.4314.35'

$root   = $PSScriptRoot
$src    = Join-Path $root 'src'
$work   = Join-Path $root 'build'
$dist   = Join-Path $root 'dist'
$csc    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc (.NET Framework 4.x is required)." }
New-Item -ItemType Directory -Force $work, $dist | Out-Null

# --- WebView2 SDK ----------------------------------------------------------------
$pkg = Join-Path $work "webview2-$WebView2Version"
if (-not (Test-Path (Join-Path $pkg 'lib'))) {
    Write-Host "Downloading WebView2 SDK $WebView2Version ..."
    $zip = "$pkg.zip"
    Invoke-WebRequest -UseBasicParsing -OutFile $zip `
        "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$WebView2Version/microsoft.web.webview2.$WebView2Version.nupkg"
    Expand-Archive $zip -DestinationPath $pkg -Force
    Remove-Item $zip
}
$coreDll   = Join-Path $pkg 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$loaderDll = Join-Path $pkg 'runtimes\win-x64\native\WebView2Loader.dll'

Copy-Item $coreDll, $loaderDll $dist -Force
Copy-Item (Join-Path $pkg 'LICENSE.txt') (Join-Path $dist 'WEBVIEW2-LICENSE.txt') -Force
Copy-Item (Join-Path $pkg 'NOTICE.txt') (Join-Path $dist 'WEBVIEW2-NOTICE.txt') -Force
Copy-Item (Join-Path $root 'LICENSE') $dist -Force

# --- App icon: rendered from assets\fedatather.svg into a multi-size PNG-in-ICO ----------
# Handles the small SVG subset the icon uses: <rect rx> and <path> (absolute M/L/C/Z) with fill / stroke.
Add-Type -AssemblyName System.Drawing
$svgFile = Join-Path $root 'assets\fedatather.svg'
$svg = [xml](Get-Content $svgFile -Raw)
$viewBox = [single[]]($svg.svg.viewBox.Trim() -split '[\s,]+')

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) { $p.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h))); return , $p }
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return , $p
}
function New-Point($tokens, [int]$i) { New-Object System.Drawing.PointF([single]$tokens[$i], [single]$tokens[$i + 1]) }
function New-SvgPath([string]$d) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $t = @([regex]::Matches($d, '[A-Za-z]|-?\d*\.?\d+(?:[eE][-+]?\d+)?') | ForEach-Object { $_.Value })
    $i = 0; $cmd = ''; $cur = New-Object System.Drawing.PointF(0, 0); $start = $cur
    while ($i -lt $t.Count) {
        if ($t[$i] -match '^[A-Za-z]$') { $cmd = $t[$i]; $i++ }
        switch -CaseSensitive ($cmd) {
            'M' { $cur = New-Point $t $i; $start = $cur; $i += 2; $p.StartFigure(); $cmd = 'L' }
            'L' { $pt = New-Point $t $i; $i += 2; $p.AddLine($cur, $pt); $cur = $pt }
            'C' { $c1 = New-Point $t $i; $c2 = New-Point $t ($i + 2); $pt = New-Point $t ($i + 4); $i += 6
                  $p.AddBezier($cur, $c1, $c2, $pt); $cur = $pt }
            'Z' { $p.CloseFigure(); $cur = $start; $cmd = '' }
            default { throw "Unsupported path data '$cmd' in $svgFile (use absolute M, L, C, Z)." }
        }
    }
    return , $p
}
function Get-Color($el, [string]$attr, [string]$default) {
    $v = if ($el.HasAttribute($attr)) { $el.GetAttribute($attr) } else { $default }
    if ($v -eq 'none') { return $null }
    return [System.Drawing.ColorTranslator]::FromHtml($v)
}
function New-IconPng([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($n / $viewBox[2], $n / $viewBox[3])
    $g.TranslateTransform(-$viewBox[0], -$viewBox[1])

    foreach ($el in $svg.svg.ChildNodes) {
        $shape = $null
        if ($el.LocalName -eq 'rect') {
            $shape = New-RoundRect ([single]$el.GetAttribute('x')) ([single]$el.GetAttribute('y')) `
                ([single]$el.GetAttribute('width')) ([single]$el.GetAttribute('height')) ([single]$el.GetAttribute('rx'))
        }
        elseif ($el.LocalName -eq 'path') { $shape = New-SvgPath $el.GetAttribute('d') }
        if (-not $shape) { continue }

        $fill = Get-Color $el 'fill' '#000000'
        if ($fill) { $g.FillPath((New-Object System.Drawing.SolidBrush($fill)), $shape) }
        $stroke = Get-Color $el 'stroke' 'none'
        if ($stroke) {
            $w = if ($el.HasAttribute('stroke-width')) { [single]$el.GetAttribute('stroke-width') } else { [single]1 }
            $pen = New-Object System.Drawing.Pen($stroke, $w)
            if ($el.GetAttribute('stroke-linecap') -eq 'round') { $pen.StartCap = 'Round'; $pen.EndCap = 'Round' }
            $g.DrawPath($pen, $shape)
        }
    }

    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}
$ico = Join-Path $work 'app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = @(); foreach ($s in $sizes) { $images += , (New-IconPng $s) }
$fs = [IO.File]::Create($ico)
$bw = New-Object IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i] % 256   # 0 means 256
    $bw.Write([byte]$s); $bw.Write([byte]$s); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Dispose()

# Public builds always use the original demo report. The personal override is never required.
$bossImage = Join-Path $root 'assets\boss_public.jpg'
$personalBossImage = Join-Path $root 'assets\boss_here.jpg'
if (-not $PublicRelease -and (Test-Path $personalBossImage)) { $bossImage = $personalBossImage }

# --- Compile ------------------------------------------------------------------------
$exe = Join-Path $dist 'Fedatather.exe'
$sources = Get-ChildItem (Join-Path $src '*.cs') | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /platform:x64 /optimize+ /langversion:5 `
    "/out:$exe" "/win32icon:$ico" "/win32manifest:$(Join-Path $src 'app.manifest')" `
    "/reference:$coreDll" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    "/resource:$bossImage,Fedatather.BossImage" $sources
if ($LASTEXITCODE -ne 0) { throw "Compilation failed." }

$kb = [Math]::Round(((Get-ChildItem $dist -File | Measure-Object Length -Sum).Sum) / 1KB)
Write-Host "Built $dist ($kb KB total)"
