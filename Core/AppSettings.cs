using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Serialization;

namespace MemoryCleaner.Core
{
    [Serializable]
    public class AppSettings
    {
        // 실제 메모리 자동 정리
        public bool AutoCleanPhysical { get; set; } = true;
        public int AutoCleanPhysicalThreshold { get; set; } = 60;

        // 가상 메모리 자동 정리
        public bool AutoCleanVirtual { get; set; } = false;
        public int AutoCleanVirtualThreshold { get; set; } = 90;

        // 워킹셋 자동 정리
        public bool AutoCleanWorkingSet { get; set; } = false;
        public int AutoCleanWorkingSetThreshold { get; set; } = 90;

        // 시간 기반 자동 정리
        public bool AutoCleanByTime { get; set; } = false;
        public int AutoCleanIntervalMinutes { get; set; } = 30;

        // 부팅시 실행
        public bool RunOnStartup { get; set; } = true;

        // 트레이 아이콘 클릭 시 정리
        public bool TrayClickClean { get; set; } = false;

        // 다크 모드 (기본 다크)
        public bool DarkMode { get; set; } = true;

        // 워킹셋 경고 표시
        public bool ShowWorkingSetWarning { get; set; } = true;
        public int WorkingSetWarningThreshold { get; set; } = 70;

        // 정리 후 결과 알림 표시
        public bool ShowCleanResultNotification { get; set; } = true;

        // 게임 모드(풀스크린 감지 시 자동정리 일시중지)
        public bool PauseOnFullscreen { get; set; } = true;

        // 메모리 누수 감지
        public bool EnableLeakDetector { get; set; } = true;
        public bool NotifyOnLeakDetected { get; set; } = true;

        // 강력 정리 모드. false(기본)=안전 모드: 저우선순위 Standby 해제 + GC만 수행.
        // true=전 프로세스 워킹셋 비우기 + Modified 플러시 + 전체 Standby 해제 + 파일 캐시 플러시.
        // 강력 정리는 직후 대량 페이지 재로딩으로 CPU/메모리 부하가 급변해, 불안정한
        // 하드웨어(예: 구 마이크로코드 13/14세대 Intel)에서 시스템 리셋을 유발한 사례가 있다.
        public bool FullClean { get; set; } = false;

        // 자동 업데이트 확인 (시작 후 + 24시간마다). 새 버전이 있으면 알리고, 설치는 사용자 승인 후.
        public bool AutoCheckUpdates { get; set; } = true;

        // 얕은 복제. 모든 속성이 값 타입이라 얕은 복제로 완전한 독립 사본이 된다.
        public AppSettings Clone() => (AppSettings)MemberwiseClone();

        // 설정 파일이 있는데 읽지 못해(잠김·접근 거부) 기본값으로 시작한 경우 true.
        // 이 상태에서 Save하면 사용자의 실제 설정 파일을 기본값으로 덮어쓰므로 저장을 막는다.
        [XmlIgnore]
        public bool LoadedFromFallback { get; private set; }

        // 범위 밖 값(구버전·수동 편집)을 유효 범위로 보정한다.
        // 임계치 0이면 항상 정리, 간격 0이면 매 틱 정리 같은 비정상 동작을 막는다.
        public void Normalize()
        {
            AutoCleanPhysicalThreshold   = Clamp(AutoCleanPhysicalThreshold, 1, 100);
            AutoCleanVirtualThreshold    = Clamp(AutoCleanVirtualThreshold, 1, 100);
            AutoCleanWorkingSetThreshold = Clamp(AutoCleanWorkingSetThreshold, 1, 100);
            WorkingSetWarningThreshold   = Clamp(WorkingSetWarningThreshold, 1, 100);
            AutoCleanIntervalMinutes     = Clamp(AutoCleanIntervalMinutes, 1, 24 * 60);
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KILHO.net", "MemoryCleaner", "settings.xml");

        // 동시 저장/로드 직렬화용 락
        private static readonly object _ioLock = new object();

        public static AppSettings Load()
        {
            lock (_ioLock)
            {
                if (!File.Exists(SettingsPath))
                    return new AppSettings();

                const int maxAttempts = 3;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        var xs = new XmlSerializer(typeof(AppSettings));
                        AppSettings loaded;
                        using (var fs = File.OpenRead(SettingsPath))
                            loaded = (AppSettings)xs.Deserialize(fs);
                        loaded.Normalize();
                        return loaded;
                    }
                    catch (InvalidOperationException ex)
                    {
                        // XML 손상(XmlSerializer는 파싱 오류를 InvalidOperationException으로 감싼다).
                        // 손상 파일은 .corrupt-타임스탬프로 보존하고 기본값으로 시작한다(이후 저장 허용).
                        Trace.WriteLine($"[AppSettings] Load failed (corrupt XML): {ex.InnerException?.Message ?? ex.Message}");
                        BackupCorruptFile();
                        return new AppSettings();
                    }
                    catch (IOException ex) when (attempt < maxAttempts)
                    {
                        // 백신·동기화 도구의 일시적 잠금 — 잠시 후 재시도
                        Trace.WriteLine($"[AppSettings] Load retry {attempt}/{maxAttempts} (IO): {ex.Message}");
                        System.Threading.Thread.Sleep(300);
                    }
                    catch (IOException ex)
                    {
                        Trace.WriteLine($"[AppSettings] Load failed (IO): {ex.Message} — 기본값 사용, 저장 보류");
                        break;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Trace.WriteLine($"[AppSettings] Load failed (access): {ex.Message} — 기본값 사용, 저장 보류");
                        break;
                    }
                }

                // 파일은 있는데 읽지 못함 → 기본값으로 동작하되 사용자 파일은 덮어쓰지 않는다.
                return new AppSettings { LoadedFromFallback = true };
            }
        }

