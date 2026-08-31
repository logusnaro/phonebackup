$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$out = Join-Path $root 'artifacts\PhoneBackup-windows-x64-portable'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& $dotnet publish (Join-Path $root 'src\Desktop\PhoneBackup.Desktop.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -o $out
if ($LASTEXITCODE -ne 0) { throw "Windows 빌드 실패 (exit $LASTEXITCODE)" }
$distribution = Join-Path $root 'distribution'
Copy-Item (Join-Path $distribution 'PB-먼저읽기.txt') $out -Force
Copy-Item (Join-Path $distribution 'PB-설치·오류대응-요약.txt') $out -Force
Copy-Item (Join-Path $distribution 'PB-진단리포트.ps1') $out -Force
$hashLines = Get-ChildItem $out -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
}
$hashLines | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding utf8NoBOM
$zip = Join-Path $root 'artifacts\PhoneBackup-windows-x64-portable.zip'
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Fastest
