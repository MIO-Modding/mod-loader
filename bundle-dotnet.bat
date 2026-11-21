@echo off
setlocal
rem PowerShell 7 (e.g. a GitHub Actions pwsh step) passes on its module path, which makes Windows
rem PowerShell load PowerShell 7's modules and fail. Without it, Windows PowerShell uses its defaults.
set "PSModulePath="
rem Adds the .NET runtime to the packaged loader, the way releases ship it.
rem Run it after building (once package.bat has created output\). Downloads are kept
rem in %TEMP%, so running it again after a rebuild doesn't download again.
rem
rem Usage: bundle-dotnet.bat [destination]
rem   destination  Folder to put the runtime in. Defaults to output\mio-mod-loader\dotnet;
rem                pass your game's mio-mod-loader\dotnet folder to install it there directly.

rem The .NET version to ship (the latest patch of it is used).
set "DOTNET_CHANNEL=8.0"

set "DEST=%~1"
if "%DEST%"=="" set "DEST=%~dp0output\mio-mod-loader\dotnet"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop';" ^
  "$ProgressPreference = 'SilentlyContinue';" ^
  "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12;" ^
  "$info = Invoke-RestMethod ('https://builds.dotnet.microsoft.com/dotnet/release-metadata/' + $env:DOTNET_CHANNEL + '/releases.json');" ^
  "$version = $info.'latest-runtime';" ^
  "$release = $info.releases | Where-Object { $_.runtime.version -eq $version } | Select-Object -First 1;" ^
  "$file = $release.runtime.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' };" ^
  "if (-not $file) { throw ('No win-x64 runtime zip found for .NET ' + $version) };" ^
  "$zip = Join-Path $env:TEMP ('dotnet-runtime-' + $version + '-win-x64.zip');" ^
  "if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA512).Hash -ne $file.hash) {" ^
  "  Write-Host ('Downloading .NET runtime ' + $version + '...');" ^
  "  Invoke-WebRequest $file.url -OutFile $zip;" ^
  "  if ((Get-FileHash $zip -Algorithm SHA512).Hash -ne $file.hash) { Remove-Item $zip; throw ('Hash mismatch for ' + $file.url) };" ^
  "} else { Write-Host ('Using downloaded .NET runtime ' + $version) };" ^
  "if (Test-Path $env:DEST) { Remove-Item $env:DEST -Recurse -Force };" ^
  "Expand-Archive $zip -DestinationPath $env:DEST;" ^
  "Write-Host ('Added .NET runtime ' + $version + ' to ' + $env:DEST)"

if errorlevel 1 (
  echo Failed to add the .NET runtime.
  exit /b 1
)