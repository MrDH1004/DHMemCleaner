using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MemoryCleaner.UI
{
    public class GaugeBar : Control
    {
        private double _value = 0;
        private double _maximum = 100;
        private int _cornerRadius = 5;

        public double Value
        {
            get => _value;
            set
            {
                double v = Math.Max(0, Math.Min(_maximum, value));
                // 값이 실질적으로 바뀐 경우에만 재페인트(1초 타이머 반복 시 동일 값 재페인트 방지)
                if (Math.Abs(v - _value) < 0.05) { _value = v; return; }
                _value = v;
                Invalidate();
            }
        }

        public double Maximum
        {
            get => _maximum;
            set { _maximum = Math.Max(1, value); Invalidate(); }
        }

        public GaugeBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Height = 8;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int r = _cornerRadius;
            var bgRect = new RectangleF(0, 0, Width, Height);

            // 트랙 배경 (라이트 슬레이트, 불투명)
            using (var path = Shapes.RoundedRect(bgRect, r))
            using (var brush = new SolidBrush(DarkTheme.Track))
                g.FillPath(brush, path);

            // 채워진 부분
            if (_value > 0 && _maximum > 0)
            {
                float fillW = (float)(_value / _maximum * Width);
                fillW = Math.Max(r * 2, fillW);

                var fillColor = DarkTheme.GetUsageColor(_value / _maximum * 100);
                var fillRect  = new RectangleF(0, 0, fillW, Height);

                using (var path = Shapes.RoundedRect(fillRect, r))
                {
                    // 그라데이션 채우기
                    var c1 = Color.FromArgb(180, fillColor.R, fillColor.G, fillColor.B);
                    var c2 = fillColor;
                    using (var brush = new LinearGradientBrush(
                        new PointF(0, 0), new PointF(fillW, 0), c1, c2))
                        g.FillPath(brush, path);

                    // 상단 하이라이트
                    var hiRect = new RectangleF(2, 0, fillW - 4, Height / 2f);
                    if (hiRect.Width > 0)
                    {
                        using (var brush = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
                            g.FillRectangle(brush, hiRect);
                    }
                }
            }
        }

    }
}
