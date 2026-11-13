using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using MemoryCleaner.Core;
using MemoryCleaner.UI;

namespace MemoryCleaner.Controls
{
    public class ProcessControl : UserControl
    {
        private ListView _list;
        private System.Timers.Timer _refreshTimer;
        private Label _lblUpdated;

        public ProcessControl()
        {
            BackColor = DarkTheme.Background;
            Dock = DockStyle.Fill;
            Padding = new Padding(20);
            BuildUI();
            StartRefresh();
        }

        private void BuildUI()
        {
            var lblTitle = new Label
            {
                Text = "프로세스별 메모리 현황",
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = DarkTheme.TextPrimary,
                Dock = DockStyle.Top,
                Height = 40
            };

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = DarkTheme.Background };

            _lblUpdated = new Label
            {
                Text = "", Left = 8, Top = 12,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = DarkTheme.TextSecondary,
                AutoSize = true
            };

            var btnRefresh = new RoundButton("새로고침")
            {
                Left = toolbar.Width - 98,
                Top = 7, Width = 90, Height = 30,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = DarkTheme.TextOnAccent,   // 인디고 배경 위 흰 텍스트
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnRefresh.Click += (s, e) => RefreshList();

            toolbar.Controls.Add(_lblUpdated);
            toolbar.Controls.Add(btnRefresh);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BackColor = DarkTheme.Card,
                ForeColor = DarkTheme.TextPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9f),
                OwnerDraw = false
            };

            _list.Columns.Add("프로세스명", 200);
            _list.Columns.Add("PID", 65);
            _list.Columns.Add("워킹셋", 110);
            _list.Columns.Add("개인 메모리", 110);
            _list.Columns.Add("", 60); // 종료 버튼용

            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.MouseClick += OnListMouseClick;