        public void Save()
        {
            if (LoadedFromFallback)
            {
                Trace.WriteLine("[AppSettings] Save skipped — 설정 파일을 읽지 못해 기본값으로 시작한 세션(원본 보호)");
                return;
            }

            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                    var xs = new XmlSerializer(typeof(AppSettings));

                    // 임시파일에 먼저 쓴 뒤 원자적으로 교체 (중간 크래시 시 손상 방지)
                    string tmpPath = SettingsPath + ".tmp";
                    using (var fs = File.Create(tmpPath))
                        xs.Serialize(fs, this);

                    if (File.Exists(SettingsPath))
                    {
                        // File.Replace로 원자적 교체
                        File.Replace(tmpPath, SettingsPath, null);
                    }
                    else
                    {
                        File.Move(tmpPath, SettingsPath);
                    }
                }
                catch (IOException ex)
                {
                    Trace.WriteLine($"[AppSettings] Save failed (IO): {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Trace.WriteLine($"[AppSettings] Save failed (access): {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    Trace.WriteLine($"[AppSettings] Save failed (serialize): {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }

        private static void BackupCorruptFile()
        {
            try
            {
                string backup = SettingsPath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(SettingsPath, backup, overwrite: true);
                Trace.WriteLine($"[AppSettings] 손상된 설정 파일 백업: {backup}");
            }
            catch (IOException ex)
            {
                Trace.WriteLine($"[AppSettings] 손상 파일 백업 실패 (IO): {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.WriteLine($"[AppSettings] 손상 파일 백업 실패 (access): {ex.Message}");
            }
        }

        // ── 부팅 시 자동 실행 ────────────────────────────────────
        // 이 앱은 app.manifest 가 requireAdministrator(관리자 권한 필수)다.
        // 관리자 권한이 필요한 exe 는 HKCU\...\Run 레지스트리로는 로그온 시
        // 자동 실행되지 않는다 — Windows 가 UAC 상승을 조용히 띄울 수 없어
        // 항목이 있어도 무시된다. 따라서 "최고 권한으로 실행" 예약 작업
        // (Task Scheduler, 로그온 트리거)으로 등록해야 UAC 프롬프트 없이
        // 관리자 권한으로 로그온 시 자동 실행된다.
        public const string StartupTaskName = "MemoryCleaner";

        // schtasks 실행이 겹치지 않도록 직렬화
        private static readonly object _startupLock = new object();

        // UI 스레드에서 호출용: schtasks.exe를 기다리지 않도록 백그라운드에서 적용한다.
        public static System.Threading.Tasks.Task ApplyStartupRegistryAsync(bool enable)
            => System.Threading.Tasks.Task.Run(() => ApplyStartupRegistry(enable));

        // MainForm/SettingsControl 공용 헬퍼. (이름은 하위호환 위해 유지)
        // 동기 실행 — schtasks.exe 종료까지 블로킹하므로 UI 스레드에서는 Async 버전을 쓴다.
        public static void ApplyStartupRegistry(bool enable)
        {
            lock (_startupLock)
            {
                // 레거시 HKCU\Run 항목 제거: 과거 버전이 등록한 값이 남아 있으면
                // 로그온 시 실패 실행을 시도하므로 정리한다.
                RemoveLegacyRunEntry();

                try
                {
                    if (enable) CreateLogonTask();
                    else        DeleteLogonTask();
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // schtasks.exe 실행 자체 실패
                    Trace.WriteLine($"[Startup] schtasks 실행 실패: {ex.NativeErrorCode} - {ex.Message}");
                }
                catch (IOException ex)
                {
                    Trace.WriteLine($"[Startup] 작업 XML 임시파일 오류: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Trace.WriteLine($"[Startup] 접근 거부: {ex.Message}");
                }
            }
        }

        private static string GetExecutablePath()
        {
            // 단일 파일(self-contained) 게시에서도 실제 런처 exe 경로를 반환.
            string path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
                path = System.Windows.Forms.Application.ExecutablePath;
            return path;
        }

        private static void RemoveLegacyRunEntry()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key?.GetValue("MemoryCleaner") != null)
                    {
                        key.DeleteValue("MemoryCleaner", false);
                        Trace.WriteLine("[Startup] Removed legacy HKCU\\Run entry (replaced by scheduled task)");
                    }
                }
            }
            catch (System.Security.SecurityException ex)
            {
                Trace.WriteLine($"[Startup] RemoveLegacyRunEntry failed (security): {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.WriteLine($"[Startup] RemoveLegacyRunEntry failed (access): {ex.Message}");
            }
            catch (IOException ex)
            {
                Trace.WriteLine($"[Startup] RemoveLegacyRunEntry failed (IO): {ex.Message}");
            }
        }

        private static void CreateLogonTask()
        {
            string exe  = GetExecutablePath();
            string user;
            using (var identity = WindowsIdentity.GetCurrent())
                user = identity.Name; // DOMAIN\user

            string xml = BuildTaskXml(exe, user);

            // schtasks /XML 은 UTF-16(Unicode) 파일을 요구한다.
            string tmp = Path.Combine(Path.GetTempPath(), "MemoryCleaner_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);

            try
            {
                int code = RunSchtasks("/Create", "/TN", StartupTaskName, "/XML", tmp, "/F");
                if (code == 0)
                    Trace.WriteLine($"[Startup] Logon task created (HighestAvailable): {exe}");
                else
                    Trace.WriteLine($"[Startup] schtasks /Create returned {code}. 관리자 권한이 아닐 수 있음.");
            }
            finally
            {
                try { File.Delete(tmp); }
                catch (IOException ex) { Trace.WriteLine($"[Startup] 임시 작업 XML 삭제 실패: {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { Trace.WriteLine($"[Startup] 임시 작업 XML 삭제 실패: {ex.Message}"); }
            }
        }

        private static void DeleteLogonTask()
        {
            int code = RunSchtasks("/Delete", "/TN", StartupTaskName, "/F");
            // 작업이 없어서 실패(코드 1)한 경우도 정상으로 간주.
            Trace.WriteLine($"[Startup] schtasks /Delete returned {code}");
        }

        private static string BuildTaskXml(string exe, string user)
        {
            string e = System.Security.SecurityElement.Escape(exe);
            string u = System.Security.SecurityElement.Escape(user);
            // LogonTrigger + InteractiveToken + HighestAvailable:
            // 비밀번호 저장 없이, 로그온 시 관리자 권한으로 상호작용 세션에서 실행.
            return
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo>\r\n" +
"    <Description>메모리클리너 로그온 자동 실행</Description>\r\n" +
"  </RegistrationInfo>\r\n" +
"  <Triggers>\r\n" +
"    <LogonTrigger>\r\n" +
"      <Enabled>true</Enabled>\r\n" +
"      <UserId>" + u + "</UserId>\r\n" +
"    </LogonTrigger>\r\n" +
"  </Triggers>\r\n" +
"  <Principals>\r\n" +
"    <Principal id=\"Author\">\r\n" +
"      <UserId>" + u + "</UserId>\r\n" +
"      <LogonType>InteractiveToken</LogonType>\r\n" +
"      <RunLevel>HighestAvailable</RunLevel>\r\n" +
"    </Principal>\r\n" +
"  </Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>false</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
"    <IdleSettings>\r\n" +
"      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n" +
"      <RestartOnIdle>false</RestartOnIdle>\r\n" +
"    </IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <Hidden>false</Hidden>\r\n" +
"    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\">\r\n" +
"    <Exec>\r\n" +
"      <Command>" + e + "</Command>\r\n" +
"      <Arguments>/minimized</Arguments>\r\n" +
"    </Exec>\r\n" +
"  </Actions>\r\n" +
"</Task>\r\n";
        }

        private static int RunSchtasks(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using (var p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    Trace.WriteLine($"[Startup] schtasks {string.Join(' ', args)} → {p.ExitCode}: {se.Trim()} {so.Trim()}");
                return p.ExitCode;
            }
        }
    }
}
