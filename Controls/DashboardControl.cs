using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Timers;
using System.Windows.Forms;
using MemoryCleaner.Core;
using MemoryCleaner.UI;

namespace MemoryCleaner.Controls
{
    public class DashboardControl : UserControl
    {
        // 메모리 표시 레이블
        private Label _lblRamPct, _lblVirtPct, _lblWSPct;
        private Label _lblRamDetail, _lblVirtDetail, _lblWSDetail;
        private GaugeBar _gaugeRam, _gaugeVirt, _gaugeWS;

        // 정리 버튼
        private CleanButton _btnClean;
        private Label _lblStatus;
        private bool _isCleaning = false;

        private System.Timers.Timer _refreshTimer;
        private AppSettings _settings;

        // 공용 폰트: 폼 수명 내내 재사용(인스턴스/탭 재생성과 무관하게 1회만 생성)되어
        // Label에 할당해도 GDI 핸들이 누적되지 않는다. 명시적 Dispose 불필요.
        private static readonly Font FontTitle     = new Font("Segoe UI", 13f, FontStyle.Bold);
        private static readonly Font FontStatus    = new Font("Segoe UI", 8.5f);
        private static readonly Font FontRowName   = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font FontRowPct    = new Font("Segoe UI", 17f, FontStyle.Bold);
        private static readonly Font FontRowDetail = new Font("Segoe UI", 8f);

        public DashboardControl(AppSettings settings)
        {
            _settings = settings;
            BackColor = DarkTheme.Background;
            Dock = DockStyle.Fill;

            BuildUI();
            StartTimer();
            AutoCleanManager.CleanPerformed += OnCleanPerformed;
        }

        private void BuildUI()
        {
            // ── 상단 제목
            var lblTitle = new Label
            {
                Text = "메모리 현황",
                Font = FontTitle,
                ForeColor = DarkTheme.TextPrimary,
                Dock = DockStyle.Top,
                Height = 52,
                Padding = new Padding(24, 16, 0, 0)
            };
            Controls.Add(lblTitle);

            // ── 중앙 콘텐츠 패널
            var center = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = DarkTheme.Background,
                Padding = new Padding(24, 0, 24, 24)
            };
            Controls.Add(center);

            // ── RAM 행
            int y = 0;
            _gaugeRam    = AddMemRow(center, ref y, "RAM", out _lblRamPct,  out _lblRamDetail);
            _gaugeVirt   = AddMemRow(center, ref y, "가상", out _lblVirtPct, out _lblVirtDetail);
            _gaugeWS     = AddMemRow(center, ref y, "워킹셋", out _lblWSPct,  out _lblWSDetail);

            y += 16;

            // ── 정리 버튼
            _btnClean = new CleanButton
            {
                Left = 0, Top = y, Height = 56
            };
            _btnClean.Click += (s, e) => DoManualClean();
            center.Controls.Add(_btnClean);
            y += 72;

            // ── 상태 레이블
            _lblStatus = new Label
            {
                Left = 0, Top = y, Height = 20,
                Font = FontStatus,
                ForeColor = DarkTheme.TextSecondary,
                Text = ""
            };
            center.Controls.Add(_lblStatus);

            // 절대 배치 자식에는 Padding이 적용되지 않으므로 좌측 여백을 직접 맞춘다
            // (제목의 24px 들여쓰기와 같은 정렬선).
            center.Resize += (s, e) =>
            {
                int w = center.ClientSize.Width - center.Padding.Horizontal;
                if (w <= 0) return;
                foreach (Control c in center.Controls)
                {
                    c.Left = center.Padding.Left;
                    c.Width = w;
                }
            };

            lblTitle.BringToFront();
            center.BringToFront();
        }

        // 퍼센트 라벨 폭: 가장 긴 값("100.0%")이 잘리지 않도록 실제 폰트로 측정
        private static readonly int PctLabelWidth =
            TextRenderer.MeasureText("100.0%", FontRowPct).Width + 4;

