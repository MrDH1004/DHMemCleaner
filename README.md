# 메모리클리너 (DHMemCleaner)

Windows용 메모리 정리·모니터링 도구입니다. 시스템 트레이에 상주하면서 메모리 사용률을 보여주고, 조건에 따라 자동으로 정리합니다.

## 설치

[Releases](https://github.com/MrDH1004/DHMemCleaner/releases/latest)에서 `DHMemCleaner_Setup_X.Y.Z.exe`를 받아 실행하세요.

- Windows 10/11 x64. .NET 런타임은 설치 파일에 포함돼 있어 따로 설치할 필요가 없습니다.
- 메모리 정리에 시스템 권한이 필요해 관리자 권한으로 실행됩니다.
- 내려받은 파일이 맞는지 확인하려면 같은 릴리즈의 `.sha256` 파일과 비교하세요.
  ```powershell
  (Get-FileHash .\DHMemCleaner_Setup_X.Y.Z.exe -Algorithm SHA256).Hash
  ```

## 업데이트

앱이 시작 후, 그리고 24시간마다 새 릴리즈가 있는지 확인합니다. 새 버전이 있으면 트레이 알림이 뜨고, 알림을 클릭하고 **지금 업데이트**를 누르면 설치됩니다.

1. 설치 파일과 `.sha256` 체크섬을 내려받아 SHA-256 값이 같은지 확인합니다.
2. 무인 설치를 실행합니다.
3. 앱이 잠시 종료됐다가 다시 시작됩니다. 설정은 그대로 유지됩니다.

직접 확인하려면 **환경설정 → 업데이트 → 지금 확인**을 누르거나, 트레이 메뉴에서 **업데이트 확인**을 고르세요. 자동 확인은 환경설정에서 끌 수 있습니다.

명령줄에서 무인으로 업데이트할 수도 있습니다(관리자 권한).

```powershell
& "C:\Program Files\MemoryCleaner\MemoryCleaner.exe" /update
```

## 주요 기능

- **실시간 현황**: 실제 메모리, 가상 메모리, 워킹셋 사용률
- **자동 정리**: 사용률 임계치 또는 시간 간격으로 정리합니다. 정리해도 사용률이 그대로면 다음 정리를 점점 늦게 실행합니다(5분부터 최대 60분까지).
- **게임 모드**: 풀스크린 앱을 실행하는 동안에는 자동 정리를 멈춥니다.
- **누수 감지**: 워킹셋이 계속 늘어나는 프로세스를 찾아 알려줍니다.
- **정리 강도**
  - 기본(안전 모드): 저우선순위 Standby 메모리만 해제합니다.
  - 강력 정리: 전체 워킹셋, Standby, 파일 캐시까지 비웁니다. 정리 직후 부하가 몰려서 하드웨어가 불안정한 PC는 재시작될 수 있습니다. 환경설정에서 켤 때만 동작합니다.

## 빌드

필요한 도구: .NET 8 SDK, [Inno Setup 6](https://jrsoftware.org/isinfo.php), (릴리즈할 때만) GitHub CLI

```powershell
.\build.ps1            # 테스트 → publish → installer_output\DHMemCleaner_Setup_X.Y.Z.exe(+.sha256)
.\build.ps1 -Release   # 위 단계 + GitHub 릴리즈 vX.Y.Z 생성·업로드
```

버전은 `MemoryCleaner.csproj`의 `<Version>` 한 곳에서만 관리합니다. 자체 업데이트는 릴리즈 자산 이름 규칙(`DHMemCleaner_Setup_X.Y.Z.exe`, `.exe.sha256`)에 의존하므로 `build.ps1`로 릴리즈하세요.

## 로그

`%AppData%\KILHO.net\MemoryCleaner\debug.log`에 정리 단계별 결과(NTSTATUS)와 업데이트 기록이 시각과 함께 남습니다.
(이 폴더 이름은 이전 버전과 설정을 호환하려고 그대로 두었습니다.)
