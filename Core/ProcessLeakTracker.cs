using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Timers;

namespace MemoryCleaner.Core
{
    public class ProcessSample
    {
        public DateTime Time { get; set; }
        public long WorkingSet { get; set; }
    }

    public class ProcessLeakInfo
    {
        public int PID { get; set; }
        public string Name { get; set; }
        public long CurrentWorkingSet { get; set; }
        public long BaselineWorkingSet { get; set; }
        public long DeltaBytes => CurrentWorkingSet - BaselineWorkingSet;
        public double MinutesObserved { get; set; }
        public double BytesPerMinute { get; set; }
        public bool IsSuspect { get; set; }
    }

    /// <summary>
    /// 프로세스별 워킹셋 추이를 추적하여 메모리 누수 의심 프로세스를 감지.
    /// - 샘플링 주기: 30초
    /// - 히스토리 유지: 최근 30분
    /// - 누수 판단: 15분 이상 관측 && 누적 증가량 >= 300MB && 회귀 기울기 > 0 && R² >= 0.5
    /// </summary>
    public static class ProcessLeakTracker
    {
        // 튜닝 상수
        private const int SampleIntervalMs        = 30 * 1000;   // 30초
        private const int HistoryRetentionMinutes = 30;
        private const int MinObservationMinutes   = 15;
        private const long LeakThresholdBytes     = 300L * 1024 * 1024; // 300MB
        // 견고성 조건: 회귀 적합도(결정계수) 최소 기준 — 노이즈성 스파이크 배제.
        // 표본이 2개면 R²=1.0 이므로 최소 표본 시나리오는 그대로 통과.
        private const double MinRSquared          = 0.5;

        private static readonly Dictionary<int, ProcessTrack> _tracks =
            new Dictionary<int, ProcessTrack>();
        private static readonly object _lock = new object();
        private static System.Timers.Timer _timer;
        private static volatile bool _enabled;

        private class ProcessTrack
        {
            public int PID;
            public string Name;
            public List<ProcessSample> Samples = new List<ProcessSample>();
        }

        public static event EventHandler<ProcessLeakInfo> LeakDetected;

        public static void Start()
        {
            if (_timer != null) return;
            _enabled = true;
            // 시작 직후 1회 즉시 샘플링하면 baseline이 잡히고, 이후 주기적으로 갱신됨
            SampleNow();
            _timer = new System.Timers.Timer(SampleIntervalMs) { AutoReset = true };
            // System.Timers.Timer는 Elapsed 예외를 조용히 삼키므로, 원인을 로그로 남긴다.
            _timer.Elapsed += (s, e) =>
            {
                try { SampleNow(); }
                catch (InvalidOperationException ex)
                {
                    Trace.WriteLine($"[LeakTracker] 샘플링 실패: {ex.Message}");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    Trace.WriteLine($"[LeakTracker] 프로세스 열거 실패: {ex.NativeErrorCode} {ex.Message}");
                }
            };
            _timer.Start();
        }

        public static void Stop()
        {
            _enabled = false;
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
        }

        public static void Reset()
        {
            lock (_lock) _tracks.Clear();
        }

