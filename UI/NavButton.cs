using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MemoryCleaner.UI
{
    // Button을 상속해 키보드 활성화(Enter/Space), 탭 이동, PushButton 접근성 역할을
    // 플랫폼에서 그대로 받는다. 그리기만 OnPaint로 대체한다.
    public class NavButton : Button
    {
        private bool _isActive;
        private bool _isHover;

        public bool IsActive
        {
            get => _isActive;
            set
            {
                _isActive = value;
                // 스크린리더: 현재 선택된 화면임을 알린다
                AccessibleDescription = value ? "현재 화면" : null;
                Invalidate();
            }
        }

        // icon: 예약 파라미터 — 현재 렌더링에는 사용하지 않는다(MainForm은 "○"를 전달).
        // MainForm 호출 호환을 위해 생성자 시그니처(string, string)를 유지한다.
        public NavButton(string text, string icon = "")
        {
            _ = icon; // 의도적 미사용
            Text = text;
            AccessibleName = text;
            Height = 46;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _isHover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _isHover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e)  { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(DarkTheme.NavBackground);

            if (_isActive)
            {
                // 활성: 좌측 강조바 + 소프트 인디고 워시 (강조색에서 파생)
                using (var brush = new SolidBrush(DarkTheme.WithAlpha(DarkTheme.Accent, 20)))
                    g.FillRectangle(brush, 0, 0, Width, Height);

                using (var brush = new SolidBrush(DarkTheme.Accent))
                    g.FillRectangle(brush, 0, 8, 3, Height - 16);
            }
            else if (_isHover)
            {
                // 라이트 배경 위 호버는 어두운 슬레이트 틴트로
                using (var brush = new SolidBrush(Color.FromArgb(10, 15, 23, 42)))
                    g.FillRectangle(brush, 0, 0, Width, Height);
            }

            // 비활성 텍스트도 4.5:1 이상(TextSecondary 7.58:1). 활성은 굵게 + 강조바로 구분.
            Color textColor = _isActive ? DarkTheme.TextPrimary : DarkTheme.TextSecondary;

            using (var font = new Font("Segoe UI", 9f, _isActive ? FontStyle.Bold : FontStyle.Regular))
            using (var brush = new SolidBrush(textColor))
            using (var sf = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Alignment = StringAlignment.Near
            })
                g.DrawString(Text, font, brush, new Rectangle(18, 0, Width - 24, Height), sf);

            if (Focused && ShowFocusCues)
                FocusRing.Draw(g, ClientRectangle, DarkTheme.Accent, radius: 4);
        }
    }

    // 키보드 포커스 링(2px). 배경 대비 3:1 이상 색을 넘겨받는다.
    internal static class FocusRing
    {
        public static void Draw(Graphics g, Rectangle bounds, Color color, int radius)
        {
            var rc = Rectangle.Inflate(bounds, -2, -2);
            if (rc.Width <= 0 || rc.Height <= 0) return;

            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, 2f))
            using (var path = Shapes.RoundedRect(rc, radius))
                g.DrawPath(pen, path);
            g.SmoothingMode = old;
        }
    }
}