        private GaugeBar AddMemRow(Panel parent, ref int y, string label,
            out Label lblPct, out Label lblDetail)
        {
            // 행 컨테이너
            var row = new Panel
            {
                Left = 0, Top = y, Height = 72,
                BackColor = DarkTheme.Card,
                Padding = new Padding(0)
            };

            // 왼쪽 레이블
            var lblName = new Label
            {
                Text = label,
                Left = 16, Top = 16, Width = 52, Height = 18,
                Font = FontRowName,
                ForeColor = DarkTheme.TextSecondary,
                BackColor = DarkTheme.Card
            };

            // 퍼센트
            var lblP = new Label
            {
                Text = "--%", Left = 16, Top = 34, Width = PctLabelWidth, Height = 24,
                Font = FontRowPct,
                ForeColor = DarkTheme.TextPrimary,
                BackColor = DarkTheme.Card
            };

            // 상세
            var lblD = new Label
            {
                Text = "--", Left = 16 + PctLabelWidth + 6, Top = 42, Height = 18,
                Font = FontRowDetail,
                ForeColor = DarkTheme.TextSecondary,
                AutoSize = false,
                BackColor = DarkTheme.Card
            };

            // 게이지
            var gauge = new GaugeBar
            {
                Left = 16, Top = 61, Height = 5,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            row.Controls.Add(lblName);
            row.Controls.Add(lblP);
            row.Controls.Add(lblD);
            row.Controls.Add(gauge);
            parent.Controls.Add(row);

            // 행 하단 구분선
            y += 76;

            row.Resize += (s, e) =>
            {
                int w = row.ClientSize.Width;
                gauge.Width = w - 32;
                lblD.Width  = Math.Max(0, w - lblD.Left - 16);
            };

            lblPct    = lblP;
            lblDetail = lblD;
            return gauge;
        }

        private void StartTimer()
        {
            _refreshTimer = new System.Timers.Timer(1000) { AutoReset = true };
            _refreshTimer.Elapsed += (s, e) =>
            {
                // 논블로킹 BeginInvoke로 UI 스레드에 위임한다.
                PostToUi(RefreshData);
            };
            _refreshTimer.Start();
        }

        // 다른 스레드에서 UI 작업을 안전하게 예약한다. 핸들이 없거나 파괴 중이면 건너뛴다
        // (/minimized 시작 시 창을 열기 전까지 이 컨트롤의 핸들은 없다 — 화면이 없으니 갱신할 것도 없음).
        private void PostToUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { /* 확인 직후 핸들 파괴(경합) — ObjectDisposedException 포함 */ }
        }

        // 정리 결과 문구를 1초 갱신이 바로 덮어쓰지 않도록 잠시 유지한다.
        private DateTime _statusHoldUntil = DateTime.MinValue;
        private const int StatusHoldSeconds = 6;

        private void RefreshData()
        {
            var info = MemoryHelper.GetMemoryInfo();

            SetRow(_lblRamPct,  _gaugeRam,  _lblRamDetail,
                info.PhysicalPercent,
                $"{MemoryHelper.FormatBytes(info.PhysicalUsed)} / {MemoryHelper.FormatBytes(info.PhysicalTotal)}");

            SetRow(_lblVirtPct, _gaugeVirt, _lblVirtDetail,
                info.VirtualPercent,
                $"{MemoryHelper.FormatBytes(info.VirtualUsed)} / {MemoryHelper.FormatBytes(info.VirtualTotal)}");

            SetRow(_lblWSPct,   _gaugeWS,   _lblWSDetail,
                info.WorkingSetPercent,
                $"{MemoryHelper.FormatBytes(info.WorkingSetUsed)}");

            // 게임 모드 활성 시 우선 표시
            if (AutoCleanManager.LastTickSkippedForFullscreen)
            {
                var name = AutoCleanManager.LastFullscreenProcessName;
                _lblStatus.Text = string.IsNullOrEmpty(name)
                    ? "게임 모드 — 자동정리 일시중지"
                    : $"게임 모드 — {name} 풀스크린 감지, 자동정리 일시중지";
                _lblStatus.ForeColor = DarkTheme.Accent;
                return;
            }

            if (MonotonicClock.Now < _statusHoldUntil) return;

            var rem = MemoryHelper.GetCooldownRemaining();
            _lblStatus.Text = rem.TotalSeconds > 0
                ? $"쿨다운: {(int)rem.TotalSeconds}초 후 정리 가능"
                : "";
            _lblStatus.ForeColor = DarkTheme.TextSecondary;
        }

        private void SetRow(Label lblPct, GaugeBar gauge, Label lblDetail, double pct, string detail)
        {
            // 값이 실제로 바뀐 경우에만 텍스트/색상을 갱신해 불필요한 재페인트를 줄인다.
            string pctText = $"{pct:F1}%";
            if (lblPct.Text != pctText) lblPct.Text = pctText;

            Color pctColor = DarkTheme.GetUsageColor(pct);
            if (lblPct.ForeColor != pctColor) lblPct.ForeColor = pctColor;

            gauge.Value = pct; // GaugeBar가 자체 epsilon으로 재페인트 여부를 판단

            if (lblDetail.Text != detail) lblDetail.Text = detail;
        }

