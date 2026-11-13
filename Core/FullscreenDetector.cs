using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MemoryCleaner.Core
{
    /// <summary>
    /// 포그라운드 윈도우가 풀스크린(게임/영상 등)인지 감지.
    /// 자동정리가 게임/영상 시청 중 끊김을 유발하지 않도록 사용.
    /// </summary>
    public static class FullscreenDetector
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        // 32-bit, 64-bit 모두 호환되는 GetWindowLongPtr 시그니처
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

        private static IntPtr GetWindowLongCompat(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : GetWindowLong32(hWnd, nIndex);
        }

        // Window Style 상수 (winuser.h 공식 헤더 기준)
        private const int  GWL_STYLE      = -16;
        private const long WS_CAPTION     = 0x00C00000L;  // 타이틀바
        private const long WS_THICKFRAME  = 0x00040000L;  // 리사이즈 가능 프레임

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        // 풀스크린 검사에서 제외할 시스템 프로세스명 (전부 소문자)
        private static readonly HashSet<string> ExcludedProcesses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "dwm", "searchhost", "shellexperiencehost",
            "startmenuexperiencehost", "lockapp", "logonui"
        };

        /// <summary>
        /// 현재 포그라운드 윈도우가 풀스크린인지 검사.
        /// 데스크톱/탐색기 등 시스템 윈도우는 false.
        /// </summary>
        public static bool IsForegroundFullscreen(out string foregroundProcessName)
        {
            foregroundProcessName = null;
            // Win32 조회는 실패 시 0/false를 반환하므로 예외 대신 반환값으로 판정한다.
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                if (hwnd == GetDesktopWindow()) return false;
                if (hwnd == GetShellWindow()) return false;

                if (!GetWindowRect(hwnd, out RECT rc)) return false;

                int width = rc.Right - rc.Left;
                int height = rc.Bottom - rc.Top;
                if (width <= 0 || height <= 0) return false;

                // 윈도우가 위치한 모니터 찾기
                var winRect = new System.Drawing.Rectangle(rc.Left, rc.Top, width, height);
                var screen = Screen.FromRectangle(winRect);
                var bounds = screen.Bounds;

                // 윈도우 사이즈가 모니터 전체 크기와 일치하는지 확인 (1픽셀 오차 허용)
                bool sizeMatch = Math.Abs(width - bounds.Width) <= 1
                              && Math.Abs(height - bounds.Height) <= 1;
                bool positionMatch = Math.Abs(rc.Left - bounds.Left) <= 1
                                  && Math.Abs(rc.Top - bounds.Top) <= 1;
                if (!sizeMatch || !positionMatch) return false;

                // 타이틀바/프레임이 있는 일반 창 최대화(Excel 등)는 풀스크린이 아님.
                // 진짜 풀스크린/borderless 게임은 WS_CAPTION이 없다.
                long style = GetWindowLongCompat(hwnd, GWL_STYLE).ToInt64();
                if ((style & WS_CAPTION) != 0) return false;
                if ((style & WS_THICKFRAME) != 0) return false;

                // 프로세스 이름 확인 (시스템 프로세스 제외)
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return false;

                try
                {
                    using (var proc = Process.GetProcessById((int)pid))
                    {
                        foregroundProcessName = proc.ProcessName;
                        if (ExcludedProcesses.Contains(proc.ProcessName))
                            return false;
                    }
                }
                catch (ArgumentException) { return false; }          // 조회 직전 종료된 프로세스
                catch (InvalidOperationException) { return false; }  // ProcessName 조회 시점에 종료됨

                return true;
            }
        }

        public static bool IsForegroundFullscreen() => IsForegroundFullscreen(out _);
    }
}
