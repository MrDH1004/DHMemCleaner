using System;
using System.Collections.Generic;
using System.Threading;
using System.Timers;

namespace MemoryCleaner.Core
{
    public class CleanEventArgs : EventArgs
    {
        public CleanHistoryEntry Entry { get; set; }
        public bool ShowNotification { get; set; }
    }

    public static class AutoCleanManager
    {
        private static System.Timers.Timer _monitorTimer;
        // 타이머 스레드가 읽는 설정. Start/UpdateSettings가 받은 객체를 그대로 쓰지 않고
        // 복제본(Clone)을 저장한다 — UI 스레드는 원본 인스턴스를 계속 수정하므로, 복제해야
        // 틱 처리 도중 값이 바뀌지 않는다. 참조 교체는 volatile로 원자적으로 보인다.
        private static volatile AppSettings _settings;
        private static DateTime _lastTimedClean = DateTime.MinValue;

        // PerformClean 재진입 가드. 0=유휴, 1=정리 중. 여러 스레드에서 동시 호출 방지.
        private static int _cleaning = 0;

        // AC3.2: 직전 틱에서 풀스크린(게임 모드)이었는지 추적.
        // true→false 전환(게임 종료) 시 시간 기반 정리 시계를 리셋하고, 임계치 트리거도
        // GameExitGraceMinutes 동안 보류하여 작업 전환을 위한 유예 시간을 확보한다.
        // (게임 직후 RAM이 높은 상태에서 즉시 정리 — 특히 강력 정리 — 가 돌지 않게)
        private static bool _wasFullscreen = false;
        private const int GameExitGraceMinutes = 2;
        private static DateTime _thresholdGraceUntil = DateTime.MinValue;

        // 모든 내부 시각은 MonotonicClock 기준(시스템 시계 변경에 영향받지 않음).
        private static DateTime Now => MonotonicClock.Now;

        // 사용률 임계치 트리거 백오프.
        // 정리해도 사용률이 임계치 아래로 안 내려가면(실사용 메모리라 회수 불가) 기존엔
        // 쿨다운(60초)마다 정리를 무한 반복했다. 정리 후에도 임계치 이상이면 다음 임계치
        // 정리를 5분 → 10분 → … 최대 60분 뒤로 미루고, 사용률이 임계치 아래로 내려가면 리셋한다.
        private const int ThresholdBackoffStartMinutes = 5;
        private const int ThresholdBackoffMaxMinutes   = 60;
        private static int _thresholdBackoffMinutes = 0;
        private static DateTime _nextThresholdCleanAllowed = DateTime.MinValue;

        public static readonly List<CleanHistoryEntry> History = new List<CleanHistoryEntry>();
        private static readonly object _historyLock = new object();

        public static event EventHandler<CleanEventArgs> CleanPerformed;

        public static void Start(AppSettings settings)
        {
            _settings = settings.Clone();
            _lastTimedClean = Now;  // 앱 시작 직후 즉시 정리 방지
            _thresholdBackoffMinutes = 0;
            _nextThresholdCleanAllowed = DateTime.MinValue;
            _thresholdGraceUntil = DateTime.MinValue;

            // 모니터링 타이머 (3초마다 체크)
            _monitorTimer = new System.Timers.Timer(3000);
            _monitorTimer.Elapsed += OnMonitorTick;
            _monitorTimer.AutoReset = true;
            _monitorTimer.Start();

            // 메모리 누수 감지기 시작
            if (_settings.EnableLeakDetector)
                ProcessLeakTracker.Start();
        }

        public static void UpdateSettings(AppSettings settings)
        {
            _settings = settings.Clone();
            // 누수 감지기 토글 반영
            if (_settings.EnableLeakDetector)
                ProcessLeakTracker.Start();
            else
                ProcessLeakTracker.Stop();
        }

        public static void Stop()
        {
            _monitorTimer?.Stop();
            _monitorTimer?.Dispose();
            ProcessLeakTracker.Stop();
        }

        // 마지막으로 게임 모드로 인해 자동정리를 스킵했는지 (UI 표시용)
        public static bool LastTickSkippedForFullscreen { get; private set; }
        public static string LastFullscreenProcessName { get; private set; }

