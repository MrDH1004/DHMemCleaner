using System;
using System.Linq;
using MemoryCleaner.Core;
using Xunit;

namespace MemoryCleaner.Tests
{
    public class ProcessLeakTrackerTests
    {
        public ProcessLeakTrackerTests()
        {
            // 테스트 간 상태 격리
            ProcessLeakTracker.Stop();
            ProcessLeakTracker.Reset();
        }

        // ── 1. 샘플이 없으면 결과도 비어있음 ─────────────────────
        [Fact]
        public void DetectLeaks_NoSamples_ReturnsEmpty()
        {
            var result = ProcessLeakTracker.DetectLeaks();
            Assert.Empty(result);
        }

        // ── 2. 샘플 1개로는 누수 판정 불가 (관측시간 계산 불능) ──
        [Fact]
        public void DetectLeaks_SingleSample_NotIncluded()
        {
            ProcessLeakTracker.InjectSampleForTest(
                1234, "test.exe", 100L * 1024 * 1024, DateTime.UtcNow);
            var result = ProcessLeakTracker.DetectLeaks();
            Assert.Empty(result);
        }

        // ── 3. 짧은 관측시간 (5분) — 의심 분류 안 됨 ─────────────
        [Fact]
        public void DetectLeaks_ShortObservation_NotSuspect()
        {
            var t0 = DateTime.UtcNow.AddMinutes(-5);
            ProcessLeakTracker.InjectSampleForTest(2001, "short.exe", 100L * 1024 * 1024, t0);
            ProcessLeakTracker.InjectSampleForTest(2001, "short.exe", 500L * 1024 * 1024, DateTime.UtcNow);

            var result = ProcessLeakTracker.DetectLeaks();
            var match = result.FirstOrDefault(r => r.PID == 2001);
            Assert.NotNull(match);
            Assert.False(match.IsSuspect, "5분 관측은 의심 분류되면 안 됨");
        }

        // ── 4. 충분히 관측 + 큰 증가 → 의심 분류 ────────────────
        [Fact]
        public void DetectLeaks_LongObservationLargeGrowth_IsSuspect()
        {
            var t0 = DateTime.UtcNow.AddMinutes(-20);
            ProcessLeakTracker.InjectSampleForTest(3001, "leak.exe", 100L * 1024 * 1024, t0);
            ProcessLeakTracker.InjectSampleForTest(3001, "leak.exe", 500L * 1024 * 1024, DateTime.UtcNow);

            var result = ProcessLeakTracker.DetectLeaks();
            var match = result.FirstOrDefault(r => r.PID == 3001);
            Assert.NotNull(match);
            Assert.True(match.IsSuspect, "20분 관측 + 400MB 증가는 의심이어야 함");
            Assert.Equal(400L * 1024 * 1024, match.DeltaBytes);
        }

        // ── 5. 메모리가 감소한 프로세스는 의심 아님 ────────────
        [Fact]
        public void DetectLeaks_DecreasingMemory_NotSuspect()
        {
            var t0 = DateTime.UtcNow.AddMinutes(-20);
            ProcessLeakTracker.InjectSampleForTest(4001, "ok.exe", 800L * 1024 * 1024, t0);
            ProcessLeakTracker.InjectSampleForTest(4001, "ok.exe", 300L * 1024 * 1024, DateTime.UtcNow);

            var result = ProcessLeakTracker.DetectLeaks();
            var match = result.FirstOrDefault(r => r.PID == 4001);
            Assert.NotNull(match);
            Assert.False(match.IsSuspect, "메모리 감소는 의심 아님");
            Assert.True(match.DeltaBytes < 0);
        }

        // ── 6. 임계치 미달 (200MB 증가) → 의심 아님 ────────────
        [Fact]
        public void DetectLeaks_SmallGrowth_NotSuspect()
        {
            var t0 = DateTime.UtcNow.AddMinutes(-20);
            ProcessLeakTracker.InjectSampleForTest(5001, "small.exe", 100L * 1024 * 1024, t0);
            ProcessLeakTracker.InjectSampleForTest(5001, "small.exe", 300L * 1024 * 1024, DateTime.UtcNow);

            var result = ProcessLeakTracker.DetectLeaks();
            var match = result.FirstOrDefault(r => r.PID == 5001);
            Assert.NotNull(match);
            Assert.False(match.IsSuspect, "200MB 증가는 임계치(300MB) 미달");
        }

