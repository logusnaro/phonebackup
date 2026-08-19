$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:JAVA_HOME = (Resolve-Path (Join-Path $root '.tools\jdk17')).Path
$env:ANDROID_HOME = (Resolve-Path (Join-Path $root '.tools\android-sdk')).Path
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:ANDROID_USER_HOME = Join-Path $root '.android'
$env:GRADLE_USER_HOME = Join-Path $root '.gradle-home'
$gradle = Join-Path $root '.tools\gradle\bin\gradle.bat'
Push-Location (Join-Path $root 'android')
try { & $gradle assembleDebug --no-daemon --offline --console=plain } finally { Pop-Location }
