using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MemoryCleaner.Core;
using MemoryCleaner.UI;

namespace MemoryCleaner.Controls
{
    public class HistoryControl : UserControl
    {
        private ListView _list;

        public HistoryControl()
        {
            BackColor = DarkTheme.Background;
            Dock = DockStyle.Fill;
            Padding = new Padding(20);
            BuildUI();
            RefreshList(); // 탭 진입 시 기존 이력 즉시 표시
            AutoCleanManager.CleanPerformed += OnCleanPerformed;
        }

        private void BuildUI()
        {
            var lblTitle = new Label
            {
                Text = "정리 이력",
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = DarkTheme.TextPrimary,
                Dock = DockStyle.Top,
                Height = 40
            };

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = DarkTheme.Background };
            var btnClear = new RoundButton("기록 지우기")
            {
                Left = toolbar.Width - 118, Top = 7, Width = 110, Height = 30,  // 우측 8px (Process 새로고침과 같은 정렬)
                Font = new Font("Segoe UI", 8.5f),
                BaseColor = DarkTheme.Card,
                HoverColor = DarkTheme.CardHover,
                PressColor = DarkTheme.Border,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnClear.Click += (s, e) => ConfirmAndClear();
            toolbar.Controls.Add(btnClear);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = DarkTheme.Card,
                ForeColor = DarkTheme.TextPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9f)
            };

            _list.Columns.Add("시각", 130);
            _list.Columns.Add("트리거", 120);
            _list.Columns.Add("실제메모리 전→후", 145);
            _list.Columns.Add("가상메모리 전→후", 145);
            _list.Columns.Add("워킹셋 전→후", 130);
            _list.Columns.Add("절약", 75);

            // 도킹은 컬렉션 뒤쪽부터 처리된다: 제목(Top) → 툴바(Top) → 리스트(Fill, 남은 영역).
            // 순서를 BringToFront로 다시 바꾸면 Fill이 먼저 도킹돼 제목/툴바가 리스트 위에 겹친다.
            Controls.Add(_list);
            Controls.Add(toolbar);
            Controls.Add(lblTitle);
        }

        // 되돌릴 수 없는 삭제라 확인 후 실행한다.
        private void ConfirmAndClear()
        {
            int count = AutoCleanManager.GetHistorySnapshot().Count;
            if (count == 0) return;

            var confirm = new TaskDialogButton("기록 지우기");
            var page = new TaskDialogPage
            {
                Caption = "정리 이력",
                Heading = $"정리 이력 {count}건을 지울까요?",
                Text = "지운 기록은 되돌릴 수 없습니다.",
                Icon = TaskDialogIcon.Warning,
                Buttons = { confirm, TaskDialogButton.Cancel },
                DefaultButton = TaskDialogButton.Cancel
            };

            if (TaskDialog.ShowDialog(this, page) != confirm) return;

            AutoCleanManager.ClearHistory();
            RefreshList();
        }

        private void RefreshList()
        {
            List<CleanHistoryEntry> snapshot = AutoCleanManager.GetHistorySnapshot();

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var h in snapshot)
            {
                var item = new ListViewItem(h.Time.ToString("MM-dd HH:mm:ss"));
                item.SubItems.Add(h.Trigger);
                item.SubItems.Add($"{h.BeforePhysical:F1}% → {h.AfterPhysical:F1}%");
                item.SubItems.Add($"{h.BeforeVirtual:F1}% → {h.AfterVirtual:F1}%");
                item.SubItems.Add($"{h.BeforeWorkingSet:F1}% → {h.AfterWorkingSet:F1}%");
                double saved = h.BeforePhysical - h.AfterPhysical;
                item.SubItems.Add($"{saved:+0.0;-0.0;0.0}%");
                item.ForeColor = saved > 1 ? DarkTheme.SuccessText : DarkTheme.TextSecondary;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
        }

        private void OnCleanPerformed(object sender, CleanEventArgs e)
        {
            // 정리 스레드에서 호출된다. 핸들이 없으면(화면 미표시) 갱신할 것이 없다.
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)RefreshList); }
            catch (InvalidOperationException) { /* 확인 직후 핸들 파괴(경합) — ObjectDisposedException 포함 */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) AutoCleanManager.CleanPerformed -= OnCleanPerformed;
            base.Dispose(disposing);
        }
    }
}