        private static void OnMonitorTick(object sender, ElapsedEventArgs e)
        {
            // 타이머 스레드와 UI 스레드가 _settings 참조를 공유하므로 시작 시점에
            // 로컬로 캡처하여 틱 처리 도중 참조 교체로 인한 불일치를 방지한다.
            var settings = _settings;
            if (settings == null) return;

            // 게임 모드: 풀스크린 앱이 활성이면 자동정리 스킵
            string fsName = null;
            bool isFullscreen = settings.PauseOnFullscreen
                && FullscreenDetector.IsForegroundFullscreen(out fsName);

            // AC3.2: 게임 모드 종료 감지 (이전 틱은 풀스크린, 현재 틱은 아님).
            // 시간 기반 정리 시계를 현재 시각으로 리셋하여, 풀스크린 종료 직후
            // 즉시 정리되지 않고 다음 인터벌만큼의 유예 시간을 갖도록 한다.
            if (_wasFullscreen && !isFullscreen)
            {
                _lastTimedClean = Now;
                _thresholdGraceUntil = Now.AddMinutes(GameExitGraceMinutes);
            }
            _wasFullscreen = isFullscreen;

            if (isFullscreen)
            {
                LastTickSkippedForFullscreen = true;
                LastFullscreenProcessName = fsName;
                return;
            }
            LastTickSkippedForFullscreen = false;
            LastFullscreenProcessName = null;

            var info = MemoryHelper.GetMemoryInfo();
            DateTime now = Now;

            // 임계치 트리거 허용 시각 = 백오프 시각과 게임 종료 유예 중 늦은 쪽
            DateTime thresholdAllowedAt = _nextThresholdCleanAllowed > _thresholdGraceUntil
                ? _nextThresholdCleanAllowed : _thresholdGraceUntil;

            var decision = Decide(settings, info, now, _lastTimedClean,
                thresholdAllowedAt, MemoryHelper.GetCooldownRemaining());

            // 임계치 아래로 내려옴 → 백오프 리셋
            if (decision.ThresholdTrigger == null)
            {
                _thresholdBackoffMinutes = 0;
                _nextThresholdCleanAllowed = DateTime.MinValue;
            }

            if (!decision.ShouldClean) return;

            // PerformClean이 실제로 정리한 경우에만 시계/백오프 상태를 갱신한다.
            // (쿨다운·재진입 가드로 건너뛴 경우 상태를 건드리면 정리 없이 백오프만 늘어난다)
            if (!PerformClean(decision.Trigger)) return;

            if (decision.IsTimed)
                _lastTimedClean = Now;

            if (decision.ThresholdTrigger != null &&
                GetThresholdTrigger(settings, MemoryHelper.GetMemoryInfo()) != null)
            {
                _thresholdBackoffMinutes = NextBackoffMinutes(_thresholdBackoffMinutes);
                _nextThresholdCleanAllowed = Now.AddMinutes(_thresholdBackoffMinutes);
                System.Diagnostics.Trace.WriteLine(
                    $"[AutoClean] 정리 후에도 임계치 이상 — 다음 임계치 정리 {_thresholdBackoffMinutes}분 보류");
            }
        }

        // ── 틱 판정 (순수 함수 — 단위 테스트 대상) ─────────────────

        internal readonly struct TickDecision
        {
            public TickDecision(bool shouldClean, string trigger, bool isTimed, string thresholdTrigger)
            {
                ShouldClean = shouldClean;
                Trigger = trigger;
                IsTimed = isTimed;
                ThresholdTrigger = thresholdTrigger;
            }

            public bool ShouldClean { get; }
            public string Trigger { get; }
            public bool IsTimed { get; }
            // 현재 사용률이 넘은 임계치 트리거(백오프·쿨다운과 무관). 없으면 null.
            public string ThresholdTrigger { get; }
        }

        // 한 틱에서 정리할지 결정한다. 상태를 바꾸지 않는다.
        //  - 임계치 트리거: 임계치 초과 && 쿨다운 해제 && 백오프 시각 경과
        //  - 시간 트리거(AC3.1): 간격 경과 && 쿨다운 해제. 둘 다 해당하면 시간 트리거가 이름을 가진다.
        internal static TickDecision Decide(AppSettings s, MemoryInfo info, DateTime now,
            DateTime lastTimedClean, DateTime nextThresholdAllowed, TimeSpan cooldownRemaining)
        {
            string thresholdTrigger = GetThresholdTrigger(s, info);
            bool cooldownOver = cooldownRemaining <= TimeSpan.Zero;

            bool thresholdDue = thresholdTrigger != null && cooldownOver && now >= nextThresholdAllowed;
            bool timedDue = s.AutoCleanByTime && cooldownOver
                && (now - lastTimedClean).TotalMinutes >= s.AutoCleanIntervalMinutes;

            if (timedDue)
                return new TickDecision(true, "자동(시간)", true, thresholdTrigger);
            if (thresholdDue)
                return new TickDecision(true, thresholdTrigger, false, thresholdTrigger);
            return new TickDecision(false, null, false, thresholdTrigger);
        }

