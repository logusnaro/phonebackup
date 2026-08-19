$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$out = Join-Path $root 'artifacts\PhoneBackup-windows-x64-portable'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& $dotnet publish (Join-Path $root 'src\Desktop\PhoneBackup.Desktop.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -o $out
$zip = Join-Path $root 'artifacts\PhoneBackup-windows-x64-portable.zip'
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Fastest