            // 키보드 경로: 메뉴 키/Shift+F10으로 컨텍스트 메뉴, 또는 Delete 키
            var menu = new ContextMenuStrip();
            var miKill = new ToolStripMenuItem("프로세스 종료") { ShortcutKeyDisplayString = "Del" };
            miKill.Click += (s, e) => KillSelected();
            menu.Items.Add(miKill);
            menu.Opening += (s, e) => e.Cancel = _list.SelectedItems.Count == 0;
            _list.ContextMenuStrip = menu;
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete)
                {
                    e.Handled = true;
                    KillSelected();
                }
            };

            // 도킹은 컬렉션 뒤쪽부터 처리된다: 제목(Top) → 툴바(Top) → 리스트(Fill, 남은 영역).
            Controls.Add(_list);
            Controls.Add(toolbar);
            Controls.Add(lblTitle);

            RefreshList();
        }

        private void RefreshList()
        {
            var procs = MemoryHelper.GetTopProcesses(30);

            // 선택/스크롤 상태 보존
            var selectedPids = new HashSet<int>();
            foreach (ListViewItem sel in _list.SelectedItems)
                if (sel.Tag is int sp) selectedPids.Add(sp);
            int topPid = _list.TopItem?.Tag is int tp ? tp : -1;

            _list.BeginUpdate();

            // 기존 항목을 PID로 매핑 (증분 갱신용)
            var existing = new Dictionary<int, ListViewItem>();
            foreach (ListViewItem it in _list.Items)
                if (it.Tag is int ipid) existing[ipid] = it;

            // GetTopProcesses 순서(워킹셋 내림차순)대로 쌓은 항목 목록
            var ordered = new List<ListViewItem>(procs.Count);
            foreach (var p in procs)
            {
                if (!existing.TryGetValue(p.PID, out var item))
                {
                    // 새 항목 생성 (리스트 반영은 아래에서 순서대로)
                    item = new ListViewItem(p.Name);
                    item.SubItems.Add(p.PID.ToString());
                    item.SubItems.Add(MemoryHelper.FormatBytes(p.WorkingSet));
                    item.SubItems.Add(MemoryHelper.FormatBytes(p.PrivateBytes));
                    item.SubItems.Add("종료");
                    item.Tag = p.PID;
                }
                else
                {
                    // 기존 항목은 객체 재사용 + 텍스트 갱신
                    item.SubItems[0].Text = p.Name;
                    item.SubItems[2].Text = MemoryHelper.FormatBytes(p.WorkingSet);
                    item.SubItems[3].Text = MemoryHelper.FormatBytes(p.PrivateBytes);
                }

                // 색상: 워킹셋 크기에 따라
                if (p.WorkingSet > 500L * 1024 * 1024)
                    item.ForeColor = DarkTheme.Danger;
                else if (p.WorkingSet > 200L * 1024 * 1024)
                    item.ForeColor = DarkTheme.WarningText;
                else
                    item.ForeColor = DarkTheme.TextPrimary;

                ordered.Add(item);
            }

            // 순위 변동·새 항목·종료된 항목을 반영해 워킹셋 순서로 재배치
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
            _lblUpdated.Text = $"마지막 업데이트: {DateTime.Now:HH:mm:ss}";
        }

        private void OnListMouseClick(object sender, MouseEventArgs e)
        {
            var hit = _list.HitTest(e.X, e.Y);
            if (hit.Item == null || hit.SubItem == null) return;

            int subItemIndex = hit.Item.SubItems.IndexOf(hit.SubItem);
            if (subItemIndex != 4) return; // "종료" 컬럼만

            ConfirmAndKill(hit.Item);
        }

        private void KillSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            ConfirmAndKill(_list.SelectedItems[0]);
        }

        private void ConfirmAndKill(ListViewItem item)
        {
            if (!(item.Tag is int pid)) return;
            string name = item.Text;

            var confirm = new TaskDialogButton("프로세스 종료");
            var page = new TaskDialogPage
            {
                Caption = "프로세스 종료",
                Heading = $"{name} (PID {pid})을(를) 종료할까요?",
                Text = "저장하지 않은 작업은 사라집니다.",
                Icon = TaskDialogIcon.Warning,
                Buttons = { confirm, TaskDialogButton.Cancel },
                DefaultButton = TaskDialogButton.Cancel
            };

            if (TaskDialog.ShowDialog(this, page) != confirm) return;

            KillProcessAsync(pid, name);
        }

        // 백그라운드에서 Kill (UI 블로킹 방지) + PID 재사용 방어
        private void KillProcessAsync(int pid, string expectedName)
        {
            Task.Run(() =>
            {
                string failure = null;
                try
                {
                    using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                    {
                        // 리스트 표시명(ProcessName)과 불일치하면 PID 재사용 → 다른 프로세스이므로 건드리지 않음
                        if (!string.Equals(proc.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                            return;
                        proc.Kill();
                    }
                }
                catch (ArgumentException)
                {
                    // 이미 종료됨 — 목적 달성, 새로고침만
                }
                catch (InvalidOperationException)
                {
                    // 조회~Kill 사이에 종료됨 — 목적 달성
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[Process] Kill({pid}) failed: {ex.NativeErrorCode} {ex.Message}");
                    failure = "권한이 없거나 보호된 시스템 프로세스입니다.";
                }
                catch (NotSupportedException)
                {
                    failure = "원격 컴퓨터의 프로세스는 종료할 수 없습니다.";
                }
                finally
                {
                    SafeInvoke((Action)RefreshList);
                }

                if (failure != null)
                    SafeInvoke((Action)(() => ShowKillFailure(expectedName, failure)));
            });
        }

        private void ShowKillFailure(string name, string reason)
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = "프로세스 종료",
                Heading = $"{name}을(를) 종료하지 못했습니다",
                Text = reason + " 작업 관리자를 관리자 권한으로 열어 다시 시도하세요.",
                Icon = TaskDialogIcon.Error,
                Buttons = { TaskDialogButton.OK }
            });
        }

        // Dispose 이후 Invoke 예외 방지
        private void SafeInvoke(Delegate method)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(method); }
            // 확인 직후 핸들 파괴/Dispose(경합) — ObjectDisposedException 포함
            catch (InvalidOperationException) { }
        }

        private void StartRefresh()
        {
            _refreshTimer = new System.Timers.Timer(5000) { AutoReset = true };
            _refreshTimer.Elapsed += (s, e) => SafeInvoke((Action)RefreshList);
            _refreshTimer.Start();
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