        // 사용률이 넘은 첫 임계치의 트리거 이름. 넘은 게 없으면 null.
        internal static string GetThresholdTrigger(AppSettings s, MemoryInfo info)
        {
            if (s.AutoCleanPhysical && info.PhysicalPercent >= s.AutoCleanPhysicalThreshold)
                return "자동(실제 메모리)";
            if (s.AutoCleanVirtual && info.VirtualPercent >= s.AutoCleanVirtualThreshold)
                return "자동(가상 메모리)";
            if (s.AutoCleanWorkingSet && info.WorkingSetPercent >= s.AutoCleanWorkingSetThreshold)
                return "자동(워킹셋)";
            return null;
        }

        // 백오프 진행: 0 → 5 → 10 → 20 → 40 → 60(상한)
        internal static int NextBackoffMinutes(int current)
            => current <= 0
                ? ThresholdBackoffStartMinutes
                : Math.Min(current * 2, ThresholdBackoffMaxMinutes);

        // 스레드 안전한 History 스냅샷 반환. 외부는 이 메서드로만 History를 읽는다.
        public static List<CleanHistoryEntry> GetHistorySnapshot()
        {
            lock (_historyLock)
                return new List<CleanHistoryEntry>(History);
        }

        // 스레드 안전한 History 초기화. 외부는 이 메서드로만 History를 비운다.
        public static void ClearHistory()
        {
            lock (_historyLock)
                History.Clear();
        }

        // 실제로 정리를 수행했으면 true. 재진입 가드나 쿨다운으로 건너뛰면 false.
        // bypassCooldown: 사용자가 직접 누른 정리(수동·트레이)만 true로 넘긴다.
        //   트리거 이름 문자열로 판단하지 않는다(이름 변경·오타 시 우회가 조용히 꺼지는 결합 제거).
        //   강력 정리 모드에서는 MemoryHelper가 bypass를 무시하고 쿨다운을 지킨다.
        public static bool PerformClean(string trigger, bool bypassCooldown = false)
        {
            // 재진입 가드: 타이머/수동(Task.Run)/트레이 등 여러 스레드에서 동시 호출될 수
            // 있으므로 이미 정리 중이면 즉시 반환한다. 락으로 전체를 감싸면 UI가 블로킹될 수
            // 있어 CompareExchange 기반 가드를 사용한다.
            if (Interlocked.CompareExchange(ref _cleaning, 1, 0) != 0)
                return false;
            try
            {
                var before = MemoryHelper.GetMemoryInfo();
                bool fullClean = _settings?.FullClean ?? false;
                bool cleaned = MemoryHelper.CleanMemory(bypassCooldown, fullClean);
                if (!cleaned) return false;

                var after = MemoryHelper.GetMemoryInfo();

                // AC1.2: 권한 부족으로 경량 정리(GC)만 수행된 경우 Trigger에 태그 추가.
                // 권한 외 원인으로 실패한 단계가 있으면 별도 태그 — 실패를 정상 정리로 기록하지 않는다.
                string effectiveTrigger = trigger;
                if (MemoryHelper.LastCleanPrivilegeFailed)
                    effectiveTrigger += CleanHistoryEntry.PrivilegeFallbackTag;
                if (MemoryHelper.LastCleanStepFailed)
                    effectiveTrigger += CleanHistoryEntry.StepFailureTag;

                var entry = new CleanHistoryEntry
                {
                    Time = DateTime.Now,   // 표시용 실제 시각
                    Trigger = effectiveTrigger,
                    PartiallyFailed = MemoryHelper.LastCleanPrivilegeFailed || MemoryHelper.LastCleanStepFailed,
                    BeforePhysical = before.PhysicalPercent,
                    AfterPhysical = after.PhysicalPercent,
                    BeforeVirtual = before.VirtualPercent,
                    AfterVirtual = after.VirtualPercent,
                    BeforeWorkingSet = before.WorkingSetPercent,
                    AfterWorkingSet = after.WorkingSetPercent,
                };

                lock (_historyLock)
                {
                    History.Insert(0, entry);
                    if (History.Count > 200) History.RemoveAt(History.Count - 1);
                }

                CleanPerformed?.Invoke(null, new CleanEventArgs
                {
                    Entry = entry,
                    ShowNotification = _settings?.ShowCleanResultNotification ?? true
                });
                return true;
            }
            finally
            {
                Interlocked.Exchange(ref _cleaning, 0);
            }
        }
    }
}
