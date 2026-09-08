$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:JAVA_HOME = (Resolve-Path (Join-Path $root '.tools\jdk17')).Path
$env:ANDROID_HOME = (Resolve-Path (Join-Path $root '.tools\android-sdk')).Path
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:ANDROID_USER_HOME = Join-Path $root '.android'
$env:GRADLE_USER_HOME = Join-Path $root '.gradle-home'
$keystore = if ($env:PB_KEYSTORE_PATH) { $env:PB_KEYSTORE_PATH } else { Join-Path $root '.android\phonebackup-release.keystore' }
if (-not (Test-Path $keystore)) { throw "Release 서명키를 찾지 못했습니다: $keystore" }
if ([string]::IsNullOrWhiteSpace($env:PB_KEYSTORE_PASSWORD) -or [string]::IsNullOrWhiteSpace($env:PB_KEY_PASSWORD)) {
    throw 'PB_KEYSTORE_PASSWORD와 PB_KEY_PASSWORD 환경 변수를 설정한 뒤 다시 실행하세요.'
}
$env:PB_KEYSTORE_PATH = (Resolve-Path $keystore).Path
$gradle = Join-Path $root '.tools\gradle\bin\gradle.bat'
$apkSource = Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk'
$apkTarget = Join-Path $root 'artifacts\PhoneBackupAndroid-release-v2.0.3.apk'
Push-Location (Join-Path $root 'android')
try { & $gradle assembleRelease --no-daemon --offline --console=plain -x lintVitalAnalyzeRelease -x lintVitalReportRelease -x lintVitalRelease } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "Android Release 빌드 실패 (exit $LASTEXITCODE)" }
if (-not (Test-Path $apkSource)) { throw "Release APK가 생성되지 않았습니다: $apkSource" }
New-Item -ItemType Directory (Split-Path $apkTarget) -Force | Out-Null
Copy-Item $apkSource $apkTarget -Force
(Get-FileHash $apkTarget -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + (Split-Path $apkTarget -Leaf) | Set-Content (Join-Path $root 'artifacts\SHA256SUMS.txt') -Encoding ascii
Write-Host "Release APK: $apkTarget"