        // ── 7. 증가량 큰 순으로 정렬 ────────────────────────────
        [Fact]
        public void DetectLeaks_SortedByDeltaDescending()
        {
            var t0 = DateTime.UtcNow.AddMinutes(-20);
            ProcessLeakTracker.InjectSampleForTest(6001, "a.exe", 100L * 1024 * 1024, t0);
            ProcessLeakTracker.InjectSampleForTest(6001, "a.exe", 200L * 1024 * 1024, DateTime.UtcNow);

            ProcessLeakTracker.InjectSampleForTest(6002, "b.exe", 100L * 1024 * 1024, t0);
            ProcessLeakTracker.InjectSampleForTest(6002, "b.exe", 600L * 1024 * 1024, DateTime.UtcNow);

            ProcessLeakTracker.InjectSampleForTest(6003, "c.exe", 100L * 1024 * 1024, t0);
            ProcessLeakTracker.InjectSampleForTest(6003, "c.exe", 350L * 1024 * 1024, DateTime.UtcNow);

            var result = ProcessLeakTracker.DetectLeaks();
            // b > c > a 순으로 정렬되어야 함
            Assert.Equal(6002, result[0].PID);
            Assert.Equal(6003, result[1].PID);
            Assert.Equal(6001, result[2].PID);
        }

        // ── 8. Reset()으로 트랙 클리어 ───────────────────────────
        [Fact]
        public void Reset_ClearsAllTracks()
        {
            ProcessLeakTracker.InjectSampleForTest(7001, "test.exe", 100, DateTime.UtcNow);
            ProcessLeakTracker.Reset();
            var result = ProcessLeakTracker.DetectLeaks();
            Assert.DoesNotContain(result, r => r.PID == 7001);
        }

        // ── 9. BytesPerMinute 계산 정확성 ──────────────────────
        [Fact]
        public void DetectLeaks_BytesPerMinute_CalculatedCorrectly()
        {
            // 정확히 10분 동안 400MB 증가 = 40MB/min
            var t0 = DateTime.UtcNow.AddMinutes(-10);
            ProcessLeakTracker.InjectSampleForTest(
                8001, "rate.exe", 100L * 1024 * 1024, t0);
            ProcessLeakTracker.InjectSampleForTest(
                8001, "rate.exe", 500L * 1024 * 1024, DateTime.UtcNow);

            var match = ProcessLeakTracker.DetectLeaks().First(r => r.PID == 8001);
            // 40 MB/min ± 1MB 허용
            double expectedMbPerMin = 40.0;
            double actualMbPerMin = match.BytesPerMinute / (1024.0 * 1024.0);
            Assert.InRange(actualMbPerMin, expectedMbPerMin - 1, expectedMbPerMin + 1);
        }

        // ── 10. LeakDetected 이벤트 발생 검증 (직접 호출은 어려우므로 데이터만 검증) ──
        [Fact]
        public void DetectLeaks_SuspectInfo_HasAllFieldsPopulated()
        {
            var t0 = DateTime.UtcNow.AddMinutes(-20);
            ProcessLeakTracker.InjectSampleForTest(9001, "full.exe", 50L * 1024 * 1024, t0);
            ProcessLeakTracker.InjectSampleForTest(9001, "full.exe", 600L * 1024 * 1024, DateTime.UtcNow);

            var match = ProcessLeakTracker.DetectLeaks().First(r => r.PID == 9001);
            Assert.Equal("full.exe", match.Name);
            Assert.Equal(9001, match.PID);
            Assert.True(match.IsSuspect);
            Assert.True(match.MinutesObserved >= 19.5);
            Assert.True(match.DeltaBytes > 0);
            Assert.True(match.BytesPerMinute > 0);
        }
    }
}
