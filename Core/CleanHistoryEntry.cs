using System;

namespace MemoryCleaner.Core
{
    public class CleanHistoryEntry
    {
        // AC1.2: 권한 부족으로 경량 정리(GC)만 수행한 경우 Trigger 끝에 붙는 태그.
        public const string PrivilegeFallbackTag = " [권한 부족 - 경량 정리]";

        // 권한 외 원인(NTSTATUS)으로 일부 정리 단계가 실패한 경우 Trigger 끝에 붙는 태그.
        public const string StepFailureTag = " [일부 단계 실패]";

        // 정리 단계 중 하나라도 실패했는지(권한 부족 또는 그 외). 알림 문구 분기에 쓴다.
        public bool PartiallyFailed { get; set; }

        public DateTime Time { get; set; }
        public string Trigger { get; set; }   // "수동", "자동(사용률)", "자동(시간)", "트레이" (+ 권한 부족 태그 가능)
        public double BeforePhysical { get; set; }
        public double AfterPhysical { get; set; }
        public double BeforeVirtual { get; set; }
        public double AfterVirtual { get; set; }
        public double BeforeWorkingSet { get; set; }
        public double AfterWorkingSet { get; set; }

        public double PhysicalSaved => BeforePhysical - AfterPhysical;
        public string Summary => $"실제 메모리 {BeforePhysical:F1}% → {AfterPhysical:F1}% (절약 {PhysicalSaved:F1}%)";
    }
}
