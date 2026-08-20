# 업무폰 통합 백업

Windows PC를 중앙 저장소로 사용해 삼성 Android 업무폰의 녹음·문서·사진과 주소록을 같은 Wi‑Fi에서 관리하는 사내 도구입니다.

## 현재 구현된 범위

- Windows WPF/.NET 8 데스크톱 앱과 Kotlin Android 앱 프로젝트
- 멤버/기기 모델, SQLite 스키마, 감사 로그
- 6자리 Wi‑Fi 페어링 코드와 기기별 토큰 인증
- 로컬 HTTPS 수신 서버와 SHA-256 검증 파일 업로드
- 원본 파일명 보존, 해시 기반 중복 저장 방지, 백업 폴더 선택
- 주소록 읽기·승인 대기 업로드·업무 통합 주소록 배포 기반
- Android Storage Access Framework 폴더 선택과 수동 백업 Worker
- 대시보드·멤버·주소록·파일·설정 기본 화면

## 빌드

필요한 도구는 .NET 8 SDK/Windows Desktop Runtime, JDK 17, Gradle, Android SDK입니다. Android Studio는 필요하지 않습니다.

```powershell
dotnet restore PhoneBackup.sln
dotnet publish src/Desktop/PhoneBackup.Desktop.csproj -c Release
# Android Studio에서 android 폴더를 열고 Gradle Sync 후 assembleRelease 실행

# 설치된 프로젝트 전용 도구로 재현
.\scripts\build-windows.ps1
.\scripts\build-android.ps1
```

현재 환경에서 Windows Release 폴더형 배포본과 Android debug APK 빌드를 검증했습니다. Windows는 `artifacts/PhoneBackup-windows-x64-portable.zip` 압축을 풀어 `PhoneBackup.exe`를 실행합니다. Android APK는 `artifacts/PhoneBackupAndroid-debug.apk`이며 Android 6.0(API 23) 이상을 지원합니다. 첫 현장 연결 전에 실제 삼성 모델/Android 버전과 통화녹음 파일명 샘플을 받아 파일명 파서를 보강해야 합니다.

Android Studio는 필수가 아닙니다. 저장소의 `scripts/build-android.ps1` 또는 설치된 JDK 17·Gradle·Android SDK로 `:app:assembleDebug`를 실행할 수 있습니다. Windows와 Android 앱 아이콘은 초록색 바탕의 흰색 `PB`로 통일되어 있습니다.

## 운영 주의

자세한 설치·시험 절차는 [한국어 설치 가이드](docs/INSTALL_GUIDE_KO.md)를 먼저 읽으세요.

- 최초 버전은 음성 전사·요약·중요도 분석을 하지 않습니다.
- 백업 저장소가 노트북 한 곳뿐이므로 삭제 추천은 90일 보존 후에도 수동 승인만 허용해야 합니다.
- Android 앱은 6자리 코드를 입력하면 같은 Wi‑Fi의 PC를 자동으로 찾아 연결합니다.
- 사내 Wi‑Fi의 기기 간 통신 차단(AP isolation)과 Windows 방화벽 정책을 온보딩에서 확인해야 합니다.
