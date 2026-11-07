$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$lib  = Join-Path $root 'vendor\webview2\lib\net462'
$native = Join-Path $root 'vendor\webview2\runtimes\win-x64\native'
$out  = Join-Path $root 'dist\AmuseOverlay'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
if (-not (Test-Path $lib)) { throw "WebView2 SDK not found. Run download-vendor.ps1 first." }

New-Item -ItemType Directory -Force -Path $out | Out-Null

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/optimize+'
    '/langversion:5'
    "/out:$out\AmuseOverlay.exe"
    "/r:$fw\System.dll"
    "/r:$fw\System.Core.dll"
    "/r:$fw\System.Drawing.dll"
    "/r:$fw\System.Windows.Forms.dll"
    "/r:$fw\System.Web.Extensions.dll"
    "/r:$lib\Microsoft.Web.WebView2.Core.dll"
    "/r:$lib\Microsoft.Web.WebView2.WinForms.dll"
) + $sources

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

Copy-Item "$lib\Microsoft.Web.WebView2.Core.dll"     $out -Force
Copy-Item "$lib\Microsoft.Web.WebView2.WinForms.dll"  $out -Force
Copy-Item "$native\WebView2Loader.dll"                $out -Force

Write-Host ''
Write-Host "Built: $out\AmuseOverlay.exe" -ForegroundColor Green
Get-ChildItem $out | Select-Object Name, Length | Format-Table -AutoSize
