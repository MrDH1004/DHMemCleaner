using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MemoryCleaner.Core
{
    public class MemoryInfo
    {
        public long PhysicalTotal { get; set; }
        public long PhysicalUsed { get; set; }
        public double PhysicalPercent { get; set; }

        public long VirtualTotal { get; set; }
        public long VirtualUsed { get; set; }
        public double VirtualPercent { get; set; }

        public long WorkingSetUsed { get; set; }
        public long WorkingSetTotal { get; set; }
        public double WorkingSetPercent { get; set; }
    }

    public class ProcessMemoryInfo
    {
        public int PID { get; set; }
        public string Name { get; set; }
        public long WorkingSet { get; set; }
        public long PrivateBytes { get; set; }
    }

    public static class MemoryHelper
    {
        // ── Win32 구조체 / 상수 ──────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        private const uint SE_PRIVILEGE_ENABLED        = 0x00000002;
        private const string SE_INCREASE_QUOTA_NAME    = "SeIncreaseQuotaPrivilege";
        private const string SE_PROFILE_SINGLE_NAME    = "SeProfileSingleProcessPrivilege";

        private const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const int TOKEN_QUERY             = 0x0008;

        // SystemMemoryListInformation
        private const int SystemMemoryListInformation       = 0x50;
        private const int MemoryEmptyWorkingSets            = 2;
        private const int MemoryFlushModifiedList           = 3;
        private const int MemoryPurgeStandbyList            = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        // SystemFileCacheInformation
        private const int SystemFileCacheInformation = 0x15;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_FILECACHE_INFORMATION
        {
            public IntPtr CurrentSize;
            public IntPtr PeakSize;
            public uint PageFaultCount;
            public IntPtr MinimumWorkingSet;
            public IntPtr MaximumWorkingSet;
            public IntPtr CurrentSizeIncludingTransitionInPages;
            public IntPtr PeakSizeIncludingTransitionInPages;
            public uint TransitionRePurposeCount;
            public uint Flags;
        }

        // ── P/Invoke ─────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
            ref TOKEN_PRIVILEGES newState, uint bufLen, IntPtr prevState, IntPtr retLen);

        [DllImport("ntdll.dll")]
        private static extern uint NtSetSystemInformation(int infoClass, IntPtr info, int length);

        // ── NTSTATUS 코드 ────────────────────────────────────────

        private const uint STATUS_SUCCESS           = 0x00000000;
        private const uint STATUS_PRIVILEGE_NOT_HELD = 0xC0000061;
        private const uint STATUS_ACCESS_DENIED      = 0xC0000022;

        // ── 쿨다운 ───────────────────────────────────────────────

        // AC3.3: 쿨다운의 단일 진실 원천. 명시적으로 DateTime.MinValue로 초기화하여
        // 앱 시작 직후 첫 정리가 쿨다운에 막히지 않도록 한다.
        // 시스템 시계 변경에 흔들리지 않도록 MonotonicClock 기준으로 기록한다.
        private static DateTime _lastCleanTime = DateTime.MinValue;
        private const int CooldownSeconds = 60;

        // ── 마지막 정리 결과 ─────────────────────────────────────
        // 권한 외 원인으로 실패한 단계가 있었는지(예: STATUS_INVALID_PARAMETER).
        // AutoCleanManager가 이력 태그·알림 문구에 사용한다.
        public static bool LastCleanStepFailed { get; private set; }

        // AC3.3: 테스트 격리용. 전역 static 쿨다운 상태를 초기화하여
        // 테스트 간 상태 오염(state pollution)을 방지한다.
        public static void ResetLastCleanTime()
        {
            _lastCleanTime = DateTime.MinValue;
        }

        // ── 권한 실패 상태 (AC1.2: 그레이스풀 폴백 태그용) ────────
        // 마지막 CleanMemory 호출에서 Standby/Cache 정리가 권한 부족으로
        // 실패했는지 여부. AutoCleanManager가 History 태그에 사용한다.
        public static bool LastCleanPrivilegeFailed { get; private set; }

        // ── 메모리 정보 조회 (캐시) ──────────────────────────────
        // GetMemoryInfo()는 Dashboard(1초), 트레이(5초), 모니터(3초) 타이머에서
        // 각각 호출되어 초당 여러 번 전 프로세스 스냅샷(GetTotalWorkingSet)을 유발한다.
        // 짧은 TTL 캐시로 중복 전체 프로세스 열거를 제거한다.
        //
        // 동시성 설계:
        //  - _memInfoLock 은 캐시 필드 읽기/쓰기에만 짧게 잡는다. 전 프로세스 열거
        //    (ComputeMemoryInfo, 수십~수백 ms)는 이 락 밖에서 수행해 UI 스레드가 막히지 않게 한다.
        //  - _computeLock 은 "동시에 한 스레드만 계산"을 보장한다. 이미 다른 스레드가
        //    계산 중이고 이전 값이 있으면, 기다리지 않고 이전 값(최대 수 초 전)을 돌려준다.
        //  - _cacheGeneration 은 무효화 세대. 계산 도중 InvalidateMemoryInfoCache()가
        //    호출되면(정리 완료 직후) 그 계산 결과는 정리 이전 상태이므로 캐시에 저장하지 않는다.
        private static readonly object _memInfoLock = new object();
        private static readonly object _computeLock = new object();
        private static MemoryInfo _cachedMemInfo;
        private static DateTime _memInfoCachedAt = DateTime.MinValue;
        private static long _cacheGeneration = 0;
        private const int MemInfoCacheTtlMs = 900;

        public static MemoryInfo GetMemoryInfo()
        {
            MemoryInfo cached;
            if (TryGetFreshCache(out cached))
                return cached;

            bool entered = Monitor.TryEnter(_computeLock);
            if (!entered)
            {
                // 다른 스레드가 계산 중 — 이전 값이 있으면 기다리지 않고 반환
                if (cached != null) return cached;
                Monitor.Enter(_computeLock);
            }

            try
            {
                // 기다리는 동안 다른 스레드가 새 값을 채웠을 수 있다
                if (TryGetFreshCache(out cached))
                    return cached;

                long generation = Interlocked.Read(ref _cacheGeneration);
                var info = ComputeMemoryInfo();

                lock (_memInfoLock)
                {
                    if (generation == _cacheGeneration)
                    {
                        _cachedMemInfo = info;
                        _memInfoCachedAt = DateTime.UtcNow;
                    }
                }
                return info;
            }
            finally
            {
                Monitor.Exit(_computeLock);
            }
        }

        // 캐시가 TTL 이내면 true. false여도 out 값에는 (오래됐을 수 있는) 마지막 값이 들어간다.
        private static bool TryGetFreshCache(out MemoryInfo cached)
        {
            lock (_memInfoLock)
            {
                cached = _cachedMemInfo;
                return cached != null &&
                       (DateTime.UtcNow - _memInfoCachedAt).TotalMilliseconds < MemInfoCacheTtlMs;
            }
        }

        // 캐시 무효화. 메모리 상태를 바꾼 직후(정리 완료 등) 호출하여 다음
        // GetMemoryInfo()가 TTL 이내라도 실측값을 다시 계산하도록 한다.
        // (before/after 절약량 계산이 동일 캐시 인스턴스를 받아 0%로 표시되는 것 방지)
        public static void InvalidateMemoryInfoCache()
        {
            lock (_memInfoLock)
            {
                _cachedMemInfo = null;
                _memInfoCachedAt = DateTime.MinValue;
                Interlocked.Increment(ref _cacheGeneration);
            }
        }

        private static MemoryInfo ComputeMemoryInfo()
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            GlobalMemoryStatusEx(ref status);

            long physTotal = (long)status.ullTotalPhys;
            long physAvail = (long)status.ullAvailPhys;
            long physUsed  = physTotal - physAvail;

            long pageTotal = (long)status.ullTotalPageFile;
            long pageAvail = (long)status.ullAvailPageFile;
            long pageUsed  = pageTotal - pageAvail;

            // WorkingSetUsed 는 전 프로세스 WorkingSet64 의 단순 합이다(GetTotalWorkingSet).
            // 공유 페이지(DLL 등)가 프로세스마다 중복 계산되므로 실제 물리 RAM 사용량이
            // 아니라 상한 근사치이며, 물리 총량을 초과할 수 있다. UI 지표 왜곡(>100%)을
            // 막기 위해 물리 총량으로 클램프한다. 정확한 물리 사용량은 PhysicalUsed 사용.
            long wsUsed = GetTotalWorkingSet();
            if (physTotal > 0 && wsUsed > physTotal) wsUsed = physTotal;

            return new MemoryInfo
            {
                PhysicalTotal   = physTotal,
                PhysicalUsed    = physUsed,
                PhysicalPercent = physTotal > 0 ? (double)physUsed / physTotal * 100.0 : 0,

                VirtualTotal   = pageTotal,
                VirtualUsed    = pageUsed,
                VirtualPercent = pageTotal > 0 ? (double)pageUsed / pageTotal * 100.0 : 0,

                WorkingSetUsed    = wsUsed,
                WorkingSetTotal   = physTotal,
                WorkingSetPercent = physTotal > 0 ? (double)wsUsed / physTotal * 100.0 : 0,
            };
        }

        private static long GetTotalWorkingSet()
        {
            long total = 0;
            foreach (var proc in Process.GetProcesses())
            {
                try { total += proc.WorkingSet64; }
                catch (InvalidOperationException) { /* 열거 직후 종료된 프로세스 — 합계에서 제외 */ }
                finally { proc.Dispose(); }
            }
            return total;
        }

        // ── 메모리 정리 ──────────────────────────────────────────

        // 로깅 헬퍼: Trace.WriteLine로 출력 → Program.cs의 TraceListener가 debug.log에 기록.
        // (Debug.WriteLine은 Release 빌드에서 제거되므로 Trace를 사용해 양쪽 빌드에서 모두 기록)
        private static void Log(string message)
        {
            Trace.WriteLine(message);
        }

        // NTSTATUS 코드를 사람이 읽을 수 있는 이름으로 변환 (AC1.1).
        private static string DescribeStatus(uint status)
        {
            switch (status)
            {
                case STATUS_SUCCESS:            return "STATUS_SUCCESS";
                case STATUS_PRIVILEGE_NOT_HELD: return "STATUS_PRIVILEGE_NOT_HELD";
                case STATUS_ACCESS_DENIED:      return "STATUS_ACCESS_DENIED";
                default:                        return "STATUS_UNKNOWN";
            }
        }

        // fullClean=false(기본, 안전 모드): 저우선순위 Standby 해제 + GC만 수행한다.
        // fullClean=true(강력 모드): 전 프로세스 워킹셋 비우기 ~ 파일 캐시 플러시까지 전부 수행.
        //
        // forceCooldownBypass(수동·트레이 정리)는 안전 모드에서만 적용된다. 강력 정리는 직후
        // 부하 급증으로 불안정한 PC를 리셋시킨 사례가 있어, 수동이라도 60초 쿨다운을 지킨다
        // (트레이 연타로 강력 정리가 연속 실행되는 것 방지).
        public static bool CleanMemory(bool forceCooldownBypass = false, bool fullClean = false)
        {
            bool bypass = forceCooldownBypass && !fullClean;
            if (!bypass && GetCooldownRemaining() > TimeSpan.Zero)
                return false;

            _lastCleanTime = MonotonicClock.Now;
            LastCleanPrivilegeFailed = false;
            LastCleanStepFailed = false;

            Log($"[MemoryHelper] CleanMemory 시작 (mode={(fullClean ? "Full" : "Safe")})");

            var tally = new StepTally();
            if (fullClean) RunFullClean(tally);
            else           RunSafeClean(tally);

            // AC1.2: Standby/Cache 정리가 "권한 부족"으로 실패한 경우에만 폴백 태그를 단다.
            // 권한과 무관한 실패(STATUS_INVALID_PARAMETER 등)는 권한 문제로 오진하지 않고
            // 별도로 로깅한다(어떤 단계가 어떤 NTSTATUS를 냈는지는 위 단계별 로그에 있다).
            if (tally.PrivilegeFailed)
            {
                LastCleanPrivilegeFailed = true;
                Log("[MemoryHelper] 권한 부족 감지 — 경량 정리(GC)로 폴백합니다.");
            }
            if (tally.OtherFailed)
            {
                LastCleanStepFailed = true;
                Log("[MemoryHelper] 권한 외 원인으로 실패한 단계가 있음 — 위 NTSTATUS 로그 확인");
            }

            // .NET GC (권한 부족 시에도 항상 수행)
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);

            // 방금 메모리 상태를 바꿨으므로 캐시를 무효화하여, PerformClean의
            // after = GetMemoryInfo()가 TTL 이내라도 실측값을 다시 계산하게 한다.
            InvalidateMemoryInfoCache();

            Log("[MemoryHelper] CleanMemory 완료");

            // AC1.2: 권한 부족이어도 부분 성공으로 간주하여 true 반환.
            return true;
        }

        // 안전 모드: 낮은 우선순위 Standby 페이지만 해제한다.
        // 워킹셋을 강제로 비우지 않으므로 정리 직후 대량 페이지 재로딩(부하 급증)이 없다.
        private static void RunSafeClean(StepTally tally)
        {
            EnablePrivilege(SE_PROFILE_SINGLE_NAME);
            tally.Add(PurgeSystemMemory(MemoryPurgeLowPriorityStandbyList));
        }

        private static void RunFullClean(StepTally tally)
        {
            // 1) 필요한 권한 활성화
            //    SystemMemoryListInformation → SeProfileSingleProcessPrivilege
            //    SystemFileCacheInformation  → SeIncreaseQuotaPrivilege
            EnablePrivilege(SE_INCREASE_QUOTA_NAME);
            EnablePrivilege(SE_PROFILE_SINGLE_NAME);

            // (과거의 프로세스별 EmptyWorkingSet 루프는 제거했다. kernel32.dll에는
            //  "EmptyWorkingSet" export가 없어(K32EmptyWorkingSet만 존재) 매 프로세스마다
            //  EntryPointNotFoundException이 발생했고, CloseHandle이 건너뛰어져 프로세스
            //  핸들이 누수됐다. 아래 MemoryEmptyWorkingSets가 시스템 전체에 동일 작업을 한다.)

            // 2) 모든 프로세스 워킹셋 강제 비우기 (시스템 전체)
            tally.Add(PurgeSystemMemory(MemoryEmptyWorkingSets));

            // 3) Modified 리스트 → Standby로 이동
            tally.Add(PurgeSystemMemory(MemoryFlushModifiedList));

            // 4) 낮은 우선순위 Standby 해제
            tally.Add(PurgeSystemMemory(MemoryPurgeLowPriorityStandbyList));

            // 5) 전체 Standby 리스트 → 가용 메모리로 해제 (핵심)
            tally.Add(PurgeSystemMemory(MemoryPurgeStandbyList));

            // 6) 파일 시스템 캐시 플러시
            tally.Add(FlushFileCache());
        }

        // 단계별 NTSTATUS 집계: 권한 실패와 그 외 실패를 구분한다.
        private sealed class StepTally
        {
            public bool PrivilegeFailed;
            public bool OtherFailed;

            public void Add(uint status)
            {
                if (status == STATUS_SUCCESS) return;
                if (status == STATUS_PRIVILEGE_NOT_HELD || status == STATUS_ACCESS_DENIED)
                    PrivilegeFailed = true;
                else
                    OtherFailed = true;
            }
        }

        // AC1.1: NtSetSystemInformation의 NTSTATUS를 로깅하고 그대로 반환한다.
        private static uint PurgeSystemMemory(int command)
        {
            IntPtr ptr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(ptr, command);
                uint status = NtSetSystemInformation(SystemMemoryListInformation, ptr, sizeof(int));
                Log($"[MemoryHelper] NtSetSystemInformation(0x{command:X}) → 0x{status:X8} ({DescribeStatus(status)})");
                return status;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        // AC1.1: 파일 캐시 플러시(SeIncreaseQuotaPrivilege 필요)의 NTSTATUS를 로깅하고 반환한다.
        private static uint FlushFileCache()
        {
            var info = new SYSTEM_FILECACHE_INFORMATION();
            info.MinimumWorkingSet = new IntPtr(-1);
            info.MaximumWorkingSet = new IntPtr(-1);
            int size = Marshal.SizeOf(info);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                uint status = NtSetSystemInformation(SystemFileCacheInformation, ptr, size);
                Log($"[MemoryHelper] NtSetSystemInformation(0x{SystemFileCacheInformation:X}) → 0x{status:X8} ({DescribeStatus(status)})");
                return status;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static void EnablePrivilege(string privilegeName)
        {
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(),
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                    return;

                if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                {
                    CloseHandle(token);
                    return;
                }

                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new LUID_AND_ATTRIBUTES
                    {
                        Luid = luid,
                        Attributes = SE_PRIVILEGE_ENABLED
                    }
                };

                // AC1.3: AdjustTokenPrivileges는 권한이 일부만 할당돼도 true를 반환하므로
                // 호출 직후 GetLastError로 ERROR_NOT_ALL_ASSIGNED(0x514) 등을 확인한다.
                bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                int err = Marshal.GetLastWin32Error();
                if (!ok || err != 0)
                    Log($"[MemoryHelper] EnablePrivilege({privilegeName}) failed: 0x{err:X8}");

                CloseHandle(token);
            }
            catch (Exception ex)
            {
                Log($"[MemoryHelper] EnablePrivilege({privilegeName}) 예외: {ex.Message}");
            }
        }

        public static TimeSpan GetCooldownRemaining()
        {
            var elapsed   = MonotonicClock.Now - _lastCleanTime;
            var remaining = TimeSpan.FromSeconds(CooldownSeconds) - elapsed;
            return remaining.TotalSeconds > 0 ? remaining : TimeSpan.Zero;
        }

        // ── 프로세스 목록 ────────────────────────────────────────

        public static List<ProcessMemoryInfo> GetTopProcesses(int count = 20)
        {
            var list = new List<ProcessMemoryInfo>();
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    list.Add(new ProcessMemoryInfo
                    {
                        PID         = proc.Id,
                        Name        = proc.ProcessName,
                        WorkingSet  = proc.WorkingSet64,
                        PrivateBytes = proc.PrivateMemorySize64
                    });
                }
                catch (InvalidOperationException) { /* 열거 직후 종료된 프로세스 — 목록에서 제외 */ }
                finally { proc.Dispose(); }
            }

            list.Sort((a, b) => b.WorkingSet.CompareTo(a.WorkingSet));
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
            return list;
        }

        // ── 포맷 ────────────────────────────────────────────────

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            if (bytes >= 1024 * 1024)         return $"{bytes / (1024.0 * 1024):F0} MB";
            if (bytes >= 1024)                return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
