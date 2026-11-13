using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MemoryCleaner.UI
{
    // Button을 상속해 키보드 활성화(Enter/Space), 탭 이동, PushButton 접근성 역할을
    // 플랫폼에서 받는다. 그리기만 OnPaint로 대체한다.
    public class RoundButton : Button
    {
        private bool _isHover;
        private bool _isPressed;
        private int _cornerRadius = 10;
        private Color _baseColor;
        private Color _hoverColor;
        private Color _pressColor;
        private Color _textColor = DarkTheme.TextPrimary;

        public int CornerRadius { get => _cornerRadius; set { _cornerRadius = value; Invalidate(); } }
        public Color BaseColor  { get => _baseColor;  set { _baseColor = value; Invalidate(); } }
        public Color HoverColor { get => _hoverColor; set { _hoverColor = value; Invalidate(); } }
        public Color PressColor { get => _pressColor; set { _pressColor = value; Invalidate(); } }
        // 주의: 기반 Control.ForeColor를 의도적으로 섀도잉한다(다형 접근 시 base가 아닌 이 값이 페인트에 쓰임).
        // 일부 호출부가 객체 초기화로 'ForeColor = ...'를 직접 사용하므로 이름 변경/제거 시 호출부가 깨진다.
        // 안전을 위해 시그니처를 유지한다. 다형 접근이 필요하면 이 프로퍼티(_textColor)를 참조할 것.
        public new Color ForeColor { get => _textColor; set { _textColor = value; Invalidate(); } }

        public RoundButton(string text = "", Color? baseColor = null)
        {
            Text = text;
            _baseColor  = baseColor ?? DarkTheme.Accent;
            _hoverColor = DarkTheme.AccentHover;
            _pressColor = DarkTheme.AccentPressed;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _isHover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _isHover = false; _isPressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _isPressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)   { _isPressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e)  { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 둥근 모서리 바깥은 부모 배경으로 채운다(Button 기본 배경색이 비치지 않게)
            Color parentBg = Parent?.BackColor ?? DarkTheme.Background;
            g.Clear(parentBg);

            // 비활성: 기본색을 부모 배경 쪽으로 70% 섞은 불투명색(흐리게 보이되 알파 합성 없음).
            Color bg;
            if (!Enabled)
                bg = DarkTheme.Blend(_baseColor, parentBg, 0.7f);
            else if (_isPressed)
                bg = _pressColor;
            else if (_isHover)
                bg = _hoverColor;
            else
                bg = _baseColor;

            var rc = new RectangleF(0, 0, Width - 1, Height - 1);

            // 버튼 본체
            using (var path = Shapes.RoundedRect(rc, _cornerRadius))
            {
                // 그라데이션 배경 (비활성은 평면)
                Color bottom = Enabled ? DarkTheme.Darken(bg, 25) : bg;
                using (var brush = new LinearGradientBrush(
                    new Point(0, 0), new Point(0, Height), bg, bottom))
                    g.FillPath(brush, path);

                // 테두리 — 흰 배경 위에서도 버튼 형태가 또렷하게 보이도록
                // 버튼 배경색을 살짝 어둡게 해 파생(흰 버튼=옅은 그레이 테두리, 강조 버튼=진한 인디고 테두리)
                Color borderCol = Enabled
                    ? DarkTheme.Darken(bg, 28)
                    : DarkTheme.WithAlpha(DarkTheme.Border, 120);
                using (var pen = new Pen(borderCol, 1))
                    g.DrawPath(pen, path);
            }

            // 텍스트
            Color txtColor = Enabled ? _textColor : Color.FromArgb(100, _textColor.R, _textColor.G, _textColor.B);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var brush = new SolidBrush(txtColor))
            using (var font = new Font(Font.FontFamily, Font.Size, Font.Style))
                g.DrawString(Text, font, brush, new Rectangle(0, _isPressed ? 1 : 0, Width, Height), sf);

            // 포커스 링: 밝은 바탕 버튼이면 강조색, 강조색 바탕이면 흰색(각각 3:1 이상)
            if (Focused && ShowFocusCues)
            {
                bool lightFill = _baseColor.GetBrightness() > 0.6f;
                FocusRing.Draw(g, ClientRectangle, lightFill ? DarkTheme.Accent : Color.White,
                    Math.Max(0, _cornerRadius - 2));
            }
        }

    }
}
