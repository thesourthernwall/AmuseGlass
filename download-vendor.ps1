# Download the WebView2 SDK (NuGet package) used by the overlay.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = '1.0.4314.35'
$download = Join-Path $root '_download'
$vendor = Join-Path $root 'vendor\webview2'

New-Item -ItemType Directory -Force -Path $download | Out-Null

if (-not (Test-Path (Join-Path $vendor 'lib\net462\Microsoft.Web.WebView2.Core.dll'))) {
    $nupkg = Join-Path $download "microsoft.web.webview2.$version.nupkg"
    $zip = Join-Path $download "webview2.$version.zip"

    Write-Host "Downloading WebView2 SDK $version ..."
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg" -OutFile $nupkg
    Copy-Item $nupkg $zip -Force
    Expand-Archive $zip -DestinationPath $vendor -Force
}

Write-Host 'WebView2 SDK ready.' -ForegroundColor Green
