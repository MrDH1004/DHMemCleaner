using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using MemoryCleaner.Core;

namespace MemoryCleaner.Tests
{
    // MemoryHelper의 전역 static 쿨다운(_lastCleanTime)을 건드리므로 통합 테스트와
    // 동일 컬렉션으로 직렬화하여 병렬 간섭을 방지한다.
    [Collection("MemoryHelperCooldown")]
    public class MemoryHelperTests
    {
        // AC3.3: xUnit은 각 테스트마다 생성자를 호출하므로, 여기서 전역 static
        // 쿨다운 상태를 초기화하여 테스트 간 상태 오염을 방지한다(테스트 격리).
        public MemoryHelperTests()
        {
            MemoryHelper.ResetLastCleanTime();
        }

        // ── GetMemoryInfo ────────────────────────────────────────

        [Fact]
        public void GetMemoryInfo_PhysicalTotal_IsPositive()
        {
            var info = MemoryHelper.GetMemoryInfo();
            Assert.True(info.PhysicalTotal > 0, "PhysicalTotal 이 0보다 커야 합니다");
        }

        [Fact]
        public void GetMemoryInfo_PhysicalPercent_InRange()
        {
            var info = MemoryHelper.GetMemoryInfo();
            Assert.InRange(info.PhysicalPercent, 0.0, 100.0);
        }

        [Fact]
        public void GetMemoryInfo_VirtualPercent_InRange()
        {
            var info = MemoryHelper.GetMemoryInfo();
            Assert.InRange(info.VirtualPercent, 0.0, 100.0);
        }

        [Fact]
        public void GetMemoryInfo_WorkingSetPercent_InRange()
        {
            var info = MemoryHelper.GetMemoryInfo();
            Assert.InRange(info.WorkingSetPercent, 0.0, 200.0); // WS가 RAM을 넘을 수도 있음
        }

        [Fact]
        public void GetMemoryInfo_PhysicalUsed_LessThanOrEqualTotal()
        {
            var info = MemoryHelper.GetMemoryInfo();
            Assert.True(info.PhysicalUsed <= info.PhysicalTotal,
                $"PhysicalUsed({info.PhysicalUsed}) > PhysicalTotal({info.PhysicalTotal})");
        }

        [Fact]
        public void GetMemoryInfo_VirtualUsed_LessThanOrEqualTotal()
        {
            var info = MemoryHelper.GetMemoryInfo();
            Assert.True(info.VirtualUsed <= info.VirtualTotal,
                $"VirtualUsed({info.VirtualUsed}) > VirtualTotal({info.VirtualTotal})");
        }

        [Fact]
        public void GetMemoryInfo_PercentMatchesRatio()
        {
            var info = MemoryHelper.GetMemoryInfo();
            double expected = (double)info.PhysicalUsed / info.PhysicalTotal * 100.0;
            Assert.Equal(expected, info.PhysicalPercent, precision: 5);
        }

        // ── FormatBytes ──────────────────────────────────────────

        [Theory]
        [InlineData(500, "500 B")]
        [InlineData(1024, "1 KB")]
        [InlineData(1024 * 1024, "1 MB")]
        [InlineData(1024L * 1024 * 1024, "1.0 GB")]
        public void FormatBytes_ReturnsCorrectUnit(long bytes, string expected)
        {
            Assert.Equal(expected, MemoryHelper.FormatBytes(bytes));
        }

        // ── CleanMemory 쿨다운 ───────────────────────────────────

        [Fact]
        public void CleanMemory_CooldownPreventsSecondCall()
        {
            // 첫 번째 호출 (강제 bypass)
            bool first = MemoryHelper.CleanMemory(forceCooldownBypass: true);
            Assert.True(first, "첫 번째 정리는 성공해야 합니다");

            // 쿨다운 중이므로 두 번째는 실패
            bool second = MemoryHelper.CleanMemory(forceCooldownBypass: false);
            Assert.False(second, "쿨다운 중에는 false를 반환해야 합니다");
        }

        [Fact]
        public void CleanMemory_ForceBypass_AlwaysSucceeds()
        {
            bool r1 = MemoryHelper.CleanMemory(forceCooldownBypass: true);
            bool r2 = MemoryHelper.CleanMemory(forceCooldownBypass: true);
            Assert.True(r1);
            Assert.True(r2);
        }

        // ── GetCooldownRemaining ─────────────────────────────────

        [Fact]
        public void GetCooldownRemaining_AfterClean_IsPositive()
        {
            MemoryHelper.CleanMemory(forceCooldownBypass: true);
            var rem = MemoryHelper.GetCooldownRemaining();
            Assert.True(rem.TotalSeconds > 0, "정리 직후 쿨다운이 남아 있어야 합니다");
        }

        [Fact]
        public void GetCooldownRemaining_IsWithin60Seconds()
        {
            MemoryHelper.CleanMemory(forceCooldownBypass: true);
            var rem = MemoryHelper.GetCooldownRemaining();
            Assert.True(rem.TotalSeconds <= 60);
        }