        private static void SampleNow()
        {
            if (!_enabled) return;
            var now = DateTime.UtcNow;
            var procs = Process.GetProcesses();
            var seenPids = new HashSet<int>();

            foreach (var p in procs)
            {
                try
                {
                    long ws = p.WorkingSet64;
                    int pid = p.Id;
                    string name = p.ProcessName;
                    seenPids.Add(pid);

                    lock (_lock)
                    {
                        if (!_tracks.TryGetValue(pid, out var track))
                        {
                            track = new ProcessTrack { PID = pid, Name = name };
                            _tracks[pid] = track;
                        }
                        else if (!string.Equals(track.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            // PID 재사용 방어: 종료된 프로세스의 PID가 새 프로세스에
                            // 재할당된 경우 이전 트랙이 오염되므로 초기화 후 이름 갱신.
                            track.Samples.Clear();
                            track.Name = name;
                        }
                        track.Samples.Add(new ProcessSample { Time = now, WorkingSet = ws });

                        // 보존 기간 초과한 샘플 제거
                        var cutoff = now.AddMinutes(-HistoryRetentionMinutes);
                        track.Samples.RemoveAll(s => s.Time < cutoff);
                    }
                }
                catch (InvalidOperationException) { /* 열거 직후 종료된 프로세스 — 이번 샘플에서 제외 */ }
                finally { p.Dispose(); }
            }

            // 종료된 프로세스 트랙 제거
            lock (_lock)
            {
                var deadPids = _tracks.Keys.Where(k => !seenPids.Contains(k)).ToList();
                foreach (var pid in deadPids) _tracks.Remove(pid);
            }

            // 누수 검사 및 이벤트 발생
            foreach (var info in DetectLeaks())
            {
                if (info.IsSuspect)
                    LeakDetected?.Invoke(null, info);
            }
        }

        /// <summary>
        /// 현재 추적 중인 모든 프로세스의 누수 정보를 반환 (의심 여부 포함).
        /// 의심 기준: MinObservationMinutes 이상 관측 && Delta >= LeakThresholdBytes
        ///           && 최소제곱 회귀 기울기 > 0 && 결정계수(R²) >= MinRSquared
        /// BytesPerMinute 는 첫/끝 델타가 아니라 전체 샘플에 대한 최소제곱 회귀 기울기(bytes/min).
        /// </summary>
        public static List<ProcessLeakInfo> DetectLeaks()
        {
            var result = new List<ProcessLeakInfo>();
            lock (_lock)
            {
                foreach (var kv in _tracks)
                {
                    var track = kv.Value;
                    if (track.Samples.Count < 2) continue;

                    var first = track.Samples[0];
                    var last  = track.Samples[track.Samples.Count - 1];
                    double minutes = (last.Time - first.Time).TotalMinutes;
                    if (minutes <= 0) continue;

                    long delta = last.WorkingSet - first.WorkingSet;

                    // 전체 샘플에 대한 최소제곱 선형회귀 (x=경과분, y=워킹셋 바이트).
                    // slope = Sxy/Sxx (bytes/min), rSquared = Sxy²/(Sxx·Syy).
                    double slope = ComputeRegression(track.Samples, first.Time, out double rSquared);

                    var info = new ProcessLeakInfo
                    {
                        PID                  = track.PID,
                        Name                 = track.Name,
                        CurrentWorkingSet    = last.WorkingSet,
                        BaselineWorkingSet   = first.WorkingSet,
                        MinutesObserved      = minutes,
                        BytesPerMinute       = slope,
                        IsSuspect = minutes >= MinObservationMinutes
                                    && delta >= LeakThresholdBytes
                                    && slope > 0
                                    && rSquared >= MinRSquared
                    };
                    result.Add(info);
                }
            }
            // 누적 증가량 기준 내림차순
            result.Sort((a, b) => b.DeltaBytes.CompareTo(a.DeltaBytes));
            return result;
        }

        /// <summary>
        /// 표본에 대한 최소제곱 선형회귀 기울기(bytes/min)를 계산하고 결정계수 R²를 반환.
        /// x축은 origin 기준 경과 분(minute), y축은 워킹셋 바이트.
        /// Sxx==0(모든 시각 동일) 또는 Syy==0(값 변화 없음)이면 R²=0.
        /// </summary>
        private static double ComputeRegression(List<ProcessSample> samples, DateTime origin, out double rSquared)
        {
            int n = samples.Count;
            double sumX = 0, sumY = 0;
            for (int i = 0; i < n; i++)
            {
                double x = (samples[i].Time - origin).TotalMinutes;
                double y = samples[i].WorkingSet;
                sumX += x;
                sumY += y;
            }
            double meanX = sumX / n;
            double meanY = sumY / n;

            double sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = (samples[i].Time - origin).TotalMinutes - meanX;
                double dy = samples[i].WorkingSet - meanY;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }

            if (sxx <= 0)
            {
                rSquared = 0;
                return 0;
            }

            double slope = sxy / sxx;
            rSquared = (syy > 0) ? (sxy * sxy) / (sxx * syy) : 0;
            return slope;
        }

        /// <summary>
        /// 테스트/외부 주입용 — 단위 테스트에서 시간 흐름을 시뮬레이션할 때 사용.
        /// </summary>
        public static void InjectSampleForTest(int pid, string name, long workingSet, DateTime time)
        {
            lock (_lock)
            {
                if (!_tracks.TryGetValue(pid, out var track))
                {
                    track = new ProcessTrack { PID = pid, Name = name };
                    _tracks[pid] = track;
                }
                else if (!string.Equals(track.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    // 이름 불일치 시 PID 재사용으로 간주해 트랙 리셋. 이름이 같으면 기존 동작 유지.
                    track.Samples.Clear();
                    track.Name = name;
                }
                track.Samples.Add(new ProcessSample { Time = time, WorkingSet = workingSet });
            }
        }
    }
}
