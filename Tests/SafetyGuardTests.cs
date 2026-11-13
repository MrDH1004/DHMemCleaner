using System;
using MemoryCleaner.Core;

namespace MemoryCleaner.Tests
{
    // 설정 범위 보정 / 단조 시계 — 상태 없는 검증
    public class SettingsNormalizeTests
    {
        [Fact]
        public void Normalize_ClampsOutOfRangeValues()
        {
            var s = new AppSettings
            {
                AutoCleanPhysicalThreshold = 0,
                AutoCleanVirtualThreshold = 150,
                AutoCleanWorkingSetThreshold = -5,
                WorkingSetWarningThreshold = 101,
                AutoCleanIntervalMinutes = 0
            };
            s.Normalize();

            Assert.Equal(1, s.AutoCleanPhysicalThreshold);
            Assert.Equal(100, s.AutoCleanVirtualThreshold);
            Assert.Equal(1, s.AutoCleanWorkingSetThreshold);
            Assert.Equal(100, s.WorkingSetWarningThreshold);
            Assert.Equal(1, s.AutoCleanIntervalMinutes);
        }

        [Fact]
        public void Normalize_KeepsValuesNotInComboList()
        {
            // 85처럼 콤보 목록에 없는 유효 값은 그대로 유지해야 한다(덮어쓰기 금지)
            var s = new AppSettings { AutoCleanPhysicalThreshold = 85, AutoCleanIntervalMinutes = 25 };
            s.Normalize();
            Assert.Equal(85, s.AutoCleanPhysicalThreshold);
            Assert.Equal(25, s.AutoCleanIntervalMinutes);
        }

        [Fact]
        public void DefaultSettings_AreNotFallback_AndCloneKeepsFlag()
        {
            var s = new AppSettings();
            Assert.False(s.LoadedFromFallback);
            Assert.False(s.Clone().LoadedFromFallback);
        }

        [Fact]
        public void MonotonicClock_NeverGoesBackwards()
        {
            DateTime prev = MonotonicClock.Now;
            for (int i = 0; i < 1000; i++)
            {
                DateTime now = MonotonicClock.Now;
                Assert.True(now >= prev);
                prev = now;
            }
        }
    }

    // 쿨다운 전역 상태를 쓰므로 직렬 컬렉션
    [Collection("MemoryHelperCooldown")]
    public class FullCleanCooldownTests
    {
        // 강력 정리는 수동(bypass)이라도 쿨다운을 지켜야 한다(트레이 연타 방지).
        // 안전 모드 정리로 쿨다운을 먼저 걸어 두므로, 강력 정리는 실제로 실행되지 않는다.
        [Fact]
        public void FullClean_IgnoresBypass_DuringCooldown()
        {
            MemoryHelper.ResetLastCleanTime();
            Assert.True(MemoryHelper.CleanMemory(forceCooldownBypass: true, fullClean: false));

            bool fullRan = MemoryHelper.CleanMemory(forceCooldownBypass: true, fullClean: true);

            Assert.False(fullRan);
            Assert.True(MemoryHelper.GetCooldownRemaining() > TimeSpan.Zero);
        }

        [Fact]
        public void SafeClean_HonorsBypass_DuringCooldown()
        {
            MemoryHelper.ResetLastCleanTime();
            Assert.True(MemoryHelper.CleanMemory(forceCooldownBypass: true));
            Assert.True(MemoryHelper.CleanMemory(forceCooldownBypass: true));
        }
    }
}
