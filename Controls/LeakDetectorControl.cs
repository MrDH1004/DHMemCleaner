using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using MemoryCleaner.Core;
using MemoryCleaner.UI;

namespace MemoryCleaner.Controls
{
    public class LeakDetectorControl : UserControl
    {
        private ListView _list;
        private Label _lblStatus;
        private System.Timers.Timer _refreshTimer;

        public LeakDetectorControl()
        {
            BackColor = DarkTheme.Background;
            Dock = DockStyle.Fill;
            Padding = new Padding(20);
            BuildUI();
            StartRefresh();
        }

        private void BuildUI()
        {
            // 3행 TableLayoutPanel로 명확한 수직 레이아웃 강제
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = DarkTheme.Background
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // 제목
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));   // 상태
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 리스트

            var lblTitle = new Label
            {
                Text = "메모리 누수 감지",
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = DarkTheme.TextPrimary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = DarkTheme.Background
            };

            _lblStatus = new Label
            {
                Text = "관측 중… (최소 15분 이상 데이터가 쌓이면 분석됩니다)",
                Font = new Font("Segoe UI", 9f),
                ForeColor = DarkTheme.TextSecondary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = DarkTheme.Background
            };

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = DarkTheme.Card,
                ForeColor = DarkTheme.TextPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9f),
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _list.Columns.Add("프로세스", 180);
            _list.Columns.Add("PID", 65);
            _list.Columns.Add("현재", 100);
            _list.Columns.Add("증가량", 110);
            _list.Columns.Add("증가율", 120);
            _list.Columns.Add("관측시간", 90);
            _list.Columns.Add("판정", 75);

            // 의심 프로세스 대응 액션 (레이아웃/컬럼은 그대로, 우클릭 메뉴로 제공)
            var menu = new ContextMenuStrip();
            var miClean = new ToolStripMenuItem("선택 프로세스 워킹셋 정리");
            miClean.Click += (s, e) => CleanSelected();
            menu.Items.Add(miClean);
            _list.ContextMenuStrip = menu;

            layout.Controls.Add(lblTitle,   0, 0);
            layout.Controls.Add(_lblStatus, 0, 1);
            layout.Controls.Add(_list,      0, 2);

            Controls.Add(layout);
        }

        // ── 워킹셋 정리 액션 (PID 재검증 + 백그라운드) ──────────────
        // EmptyWorkingSet 요구 권한: PROCESS_QUERY_(LIMITED_)INFORMATION + PROCESS_SET_QUOTA
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_SET_QUOTA                 = 0x0100;

        // kernel32.dll에는 "EmptyWorkingSet" export가 없다(K32EmptyWorkingSet만 존재).
        // psapi.dll의 EmptyWorkingSet은 K32EmptyWorkingSet으로 포워딩된다.
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private void CleanSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var sel = _list.SelectedItems[0];
            if (!(sel.Tag is int pid)) return;
            string expectedName = sel.Text;

            Task.Run(() =>
            {
                try
                {
                    // PID 재사용 방어: 표시명(ProcessName)과 불일치면 다른 프로세스이므로 스킵
                    using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                    {
                        if (!string.Equals(proc.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                            return;
                    }

                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"[LeakDetector] OpenProcess({pid}) failed: {Marshal.GetLastWin32Error()}");
                    }
                    else
                    {
                        try
                        {
                            if (!EmptyWorkingSet(h))
                                System.Diagnostics.Trace.WriteLine(
                                    $"[LeakDetector] EmptyWorkingSet({pid}) failed: {Marshal.GetLastWin32Error()}");
                        }
                        finally
                        {
                            CloseHandle(h);
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // GetProcessById: 이미 종료됨 → 스킵
                }
                catch (InvalidOperationException)
                {
                    // ProcessName 조회 시점에 종료됨 → 스킵
                }
                finally
                {
                    SafeInvoke((Action)RefreshList);
                }
            });
        }

        private void StartRefresh()
        {
            _refreshTimer = new System.Timers.Timer(5000) { AutoReset = true };
            _refreshTimer.Elapsed += (s, e) => SafeInvoke((Action)RefreshList);
            _refreshTimer.Start();
            RefreshList();
        }

        // Dispose 이후 Invoke 예외 방지
        private void SafeInvoke(Delegate method)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(method); }
            // 확인 직후 핸들 파괴/Dispose(경합) — ObjectDisposedException 포함
            catch (InvalidOperationException) { }
        }

        private void RefreshList()
        {
            List<ProcessLeakInfo> infos = ProcessLeakTracker.DetectLeaks();

            // 선택/스크롤 상태 보존
            var selectedPids = new HashSet<int>();
            foreach (ListViewItem s in _list.SelectedItems)
                if (s.Tag is int sp) selectedPids.Add(sp);
            int topPid = _list.TopItem?.Tag is int tp ? tp : -1;

            _list.BeginUpdate();

            // 기존 항목을 PID로 매핑 (증분 갱신용)
            var existing = new Dictionary<int, ListViewItem>();
            foreach (ListViewItem it in _list.Items)
                if (it.Tag is int ipid) existing[ipid] = it;

            int suspectCount = 0;
            int max = Math.Min(30, infos.Count);
            // DetectLeaks 순서(누적 증가량 내림차순)대로 쌓은 항목 목록
            var ordered = new List<ListViewItem>(max);
            for (int i = 0; i < max; i++)
            {
                var info = infos[i];

                if (!existing.TryGetValue(info.PID, out var item))
                {
                    // 새 항목 생성 (리스트 반영은 아래에서 순서대로)
                    item = new ListViewItem(info.Name);
                    item.SubItems.Add(info.PID.ToString());          // 1 PID
                    item.SubItems.Add("");                            // 2 현재
                    item.SubItems.Add("");                            // 3 증가량
                    item.SubItems.Add("");                            // 4 증가율
                    item.SubItems.Add("");                            // 5 관측시간
                    item.SubItems.Add("");                            // 6 판정
                    item.Tag = info.PID;
                }
                else
                {
                    item.SubItems[0].Text = info.Name;
                }
                ordered.Add(item);

                // 텍스트 갱신
                item.SubItems[2].Text = MemoryHelper.FormatBytes(info.CurrentWorkingSet);
                item.SubItems[3].Text = FormatDelta(info.DeltaBytes);
                item.SubItems[4].Text = FormatRate(info.BytesPerMinute);
                item.SubItems[5].Text = $"{info.MinutesObserved:F1}분";

                if (info.IsSuspect)
                {
                    item.SubItems[6].Text = "⚠ 누수 의심";
                    item.ForeColor = DarkTheme.Danger;
                    suspectCount++;
                }
                else if (info.DeltaBytes > 50L * 1024 * 1024)
                {
                    item.SubItems[6].Text = "증가 중";
                    item.ForeColor = DarkTheme.WarningText;
                }
                else
                {
                    item.SubItems[6].Text = "정상";
                    item.ForeColor = DarkTheme.TextSecondary;
                }
            }

            // 순서가 DetectLeaks 순서와 다르면(새 항목 추가·순위 변동·종료 항목 제거) 재배치한다.
            ListViewOrder.Apply(_list, ordered);

            // 선택 복원
            foreach (ListViewItem it in _list.Items)
                it.Selected = it.Tag is int ipid2 && selectedPids.Contains(ipid2);

            // 스크롤(TopItem) 복원
            if (topPid != -1)
            {
                foreach (ListViewItem it in _list.Items)
                    if (it.Tag is int ipid3 && ipid3 == topPid) { _list.TopItem = it; break; }
            }

            _list.EndUpdate();

            if (suspectCount > 0)
            {
                _lblStatus.Text = $"⚠ 누수 의심 프로세스 {suspectCount}개 감지됨 (15분 이상 관측, 누적 300MB 이상 증가)";
                _lblStatus.ForeColor = DarkTheme.Danger;
            }
            else if (infos.Count == 0)
            {
                _lblStatus.Text = "데이터 수집 중… 첫 샘플링까지 최대 30초 소요";
                _lblStatus.ForeColor = DarkTheme.TextSecondary;
            }
            else
            {
                _lblStatus.Text = $"{infos.Count}개 프로세스 관측 중 — 누수 의심 없음";
                _lblStatus.ForeColor = DarkTheme.SuccessText;
            }
        }

        private static string FormatDelta(long bytes)
        {
            string sign = bytes >= 0 ? "+" : "-";
            return sign + MemoryHelper.FormatBytes(Math.Abs(bytes));
        }

        private static string FormatRate(double bytesPerMinute)
        {
            double mbPerMin = bytesPerMinute / (1024.0 * 1024.0);
            if (mbPerMin >= 0)
                return $"+{mbPerMin:F1} MB/분";
            return $"{mbPerMin:F1} MB/분";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_refreshTimer != null)
                {
                    _refreshTimer.Stop();
                    _refreshTimer.Dispose();
                    _refreshTimer = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
