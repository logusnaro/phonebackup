$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$output = Join-Path (Get-Location) "PB-error-report-$stamp.zip"
$temp = Join-Path ([System.IO.Path]::GetTempPath()) "PB-diagnostics-$stamp"
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $report = [ordered]@{
        reportId = [guid]::NewGuid().ToString('N')
        createdAt = [DateTimeOffset]::Now.ToString('O')
        os = [Environment]::OSVersion.VersionString
        processArchitecture = [Environment]::Is64BitOperatingSystem ? 'x64' : 'x86'
        note = '로그 파일만 수집한 시작 실패용 진단 리포트입니다.'
    }
    $report | ConvertTo-Json | Set-Content (Join-Path $temp 'report.json') -Encoding UTF8
    $redact = {
        param([string]$value)
        $value = [regex]::Replace($value, '[A-Za-z]:\\[^\r\n"'']+', '<redacted-path>')
        $value = [regex]::Replace($value, '(?i)\b[a-f0-9]{64}\b', '<redacted-sha256>')
        $value = [regex]::Replace($value, '(?i)(token|password|secret|certificate|authorization)(\s*[:=]\s*)[^\s,;]+', '$1$2<redacted-secret>')
        [regex]::Replace($value, '(?<!\d)(?:\+?82[- .]?)?0\d{1,2}[- .]?\d{3,4}[- .]?\d{4}(?!\d)', '<redacted-phone>')
    }
    Get-ChildItem ([System.IO.Path]::GetTempPath()) -Filter 'PhoneBackup-*.log' -File -ErrorAction SilentlyContinue | ForEach-Object {
        $content = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
        if ($content.Length -gt 200000) { $content = $content.Substring($content.Length - 200000) }
        & $redact $content | Set-Content (Join-Path $temp $_.Name) -Encoding UTF8
    }
    Compress-Archive -Path (Join-Path $temp '*') -DestinationPath $output -CompressionLevel Fastest
    Write-Host "오류 리포트를 저장했습니다: $output"
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