        // ── GetTopProcesses ──────────────────────────────────────

        [Fact]
        public void GetTopProcesses_ReturnsRequestedCount()
        {
            var list = MemoryHelper.GetTopProcesses(10);
            Assert.True(list.Count <= 10);
        }

        [Fact]
        public void GetTopProcesses_SortedByWorkingSetDescending()
        {
            var list = MemoryHelper.GetTopProcesses(20);
            for (int i = 1; i < list.Count; i++)
                Assert.True(list[i - 1].WorkingSet >= list[i].WorkingSet,
                    "WorkingSet 내림차순 정렬이어야 합니다");
        }

        [Fact]
        public void GetTopProcesses_AllWorkingSetPositive()
        {
            var list = MemoryHelper.GetTopProcesses(20);
            foreach (var p in list)
                Assert.True(p.WorkingSet >= 0, $"{p.Name} WorkingSet이 음수입니다");
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Stage 2 통합 테스트 (AC2.3)
    //
    // 설계 노트 (구현 제약):
    //   NtSetSystemInformation은 MemoryHelper의 private P/Invoke이고 Core 코드는
    //   수정 불가하므로 Moq/서브스티튜트로 STATUS_PRIVILEGE_NOT_HELD를 강제할 수 없다.
    //   대신, 비관리자(비상승) 권한 환경에서 NtSetSystemInformation(Standby purge 등)이
    //   실제로 0xC0000061(STATUS_PRIVILEGE_NOT_HELD) / 0xC0000022를 반환하는 실 경로를
    //   사용한다. MemoryHelper.Log는 Trace.WriteLine로 출력하므로, TraceListener를
    //   붙여 디버그 로그에 해당 NTSTATUS(0xC0000061)가 기록되는지 캡처한다.
    //
    //   환경이 상승(관리자) 권한이면 NtSetSystemInformation이 성공(STATUS_SUCCESS)하여
    //   권한 실패 분기를 타지 않는다. 이 경우에도 AC1.2 계약상 CleanMemory는 true를
    //   반환해야 하므로, 본 테스트는 "CleanMemory(false)는 항상 true(부분 성공)를 반환"을
    //   핵심 단언으로 두고, 비상승 환경에서만 로그에 0xC0000061이 포함됨을 추가 검증한다.
    // ════════════════════════════════════════════════════════════════
    [Collection("MemoryHelperCooldown")]
    public class MemoryHelperTests_Integration
    {
        // AC3.3: 테스트 격리 — 전역 static 쿨다운 상태 초기화.
        public MemoryHelperTests_Integration()
        {
            MemoryHelper.ResetLastCleanTime();
        }

        // AC2.3: 비관리자 권한에서 Standby/Cache 정리가 권한 부족으로 실패해도
        //        CleanMemory는 경량 정리(GC)로 폴백하여 true(부분 성공)를 반환한다.
        //        그리고 디버그 로그에 NTSTATUS 0xC0000061이 기록된다(비상승 환경).
        [Fact]
        public void MemoryHelperTests_Integration_NonElevated_PartialClean()
        {
            // MemoryHelper.Log → Trace.WriteLine 이므로 TraceListener로 캡처한다.
            var sb = new StringBuilder();
            var listener = new TextWriterTraceListener(new System.IO.StringWriter(sb));
            Trace.Listeners.Add(listener);
            try
            {
                // 쿨다운을 우회하여 실제 정리 경로(NtSetSystemInformation 호출 포함)를 강제.
                bool result = MemoryHelper.CleanMemory(forceCooldownBypass: true);

                // AC1.2/AC2.3 핵심 계약: 권한 부족이어도 부분 성공으로 true 반환.
                Assert.True(result, "CleanMemory는 권한 부족 시에도 부분 성공(true)을 반환해야 한다");
            }
            finally
            {
                Trace.Flush();
                Trace.Listeners.Remove(listener);
                listener.Dispose();
            }

            string log = sb.ToString();

            // 권한 실패가 발생한 경우(비상승 환경): 로그에 0xC0000061이 포함되고
            // LastCleanPrivilegeFailed가 true여야 한다.
            // 상승(관리자) 환경에서는 NtSetSystemInformation이 성공하여 권한 실패가 없을 수 있다.
            if (MemoryHelper.LastCleanPrivilegeFailed)
            {
                Assert.Contains("0xC0000061", log,
                    StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // 상승 환경: 권한 실패 분기를 타지 않았으므로 0xC0000061 로그는 없을 수 있다.
                // 이 경우에도 NtSetSystemInformation 호출 자체는 로깅되어야 한다(NTSTATUS 기록).
                Assert.Contains("NtSetSystemInformation", log);
            }
        }
    }

    // xUnit 컬렉션 정의: MemoryHelper의 전역 static 쿨다운(_lastCleanTime) 상태를
    // 공유하는 테스트 클래스들을 직렬화하여 병렬 간섭을 방지한다.
    [CollectionDefinition("MemoryHelperCooldown", DisableParallelization = true)]
    public class MemoryHelperCooldownCollection { }
}
