# Compiles and runs real WebView2 integration checks against isolated temporary data.
# Requires the SDK cached by build.ps1 and the installed WebView2 Runtime.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'build\tab-tests'
New-Item -ItemType Directory -Force $out | Out-Null
$core = Join-Path $root 'dist\Microsoft.Web.WebView2.Core.dll'
Copy-Item $core, (Join-Path $root 'dist\WebView2Loader.dll') $out -Force
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem (Join-Path $root 'src\*.cs') | Where-Object { $_.Name -ne 'Program.cs' } | ForEach-Object { $_.FullName })
$sources += Join-Path $PSScriptRoot 'TabTests.cs'
$exe = Join-Path $out 'TabTests.exe'
& $csc /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" "/reference:$core" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/resource:$(Join-Path $root 'assets\boss_public.jpg'),Fedatather.BossImage" $sources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Tab tests failed.' }
