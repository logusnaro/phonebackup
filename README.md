# 업무폰 통합 백업

Windows PC에서 개인 프로필별 Samsung Smart Switch 백업을 검증·색인하고, 90일 경과 통화녹음 삭제를 승인하는 개인용 PB 도구입니다.

## 현재 구현된 범위

- Windows WPF/.NET 8 데스크톱 앱과 Kotlin Android 앱 프로젝트
- 멤버/기기 모델, SQLite 스키마, 감사 로그
- 6자리 Wi‑Fi 페어링 코드와 기기별 토큰 인증
- 로컬 HTTPS 수신 서버와 SHA-256 검증 파일 업로드
- 원본 파일명 보존, 해시 기반 중복 저장 방지, 백업 폴더 선택
- Smart Switch PC 실행 요청·백업 회차 검증·통화녹음 가져오기
- Smart Switch 검증 성공 전 삭제 후보 생성 차단
- Android 주소록 권한·주소록 동기화 제거
- Android 주 1회 백업·삭제 검토 알림과 삭제 전 해시 재검사
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

현재 환경에서 Windows Release 폴더형 배포본과 Android Release APK 빌드를 검증했습니다. Windows는 `artifacts/PhoneBackup-windows-x64-portable.zip` 압축을 풀어 `PhoneBackup.exe`를 실행합니다. 팀원용 Android APK는 `artifacts/PhoneBackupAndroid-release-v1.5.0.apk`이며 Android 6.0(API 23) 이상을 지원합니다. 첫 현장 연결 전에 실제 삼성 모델/Android 버전과 통화녹음 파일명 샘플을 받아 파일명 파서를 보강해야 합니다.

Android Studio는 필수가 아닙니다. 저장소의 `scripts/build-android.ps1` 또는 `scripts/build-android-release.ps1`와 설치된 JDK 17·Gradle·Android SDK로 빌드할 수 있습니다. Windows와 Android 앱 아이콘은 초록색 바탕의 흰색 `PB`로 통일되어 있습니다.

## 운영 주의

자세한 설치·시험 절차는 [한국어 설치 가이드](docs/INSTALL_GUIDE_KO.md)를 먼저 읽으세요.

- 최초 버전은 음성 전사·요약·중요도 분석을 하지 않습니다.
- 정식 백업 엔진은 Smart Switch PC입니다. PB의 Android 파일 업로드는 보조 백업으로만 사용합니다.
- Smart Switch 검증·PB 등록·90일 경과·사용자 승인을 모두 통과하기 전에는 휴대폰 원본을 삭제하지 않습니다.
- 삭제 대상은 1차로 통화녹음으로 제한하며, 사진·문서·동영상은 별도 매니페스트가 준비될 때까지 삭제하지 않습니다.
- 개인 프로필과 PC 연락처 메모는 서로 분리되고 휴대폰 주소록과 동기화하지 않습니다.
- Android 앱은 6자리 코드를 입력하면 같은 Wi‑Fi의 PC를 자동으로 찾아 연결합니다.
- 사내 Wi‑Fi의 기기 간 통신 차단(AP isolation)과 Windows 방화벽 정책을 온보딩에서 확인해야 합니다.
