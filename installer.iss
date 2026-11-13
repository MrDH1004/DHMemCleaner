; ============================================================
;  메모리클리너 (DHMemCleaner) — Inno Setup 설치 스크립트
;  보통은 build.ps1이 실행한다:  ISCC.exe /DMyAppVersion=X.Y.Z installer.iss
;  게시 폴더(publish\)를 통째로 담아 DHMemCleaner_Setup_X.Y.Z.exe 를 만든다.
;
;  자체 업데이트: 앱이 이 설치 파일을 아래 인자로 무인 실행한다.
;    /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE=1
;  /UPDATE=1 이면 설치가 끝난 뒤 앱을 트레이 모드로 다시 실행한다([Run] IsUpdateMode).
; ============================================================

#define MyAppName "메모리클리너"
#define MyAppNameEn "MemoryCleaner"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#define MyAppPublisher "MrDH1004"
#define MyAppURL "https://github.com/MrDH1004/DHMemCleaner"
#define MyAppExeName "MemoryCleaner.exe"

[Setup]
; AppId 는 이 앱을 고유하게 식별한다. 버전이 바뀌어도 절대 바꾸지 말 것(업그레이드 인식용).
AppId={{8F2A1C4E-6B3D-4A7E-9C1F-2D5E8A0B3C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
; 기본 설치 경로: Program Files\MemoryCleaner (기존 설치와 같은 경로 — 덮어쓰기 업그레이드)
DefaultDirName={autopf}\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
; 설치 관리자는 관리자 권한으로 실행(Program Files 기록 + 앱 자체가 관리자 필요)
PrivilegesRequired=admin
; 64비트 앱 → 64비트 모드 설치
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
OutputDir=installer_output
OutputBaseFilename=DHMemCleaner_Setup_{#MyAppVersion}
SetupIconFile=img\app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 실행 중인 앱은 Restart Manager로 닫는다(자체 업데이트 시 상주 인스턴스 포함)
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; 참고: Windows 시작 시 자동 실행은 앱이 스스로 관리한다(환경설정 '부팅 시 실행' → 작업 스케줄러 등록).

[Files]
; publish\ 폴더 전체를 설치 폴더로 복사(self-contained 단일 exe + 부속 파일 포함)
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 시작 메뉴
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
; 바탕화면(선택)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 일반 설치: 완료 후 즉시 실행(선택). 관리자 권한 앱이라 runascurrentuser로 상승 상태 유지.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent runascurrentuser
; 자체 업데이트(/UPDATE=1): 무인 설치 후 트레이 모드로 다시 실행
Filename: "{app}\{#MyAppExeName}"; Parameters: "/minimized"; \
    Flags: nowait runascurrentuser; Check: IsUpdateMode

[UninstallRun]
; 제거 시 실행 중인 프로세스 종료
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; \
    Flags: runhidden; RunOnceId: "KillMemoryCleaner"

; 참고: 사용자 설정/로그(%AppData%\KILHO.net\MemoryCleaner)는 제거 시 남겨둔다(재설치 시 설정 보존).
; 폴더 이름은 이전 버전과의 설정 호환을 위해 유지한다.

[Code]
function IsUpdateMode: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

// 파일 교체 전에 남아 있는 앱 인스턴스를 확실히 종료한다
// (트레이 상주 앱은 Restart Manager 종료 요청에 늦게 응답할 수 있다).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExeName} /F', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;
