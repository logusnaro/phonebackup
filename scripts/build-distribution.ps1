$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$packageName = 'PB-2.0-배포'
$packageDir = Join-Path $artifacts $packageName
$packageZip = Join-Path $artifacts "$packageName.zip"

$inputs = @(
    @{ Source = Join-Path $artifacts 'PhoneBackupAndroid-release-v2.0.0.apk'; Target = '01_PhoneBackupAndroid-release-v2.0.0.apk' },
    @{ Source = Join-Path $artifacts 'PhoneBackup-windows-x64-portable.zip'; Target = '02_PhoneBackup-windows-x64-portable.zip' },
    @{ Source = Join-Path $artifacts 'PB_2.0_설치·사용가이드.pptx'; Target = '03_PB_2.0_설치·사용가이드.pptx' },
    @{ Source = Join-Path $root 'distribution\PB-먼저읽기.txt'; Target = '04_PB-먼저읽기.txt' },
    @{ Source = Join-Path $root 'distribution\PB-설치·오류대응-요약.txt'; Target = '05_PB-설치·오류대응-요약.txt' }
)

$missing = @($inputs | Where-Object { -not (Test-Path -LiteralPath $_.Source) })
if ($missing.Count -gt 0) {
    $missingList = ($missing | ForEach-Object { $_.Source }) -join "`n"
    throw "배포 파일을 만들 수 없습니다. 다음 산출물이 없습니다:`n$missingList"
}

$resolvedArtifacts = [IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'
$resolvedPackage = [IO.Path]::GetFullPath($packageDir).TrimEnd('\') + '\'
if (-not $resolvedPackage.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
    throw "배포 대상이 artifacts 폴더 밖입니다: $resolvedPackage"
}

if (Test-Path -LiteralPath $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

foreach ($input in $inputs) {
    Copy-Item -LiteralPath $input.Source -Destination (Join-Path $packageDir $input.Target) -Force
}

$hashLines = Get-ChildItem -LiteralPath $packageDir -File |
    Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
$hashLines | Set-Content -LiteralPath (Join-Path $packageDir 'SHA256SUMS.txt') -Encoding utf8

if (Test-Path -LiteralPath $packageZip) {
    Remove-Item -LiteralPath $packageZip -Force
}
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $packageZip -CompressionLevel Optimal

Write-Host "Distribution folder: $packageDir"
Write-Host "Distribution ZIP:    $packageZip"