        private void DoManualClean()
        {
            if (_isCleaning) return;
            _isCleaning = true;
            _btnClean.Enabled = false;
            _btnClean.SetCleaning(true);

            System.Threading.Tasks.Task.Run(() =>
                // 사용자가 직접 누른 정리 → 쿨다운 우회 요청(강력 정리 모드에서는 MemoryHelper가 무시)
                AutoCleanManager.PerformClean("수동", bypassCooldown: true)
            ).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Trace.WriteLine($"[Dashboard] PerformClean 예외: {t.Exception?.GetBaseException()}");

                PostToUi(() =>
                {
                    _btnClean.SetCleaning(false);
                    _btnClean.Enabled = true;
                    _isCleaning = false;
                    // 건너뛴 경우(강력 정리 쿨다운 등)는 RefreshData가 남은 쿨다운을 표시한다
                });
            });
        }

        private void OnCleanPerformed(object sender, CleanEventArgs e)
        {
            if (!e.ShowNotification) return;
            var entry = e.Entry;
            PostToUi(() =>
            {
                var saved = entry.BeforePhysical - entry.AfterPhysical;
                if (entry.PartiallyFailed)
                {
                    _lblStatus.Text = $"일부 단계 실패 — RAM {entry.BeforePhysical:F1}% → {entry.AfterPhysical:F1}%. 관리자 권한 실행 여부를 확인하세요.";
                    _lblStatus.ForeColor = DarkTheme.WarningText;
                }
                else
                {
                    _lblStatus.Text = $"정리 완료  —  RAM {entry.BeforePhysical:F1}% → {entry.AfterPhysical:F1}%  ({saved:+0.0;-0.0;0}% 절약)";
                    _lblStatus.ForeColor = saved >= 0 ? DarkTheme.SuccessText : DarkTheme.TextSecondary;
                }
                _statusHoldUntil = MonotonicClock.Now.AddSeconds(StatusHoldSeconds);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer?.Stop();
                _refreshTimer?.Dispose();
                AutoCleanManager.CleanPerformed -= OnCleanPerformed;
            }
            base.Dispose(disposing);
        }
    }

    // ────────────────────── 정리 버튼 ──────────────────────
    // Button 상속: Enter/Space 활성화, 탭 이동, PushButton 접근성 역할을 플랫폼에서 받는다.
    internal class CleanButton : Button
    {
        private bool _cleaning = false;
        private bool _hover = false;
        private bool _pressed = false;

        public CleanButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = 56;
            Text = "메모리 정리";
            AccessibleName = "메모리 정리";
        }

        public void SetCleaning(bool cleaning)
        {
            _cleaning = cleaning;
            Text = cleaning ? "정리 중…" : "메모리 정리";
            AccessibleName = Text;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e)  { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? DarkTheme.Background);

            var rc = new RectangleF(0, 0, Width - 1, Height - 1);
            int r = 10;

            // 정리 중: TextSecondary 바탕 + 흰 글자(7.58:1)
            Color c1 = _cleaning ? DarkTheme.TextSecondary :
                       _pressed  ? DarkTheme.AccentPressed :
                       _hover    ? DarkTheme.AccentHover : DarkTheme.Accent;
            // 하단 그라데이션은 상단색에서 파생(새 강조색을 항상 따라간다)
            Color c2 = DarkTheme.Darken(c1, 26);

            using (var path = Shapes.RoundedRect(rc, r))
            using (var brush = new LinearGradientBrush(
                new PointF(0, 0), new PointF(0, Height), c1, c2))
                g.FillPath(brush, path);

            string text = Text;
            using (var font = new Font("Segoe UI", 11f, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.White))
            {
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(text, font, brush, new RectangleF(0, 0, Width, Height), sf);
            }

            // 강조색 바탕 위 흰 포커스 링(6.29:1)
            if (Focused && ShowFocusCues)
                FocusRing.Draw(g, ClientRectangle, Color.White, 8);
        }

    }

    // ────────────────────── 둥근 카드 패널 (Settings/History 공용) ──────────────────────
    internal class CardPanel : Panel
    {
        public Color AccentColor { get; set; } = Color.Transparent;

        public CardPanel()
        {
            BackColor = DarkTheme.Card;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color parentBg = Parent?.BackColor ?? DarkTheme.Background;
            using (var brush = new SolidBrush(parentBg))
                g.FillRectangle(brush, ClientRectangle);

            var rc = new Rectangle(0, 0, Width - 1, Height - 1);

            using (var path = Shapes.RoundedRect(rc, 10))
            {
                using (var brush = new SolidBrush(BackColor))
                    g.FillPath(brush, path);
                using (var pen = new Pen(DarkTheme.Border, 1))
                    g.DrawPath(pen, path);
            }

            if (AccentColor != Color.Transparent)
            {
                var barRect = new Rectangle(0, 12, 3, Height - 24);
                using (var brush = new SolidBrush(AccentColor))
                    g.FillRectangle(brush, barRect);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control.BackColor = BackColor;
        }

    }
}
