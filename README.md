# PB 2.0 · Smart Switch 백업 관리

PB는 두 개의 서로 독립적인 앱입니다.

- **Windows 앱**: Samsung Smart Switch가 PC에 만든 백업 폴더를 찾아 파일 유형별로 색인하고 관리합니다.
- **Android 앱**: 휴대폰 저장공간을 점검하고, 사용자가 Smart Switch 백업을 확인한 뒤 오래된 통화녹음과 선택 파일을 정리합니다.

PC와 모바일은 Wi‑Fi·USB·로컬 서버로 통신하지 않습니다. 회사 PC 보안 정책을 우회하지 않으며, 연락처 동기화도 하지 않습니다.

## Windows 기능

- Smart Switch 실행 및 기본 경로·Samsung 설정·문서·다운로드·바탕 화면·OneDrive 자동 탐색
- 예상 밖 위치는 상위 폴더를 한 번 선택하면 기종별 `SM-*` 폴더 자동 분리
- 원본을 복사하거나 수정하지 않는 제자리 색인
- 통화녹음, 사진, 영상, 문서, 오디오, 압축·앱·삼성 전용 데이터, 기타 탭
- 통화녹음 파일명에서 날짜·전화번호·상대방·대상·소속 추출
- 통화녹음 날짜별/상대방별 그룹, 검색·정렬
- 사진 미리보기, 파일 실행, 탐색기에서 해당 파일 자동 선택
- 선택 백업 폴더 SHA‑256 무결성 확인
- 개인정보를 제외한 오류 리포트 내보내기

Smart Switch 백업 내부의 개별 파일을 삭제하면 삼성 복원이 깨질 수 있으므로 PB PC 앱은 원본 삭제 기능을 제공하지 않습니다. 폴더를 목록에서 제거해도 PB 색인만 삭제됩니다.

## Android 기능

- 전체 저장공간 사용률과 80/85/90% 위험 기준
- 사진·영상·오디오 자동 검사 및 사용자가 선택한 문서/기타 폴더 검사
- 주 1회 백업·정리 알림
- 통화녹음 90일 초과 후보, 일반 파일 유형별 목록, 이미지 미리보기와 다중 선택
- 백업 전 목록 스냅샷 → Smart Switch 안내 → 3단계 사용자 확인 → 24시간 삭제 허용
- 스냅샷 이후 새로 생기거나 변경된 파일 및 90일 이내 통화녹음 삭제 차단
- 삭제 직전 경고와 Android 시스템 삭제 승인
- 개인정보를 제외한 오류 리포트 내보내기

Samsung은 Smart Switch PC 백업 완료 여부를 Android 앱이 확인하는 공개 API를 제공하지 않습니다. 따라서 PB는 백업을 자동으로 증명한다고 표시하지 않고 사용자가 Smart Switch 완료 화면을 직접 확인하도록 합니다.

## 빌드

Android Studio는 필수가 아닙니다. 저장소에 준비된 .NET 8, JDK 17, Gradle, Android SDK로 빌드할 수 있습니다.

```powershell
.\scripts\build-windows.ps1
.\scripts\build-android.ps1
.\scripts\build-android-release.ps1
.\scripts\build-distribution.ps1
```

Release APK 빌드에는 기존 서명키와 `PB_KEYSTORE_PASSWORD`, `PB_KEY_PASSWORD` 환경 변수가 필요합니다. 서명키를 바꾸면 기존 Release 앱 위에 업데이트할 수 없으므로 임의로 새 키를 만들지 않습니다. 마지막 명령은 Windows ZIP, Release APK, PPT, 안내문과 SHA-256 목록을 `artifacts/PB-2.0-배포`에 모읍니다.

- Windows: `artifacts/PhoneBackup-windows-x64-portable.zip`
- Android: `artifacts/PhoneBackupAndroid-release-v2.0.0.apk`
- 지원: Windows 10/11 x64, Android 6.0(API 23) 이상
- 아이콘: 초록색 바탕의 흰색 PB로 고정

기존 v1.x 데이터베이스와 저장 파일은 삭제하지 않습니다. PB 2.0은 기존 연결·연락처·업로드 데이터를 화면에 표시하거나 변경하지 않습니다. 이전 구현은 Git 태그 `legacy-v1.6.0`에 보존되어 있습니다.

자세한 절차는 [설치·사용 가이드](docs/INSTALL_GUIDE_KO.md), 제한사항은 [알려진 이슈](docs/KNOWN_ISSUES_KO.md)를 확인하세요.
