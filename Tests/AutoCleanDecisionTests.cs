using System;
using MemoryCleaner.Core;

namespace MemoryCleaner.Tests
{
    // AutoCleanManager.Decide / GetThresholdTrigger / NextBackoffMinutes 는 상태가 없는
    // 순수 함수라 실제 메모리·타이머 없이 틱 판정 로직을 그대로 검증한다.
    // (OnMonitorTick은 이 함수의 결과대로만 동작한다.)
    public class AutoCleanDecisionTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 14, 12, 0, 0);

        private static AppSettings Settings(bool physical = false, int physThresh = 80,
            bool virt = false, int virtThresh = 90, bool ws = false, int wsThresh = 90,
            bool byTime = false, int interval = 30)
            => new AppSettings
            {
                AutoCleanPhysical = physical, AutoCleanPhysicalThreshold = physThresh,
                AutoCleanVirtual = virt, AutoCleanVirtualThreshold = virtThresh,
                AutoCleanWorkingSet = ws, AutoCleanWorkingSetThreshold = wsThresh,
                AutoCleanByTime = byTime, AutoCleanIntervalMinutes = interval
            };

        private static MemoryInfo Mem(double phys = 50, double virt = 50, double ws = 50)
            => new MemoryInfo { PhysicalPercent = phys, VirtualPercent = virt, WorkingSetPercent = ws };

        private static AutoCleanManager.TickDecision Decide(AppSettings s, MemoryInfo m,
            DateTime? lastTimed = null, DateTime? nextAllowed = null, TimeSpan? cooldown = null)
            => AutoCleanManager.Decide(s, m, Now,
                lastTimed ?? Now, nextAllowed ?? DateTime.MinValue, cooldown ?? TimeSpan.Zero);

        // ── 임계치 트리거 ────────────────────────────────────────

        [Fact]
        public void BelowThreshold_NoClean_NoThresholdTrigger()
        {
            var d = Decide(Settings(physical: true, physThresh: 80), Mem(phys: 79.9));
            Assert.False(d.ShouldClean);
            Assert.Null(d.ThresholdTrigger);
        }

        [Fact]
        public void OverThreshold_CooldownOver_Cleans()
        {
            var d = Decide(Settings(physical: true, physThresh: 80), Mem(phys: 80));
            Assert.True(d.ShouldClean);
            Assert.False(d.IsTimed);
            Assert.Equal("자동(실제 메모리)", d.Trigger);
        }

        [Fact]
        public void OverThreshold_DuringCooldown_DoesNotClean()
        {
            // 수동 정리 직후(쿨다운 중)에는 임계치 트리거도 PerformClean을 부르지 않는다.
            // (과거: 쿨다운 중 호출 → 정리 없이 백오프만 증가하던 버그)
            var d = Decide(Settings(physical: true, physThresh: 80), Mem(phys: 95),
                cooldown: TimeSpan.FromSeconds(30));
            Assert.False(d.ShouldClean);
            Assert.Equal("자동(실제 메모리)", d.ThresholdTrigger); // 백오프 리셋 방지용으로는 여전히 보고
        }

        [Fact]
        public void OverThreshold_DuringBackoff_DoesNotClean()
        {
            var d = Decide(Settings(physical: true, physThresh: 80), Mem(phys: 95),
                nextAllowed: Now.AddMinutes(5));
            Assert.False(d.ShouldClean);
        }

        [Fact]
        public void OverThreshold_BackoffElapsed_Cleans()
        {
            var d = Decide(Settings(physical: true, physThresh: 80), Mem(phys: 95),
                nextAllowed: Now);
            Assert.True(d.ShouldClean);
        }

        [Fact]
        public void ThresholdTrigger_PriorityIsPhysicalThenVirtualThenWorkingSet()
        {
            var all = Settings(physical: true, physThresh: 80, virt: true, virtThresh: 80, ws: true, wsThresh: 80);
            Assert.Equal("자동(실제 메모리)", AutoCleanManager.GetThresholdTrigger(all, Mem(90, 90, 90)));
            Assert.Equal("자동(가상 메모리)", AutoCleanManager.GetThresholdTrigger(all, Mem(10, 90, 90)));
            Assert.Equal("자동(워킹셋)",     AutoCleanManager.GetThresholdTrigger(all, Mem(10, 10, 90)));
            Assert.Null(AutoCleanManager.GetThresholdTrigger(all, Mem(10, 10, 10)));
        }

        [Fact]
        public void DisabledThreshold_IsIgnored()
        {
            Assert.Null(AutoCleanManager.GetThresholdTrigger(Settings(physical: false), Mem(phys: 99)));
        }

        // ── 시간 트리거 (AC3.1) ──────────────────────────────────

        [Fact]
        public void Timed_IntervalElapsed_CooldownOver_Cleans()
        {
            var d = Decide(Settings(byTime: true, interval: 30), Mem(),
                lastTimed: Now.AddMinutes(-30));
            Assert.True(d.ShouldClean);
            Assert.True(d.IsTimed);
            Assert.Equal("자동(시간)", d.Trigger);
        }

        [Fact]
        public void Timed_IntervalNotElapsed_DoesNotClean()
        {
            var d = Decide(Settings(byTime: true, interval: 30), Mem(),
                lastTimed: Now.AddMinutes(-29.9));
            Assert.False(d.ShouldClean);
        }

        [Fact]
        public void Timed_DuringCooldown_DoesNotClean()
        {
            var d = Decide(Settings(byTime: true, interval: 1), Mem(),
                lastTimed: Now.AddMinutes(-5), cooldown: TimeSpan.FromSeconds(1));
            Assert.False(d.ShouldClean);
        }

        [Fact]
        public void Timed_TakesPrecedenceOverThreshold_AndKeepsThresholdTrigger()
        {
            var d = Decide(Settings(physical: true, physThresh: 80, byTime: true, interval: 30),
                Mem(phys: 95), lastTimed: Now.AddMinutes(-31));
            Assert.True(d.IsTimed);
            Assert.Equal("자동(시간)", d.Trigger);
            Assert.Equal("자동(실제 메모리)", d.ThresholdTrigger);
        }

        // 60초 쿨다운 동안 3초 틱 20회 → 시간 트리거가 한 번도 발동하지 않는다.
        [Fact]
        public void Timed_TwentyTicksInsideCooldown_NeverCleans()
        {
            var s = Settings(byTime: true, interval: 1);
            DateTime cleanedAt = Now;
            int cleans = 0;
            for (int tick = 1; tick <= 19; tick++)
            {
                DateTime t = cleanedAt.AddSeconds(3 * tick);
                TimeSpan cooldown = TimeSpan.FromSeconds(60) - (t - cleanedAt);
                var d = AutoCleanManager.Decide(s, Mem(), t, cleanedAt.AddMinutes(-5),
                    DateTime.MinValue, cooldown);
                if (d.ShouldClean) cleans++;
            }
            Assert.Equal(0, cleans);

            // 쿨다운 해제 시점(60초)에는 발동한다
            DateTime after = cleanedAt.AddSeconds(60);
            Assert.True(AutoCleanManager.Decide(s, Mem(), after, cleanedAt.AddMinutes(-5),
                DateTime.MinValue, TimeSpan.Zero).ShouldClean);
        }

        // ── 백오프 ───────────────────────────────────────────────

        [Fact]
        public void Backoff_Progression_CapsAt60()
        {
            int m = 0;
            var seen = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 7; i++) { m = AutoCleanManager.NextBackoffMinutes(m); seen.Add(m); }
            Assert.Equal(new[] { 5, 10, 20, 40, 60, 60, 60 }, seen);
        }

        // ── 설정 복제 ────────────────────────────────────────────

        [Fact]
        public void Clone_IsIndependentCopy()
        {
            var original = Settings(physical: true, physThresh: 70);
            var copy = original.Clone();
            original.AutoCleanPhysicalThreshold = 40;
            original.FullClean = true;

            Assert.NotSame(original, copy);
            Assert.Equal(70, copy.AutoCleanPhysicalThreshold);
            Assert.False(copy.FullClean);
        }
    }
}
