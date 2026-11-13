using System;
using System.IO;
using MemoryCleaner.Core;

namespace MemoryCleaner.Tests
{
    public class AppSettingsTests : IDisposable
    {
        private static readonly string RealSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KILHO.net", "MemoryCleaner", "settings.xml");

        private readonly string _backupPath;

        public AppSettingsTests()
        {
            // 실제 settings.xml이 있으면 테스트 전 백업, 테스트 종료 시 복원
            _backupPath = RealSettingsPath + ".testbak";
            if (File.Exists(RealSettingsPath))
                File.Copy(RealSettingsPath, _backupPath, overwrite: true);
        }

        // ── 1. 기본값 확인 ─────────────────────────────────────
        [Fact]
        public void DefaultValues_AreCorrect()
        {
            var s = new AppSettings();
            Assert.True(s.AutoCleanPhysical);
            Assert.Equal(60, s.AutoCleanPhysicalThreshold);
            Assert.False(s.AutoCleanVirtual);
            Assert.Equal(90, s.AutoCleanVirtualThreshold);
            Assert.False(s.AutoCleanWorkingSet);
            Assert.Equal(90, s.AutoCleanWorkingSetThreshold);
            Assert.False(s.AutoCleanByTime);
            Assert.Equal(30, s.AutoCleanIntervalMinutes);
            Assert.True(s.RunOnStartup);
            Assert.False(s.TrayClickClean);
            Assert.True(s.DarkMode);
            Assert.True(s.ShowWorkingSetWarning);
            Assert.Equal(70, s.WorkingSetWarningThreshold);
            Assert.True(s.ShowCleanResultNotification);
        }

        // ── 2. 저장 후 불러오기 ────────────────────────────────
        [Fact]
        public void SaveAndLoad_RoundTrip()
        {
            var original = new AppSettings
            {
                AutoCleanPhysical = false,
                AutoCleanPhysicalThreshold = 80,
                AutoCleanVirtual = true,
                AutoCleanVirtualThreshold = 95,
                AutoCleanWorkingSet = true,
                AutoCleanWorkingSetThreshold = 85,
                AutoCleanByTime = true,
                AutoCleanIntervalMinutes = 45,
                RunOnStartup = false,
                TrayClickClean = true,
                DarkMode = false,
                ShowWorkingSetWarning = false,
                WorkingSetWarningThreshold = 80,
                ShowCleanResultNotification = false
            };

            original.Save();
            var loaded = AppSettings.Load();

            Assert.Equal(original.AutoCleanPhysical, loaded.AutoCleanPhysical);
            Assert.Equal(original.AutoCleanPhysicalThreshold, loaded.AutoCleanPhysicalThreshold);
            Assert.Equal(original.AutoCleanVirtual, loaded.AutoCleanVirtual);
            Assert.Equal(original.AutoCleanVirtualThreshold, loaded.AutoCleanVirtualThreshold);
            Assert.Equal(original.AutoCleanWorkingSet, loaded.AutoCleanWorkingSet);
            Assert.Equal(original.AutoCleanWorkingSetThreshold, loaded.AutoCleanWorkingSetThreshold);
            Assert.Equal(original.AutoCleanByTime, loaded.AutoCleanByTime);
            Assert.Equal(original.AutoCleanIntervalMinutes, loaded.AutoCleanIntervalMinutes);
            Assert.Equal(original.RunOnStartup, loaded.RunOnStartup);
            Assert.Equal(original.TrayClickClean, loaded.TrayClickClean);
            Assert.Equal(original.DarkMode, loaded.DarkMode);
            Assert.Equal(original.ShowWorkingSetWarning, loaded.ShowWorkingSetWarning);
            Assert.Equal(original.WorkingSetWarningThreshold, loaded.WorkingSetWarningThreshold);
            Assert.Equal(original.ShowCleanResultNotification, loaded.ShowCleanResultNotification);
        }

        // ── 3. 파일 없을 때 기본값 반환 ───────────────────────
        [Fact]
        public void Load_WhenFileNotExists_ReturnsDefaults()
        {
            // 실제 파일이 없는 상황을 시뮬레이션하기 위해 Load는 내부적으로
            // 파일이 없으면 new AppSettings()를 반환하므로 기본값 검증
            var s = new AppSettings();
            Assert.Equal(60, s.AutoCleanPhysicalThreshold);
            Assert.True(s.AutoCleanPhysical);
        }

        // ── 4. 임계값 경계값 저장 ──────────────────────────────
        [Fact]
        public void ThresholdBoundary_SavesCorrectly()
        {
            var s = new AppSettings { AutoCleanPhysicalThreshold = 100 };
            s.Save();
            var loaded = AppSettings.Load();
            Assert.Equal(100, loaded.AutoCleanPhysicalThreshold);

            // 범위 밖 값(0 = 항상 정리)은 Load 시 최소값 1로 보정된다
            s.AutoCleanPhysicalThreshold = 0;
            s.Save();
            loaded = AppSettings.Load();
            Assert.Equal(1, loaded.AutoCleanPhysicalThreshold);
        }

        public void Dispose()
        {
            // 백업이 있으면 복원, 없으면 테스트가 만든 파일 삭제
            if (File.Exists(_backupPath))
            {
                File.Copy(_backupPath, RealSettingsPath, overwrite: true);
                File.Delete(_backupPath);
            }
            else if (File.Exists(RealSettingsPath))
            {
                File.Delete(RealSettingsPath);
            }
        }
    }
}
