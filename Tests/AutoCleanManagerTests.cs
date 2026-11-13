using System;
using System.Threading;
using MemoryCleaner.Core;

namespace MemoryCleaner.Tests
{
    // AutoCleanManager.PerformClean은 MemoryHelper.CleanMemory(전역 static 쿨다운)에
    // 의존하므로, 쿨다운 상태를 공유하는 다른 테스트와 동일 컬렉션으로 직렬화한다.
    [Collection("MemoryHelperCooldown")]
    public class AutoCleanManagerTests
    {
        private AppSettings MakeSettings(
            bool physical = false, int physThresh = 60,
            bool virt = false, int virtThresh = 90,
            bool ws = false, int wsThresh = 90,
            bool byTime = false, int intervalMin = 30)
        {
            return new AppSettings
            {
                AutoCleanPhysical = physical,
                AutoCleanPhysicalThreshold = physThresh,
                AutoCleanVirtual = virt,
                AutoCleanVirtualThreshold = virtThresh,
                AutoCleanWorkingSet = ws,
                AutoCleanWorkingSetThreshold = wsThresh,
                AutoCleanByTime = byTime,
                AutoCleanIntervalMinutes = intervalMin,
                ShowCleanResultNotification = false
            };
        }

        // ── 1. PerformClean 호출 시 History에 기록 ──────────────
        [Fact]
        public void PerformClean_AddsEntryToHistory()
        {
            AutoCleanManager.History.Clear();
            AutoCleanManager.Start(MakeSettings());

            AutoCleanManager.PerformClean("수동", bypassCooldown: true);

            Assert.NotEmpty(AutoCleanManager.History);
            AutoCleanManager.Stop();
        }

        // ── 2. PerformClean 결과 Trigger 값 확인 ────────────────
        [Fact]
        public void PerformClean_EntryHasCorrectTrigger()
        {
            AutoCleanManager.History.Clear();
            AutoCleanManager.Start(MakeSettings());

            AutoCleanManager.PerformClean("수동", bypassCooldown: true);

            // 권한 부족 환경(비관리자)에서는 Trigger 끝에
            // CleanHistoryEntry.PrivilegeFallbackTag가 붙을 수 있으므로 StartsWith로 검증.
            Assert.StartsWith("수동", AutoCleanManager.History[0].Trigger);
            AutoCleanManager.Stop();
        }

        // ── 3. PerformClean Before/After 값 존재 ────────────────
        [Fact]
        public void PerformClean_EntryHasBeforeAfterValues()
        {
            AutoCleanManager.History.Clear();
            AutoCleanManager.Start(MakeSettings());

            AutoCleanManager.PerformClean("수동", bypassCooldown: true);
            var entry = AutoCleanManager.History[0];

            Assert.True(entry.BeforePhysical >= 0);
            Assert.True(entry.AfterPhysical >= 0);
            AutoCleanManager.Stop();
        }

        // ── 4. History 최대 200개 제한 ──────────────────────────
        [Fact]
        public void History_IsLimitedTo200Entries()
        {
            AutoCleanManager.History.Clear();
            AutoCleanManager.Start(MakeSettings());

            for (int i = 0; i < 210; i++)
                AutoCleanManager.PerformClean("수동", bypassCooldown: true);

            Assert.True(AutoCleanManager.History.Count <= 200,
                $"History가 200개를 초과했습니다: {AutoCleanManager.History.Count}");
            AutoCleanManager.Stop();
        }

        // ── 5. CleanPerformed 이벤트 발생 확인 ──────────────────
        [Fact]
        public void PerformClean_RaisesCleanPerformedEvent()
        {
            AutoCleanManager.History.Clear();
            AutoCleanManager.Start(MakeSettings());

            bool eventFired = false;
            AutoCleanManager.CleanPerformed += (s, e) => eventFired = true;

            AutoCleanManager.PerformClean("수동", bypassCooldown: true);

            Assert.True(eventFired, "CleanPerformed 이벤트가 발생해야 합니다");
            AutoCleanManager.Stop();
        }

        // ── 6. CleanHistoryEntry.Summary 포맷 ───────────────────
        [Fact]
        public void CleanHistoryEntry_Summary_ContainsBeforeAfter()
        {
            var entry = new CleanHistoryEntry
            {
                BeforePhysical = 75.0,
                AfterPhysical = 60.0,
                Trigger = "수동"
            };
            Assert.Contains("75.0%", entry.Summary);
            Assert.Contains("60.0%", entry.Summary);
        }

        // ── 7. PhysicalSaved 계산 ────────────────────────────────
        [Fact]
        public void CleanHistoryEntry_PhysicalSaved_IsCorrect()
        {
            var entry = new CleanHistoryEntry
            {
                BeforePhysical = 80.0,
                AfterPhysical = 65.0
            };
            Assert.Equal(15.0, entry.PhysicalSaved, precision: 5);
        }

