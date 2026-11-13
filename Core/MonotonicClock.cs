using System;
using System.Diagnostics;

namespace MemoryCleaner.Core
{
    /// <summary>
    /// 시스템 시계 변경(NTP 동기화·수동 변경·서머타임)에 영향받지 않는 단조 시각.
    /// 쿨다운·시간 트리거·백오프처럼 "경과 시간"만 의미 있는 곳에서 DateTime.Now 대신 쓴다.
    /// 반환값은 차이 계산 전용이며, 화면에 표시할 실제 시각이 아니다.
    /// </summary>
    internal static class MonotonicClock
    {
        private static readonly DateTime Epoch = DateTime.UtcNow;
        private static readonly Stopwatch Watch = Stopwatch.StartNew();

        public static DateTime Now => Epoch + Watch.Elapsed;
    }
}
