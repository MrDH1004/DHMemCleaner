using System.Drawing;
using System.Drawing.Drawing2D;

namespace MemoryCleaner.UI
{
    // 커스텀 그리기 컨트롤 공용 도형 (RoundButton·CleanButton·CardPanel·GaugeBar·FocusRing).
    internal static class Shapes
    {
        // 둥근 사각형 경로. 반지름이 폭/높이의 절반을 넘으면 절반으로 줄인다(작은 게이지 채움 등).
        // 반지름 0 이하는 일반 사각형.
        public static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float r = radius;
            if (rect.Width < r * 2)  r = rect.Width / 2f;
            if (rect.Height < r * 2) r = rect.Height / 2f;

            if (r <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }

            float d = r * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