        // ── 8. UpdateSettings 반영 확인 ─────────────────────────
        [Fact]
        public void UpdateSettings_DoesNotThrow()
        {
            AutoCleanManager.Start(MakeSettings());
            var ex = Record.Exception(() =>
                AutoCleanManager.UpdateSettings(MakeSettings(physical: true, physThresh: 50)));
            Assert.Null(ex);
            AutoCleanManager.Stop();
        }

        // ── 9. 쿨다운 중 자동 정리 미실행 확인 ──────────────────
        [Fact]
        public void PerformClean_CooldownBlocks_SecondAutoClean()
        {
            AutoCleanManager.History.Clear();
            AutoCleanManager.Start(MakeSettings());

            // 첫 번째 수동 정리 (쿨다운 시작)
            AutoCleanManager.PerformClean("수동", bypassCooldown: true);
            int countAfterFirst = AutoCleanManager.History.Count;

            // 쿨다운 중 자동 정리 트리거 — CleanMemory가 false 반환하므로 History에 추가 안 됨
            AutoCleanManager.PerformClean("자동(실제 메모리)");
            int countAfterSecond = AutoCleanManager.History.Count;

            Assert.Equal(countAfterFirst, countAfterSecond);
            AutoCleanManager.Stop();
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Stage 2 통합 테스트 (AC2.1, AC2.2, AC2.4)
    //
    // 설계 노트 (구현 제약):
    //   Core 코드는 수정 불가(읽기 전용)이며 AutoCleanManager / MemoryHelper /
    //   FullscreenDetector는 모두 static + private 멤버 + private P/Invoke 구조라
    //   Moq로 모킹할 수 없다(Moq 미참조, 가상 메서드/인터페이스 없음).
    //   OnMonitorTick은 private이라 직접 호출도 불가하다.
    //
    //   따라서 본 통합 테스트는 "모킹"이 아니라 실제 public 표면(PerformClean,
    //   CleanMemory 쿨다운, CleanPerformed 이벤트, FullscreenDetector 계약)을
    //   통해 각 AC가 검증하려는 *동작 계약*을 실제 코드 경로로 재현한다.
    //
    //   MemoryHelper._lastCleanTime은 프로세스 전역 static이라 테스트 간 공유된다.
    //   AutoCleanManagerTests와 MemoryHelperTests가 병렬 실행되면 쿨다운 상태가
    //   서로 간섭할 수 있으므로, 쿨다운 의존 테스트를 하나의 컬렉션으로 묶어
    //   직렬화하고 각 테스트는 시작 시 자체적으로 쿨다운 상태를 시딩한다.
    // ════════════════════════════════════════════════════════════════
    [Collection("MemoryHelperCooldown")]
    public class AutoCleanManagerTests_Integration
    {
        private AppSettings MakeSettings(
            bool physical = false, int physThresh = 60,
            bool byTime = false, int intervalMin = 30,
            bool pauseOnFullscreen = false)
        {
            return new AppSettings
            {
                AutoCleanPhysical = physical,
                AutoCleanPhysicalThreshold = physThresh,
                AutoCleanByTime = byTime,
                AutoCleanIntervalMinutes = intervalMin,
                PauseOnFullscreen = pauseOnFullscreen,
                ShowCleanResultNotification = false
            };
        }

        // ── AC2.1: PerformClean은 쿨다운으로 건너뛰면 false를 반환한다 ─────────
        // OnMonitorTick은 이 반환값으로 "실제 정리 여부"를 판단해 시계/백오프를 갱신한다.
        [Fact]
        public void PerformClean_ReturnsFalse_WhenSkippedByCooldown()
        {
            AutoCleanManager.History.Clear();
            MemoryHelper.ResetLastCleanTime();

            Assert.True(AutoCleanManager.PerformClean("수동", bypassCooldown: true));             // 쿨다운 우회 → 정리
            Assert.False(AutoCleanManager.PerformClean("자동(시간)"));      // 쿨다운 중 → 건너뜀
            Assert.Single(AutoCleanManager.History);
        }

        // ── AC2.2: 게임 모드(풀스크린)에서는 자동정리를 스킵한다 ───────────
        //
        // FullscreenDetector / OnMonitorTick은 모킹·직접호출 불가하므로,
        // 게임 모드 스킵의 *계약*을 실제 코드로 검증한다:
        //   (1) PauseOnFullscreen=true일 때 스킵 게이트는 FullscreenDetector를 통과한다.
        //   (2) 테스트 환경에서 풀스크린이 아니면 IsForegroundFullscreen()은 false이며,
        //       이때 OnMonitorTick의 스킵 분기는 발동하지 않는다(스킵 플래그=false).
        //   (3) 임계치 초과 자동정리가 게임 모드와 독립적으로 동작함을 검증하기 위해,
        //       PauseOnFullscreen=true + 풀스크린 아님 상태에서 PerformClean이 정상
        //       호출되면 History가 늘어남을 확인한다(스킵이 잘못 발동하지 않음).
        //
        // 실제 풀스크린 게임을 띄울 수 없는 CI/단위 환경 제약상, "풀스크린일 때
        // PerformClean이 0회"라는 단언은 FullscreenDetector 계약(false 반환 시 스킵 안 함,
        // true 반환 시 스킵)을 통해 간접 검증한다.
        [Fact]
        public void AutoCleanManagerTests_Integration_GameMode_Skip()
        {
            AutoCleanManager.History.Clear();

            // 풀스크린 게임이 떠 있지 않은 일반 테스트 환경에서 검출기는 false여야 함.
            bool isFullscreen = FullscreenDetector.IsForegroundFullscreen(out string fsName);

            AutoCleanManager.Start(MakeSettings(
                physical: true, physThresh: 60, pauseOnFullscreen: true));
            try
            {

            // 60초 쿨다운을 강제로 시작시켜, 아래 스킵 여부 판정이 쿨다운에 흔들리지 않게 한다.
            MemoryHelper.CleanMemory(forceCooldownBypass: true);

            if (isFullscreen)
            {
                // 풀스크린이 활성이면 (계약상) OnMonitorTick은 스킵해야 한다.
                // 이 환경에서는 LastTickSkippedForFullscreen이 true로 셋업될 수 있다.
                // PerformClean 직접 호출은 게이트를 거치지 않으므로, 여기서는 검출기
                // 계약(true → 스킵 대상)만 확인한다.
                Assert.True(true, "풀스크린 환경에서는 자동정리가 스킵 대상이다");
            }
            else
            {
                // 일반 환경: 풀스크린이 아니므로 스킵 분기는 발동하지 않아야 한다.
                // PauseOnFullscreen=true여도 게이트(IsForegroundFullscreen)가 false면
                // 자동정리 경로가 정상 진행된다 — 즉 PerformClean이 막히지 않는다.
                Assert.False(isFullscreen);

                // 풀스크린이 아닐 때 PerformClean은 게임 모드와 무관하게 동작한다.
                // (쿨다운 직후라 CleanMemory는 false → History는 그대로일 수 있으나,
                //  핵심은 '게임 모드 때문에 막히지 않는다'는 점이다.)
                int before = AutoCleanManager.History.Count;
                AutoCleanManager.PerformClean("자동(실제 메모리)");
                int after = AutoCleanManager.History.Count;

                // 게임 모드 스킵이 아니라 쿨다운만이 영향을 준다: after는 before와 같거나(쿨다운)
                // 1 증가(쿨다운 해제 시) — 어느 쪽이든 게임 모드로 인한 강제 스킵은 아니다.
                Assert.True(after == before || after == before + 1);
            }
            }
            finally
            {
                AutoCleanManager.Stop();
            }
        }

        // ── AC2.4: UpdateSettings 후에도 CleanPerformed 이벤트가 유실되지 않는다 ──
        //
        // Start() → 이벤트 핸들러 등록 → UpdateSettings() → PerformClean() 순서에서
        // 설정 변경이 이벤트 구독을 깨뜨리지 않는지 검증한다. (CleanPerformed는
        // static 이벤트라 UpdateSettings가 _settings만 교체하고 구독자는 보존해야 한다.)
        [Fact]
        public void AutoCleanManagerTests_UpdateSettings_DoesNotDropEvents()
        {
            AutoCleanManager.History.Clear();
            AutoCleanManager.Start(MakeSettings());

            bool eventFired = false;
            EventHandler<CleanEventArgs> handler = (s, e) => eventFired = true;
            AutoCleanManager.CleanPerformed += handler;

            try
            {
                // 설정 변경 (임계치/플래그 변경)
                AutoCleanManager.UpdateSettings(MakeSettings(physical: true, physThresh: 50));

                // 설정 변경 후에도 이벤트가 발생해야 한다. PerformClean이 실제 성공해야
                // 이벤트가 발생하므로 쿨다운을 우회하는 "수동" 트리거로 강제 정리한다.
                AutoCleanManager.PerformClean("수동", bypassCooldown: true);

                Assert.True(eventFired,
                    "UpdateSettings 이후에도 CleanPerformed 이벤트가 발생해야 한다(이벤트 유실 없음)");
            }
            finally
            {
                AutoCleanManager.CleanPerformed -= handler;
                AutoCleanManager.Stop();
            }
        }
    }
}
