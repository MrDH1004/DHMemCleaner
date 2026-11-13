using System;
using System.Drawing;
using System.Windows.Forms;

namespace MemoryCleaner.UI
{
    // ─────────────────────────────────────────────────────────────
    //  라이트 테마 "Clinical White" — 정밀 계기 컨셉
    //  · 화면은 거의 무채색(흰 카드 + 헤어라인).
    //  · 색은 "의미"가 있을 때만 등장한다:
    //      - 메모리 압력 상태  → Success / Warning / Danger
    //      - 유일한 주요 액션   → Accent (인디고)
    //  · 클래스명은 기존 소비부(약 40곳) 호환을 위해 DarkTheme 유지.
    //    시맨틱 토큰 이름(Background/Surface/Card…)은 그대로이고 값만 라이트로 재매핑됐다.
    // ─────────────────────────────────────────────────────────────
    public static class DarkTheme
    {
        // ── 배경 계층 ──────────────────────────────────────────
        public static Color Background    = Color.FromArgb(244, 246, 248);  // #F4F6F8 앱 베이스(콘텐츠)
        public static Color Surface       = Color.FromArgb(255, 255, 255);  // #FFFFFF 패널
        public static Color Card          = Color.FromArgb(255, 255, 255);  // #FFFFFF 카드
        public static Color CardHover     = Color.FromArgb(240, 242, 245);  // #F0F2F5 카드 호버
        public static Color Border        = Color.FromArgb(226, 230, 236);  // #E2E6EC 헤어라인
        public static Color NavBackground = Color.FromArgb(255, 255, 255);  // #FFFFFF 네비 레일
        public static Color Track         = Color.FromArgb(233, 236, 241);  // #E9ECF1 게이지 트랙(불투명)

        // ── 강조색 (인디고 — 액션/활성 전용, 아껴 쓴다) ──────────
        public static Color Accent        = Color.FromArgb(79,  70,  229);  // #4F46E5
        public static Color AccentHover   = Color.FromArgb(67,  56,  202);  // #4338CA
        public static Color AccentPressed = Color.FromArgb(55,  48,  163);  // #3730A3
        public static Color AccentGlow    = Color.FromArgb(24,  79,  70, 229); // 소프트 워시(저알파)

        // ── 상태 색상 (흰 배경 대비 확보용으로 채도/명도 재조정) ──
        public static Color Success       = Color.FromArgb(5,   150, 105);  // #059669 에메랄드-600
        public static Color SuccessDim    = Color.FromArgb(24,  5,   150, 105);
        public static Color Warning       = Color.FromArgb(217, 119,  6);   // #D97706 앰버-600
        public static Color WarningDim    = Color.FromArgb(24,  217, 119,  6);
        public static Color Danger        = Color.FromArgb(220, 38,   38);  // #DC2626 레드-600
        public static Color DangerDim     = Color.FromArgb(24,  220, 38,  38);

        // ── 상태 텍스트 (일반 크기 글자용, 흰 카드 대비 4.5:1 이상) ──
        // Success/Warning(600)은 큰 글자·게이지·아이콘용(3:1). 작은 글자에는 아래 700 단계를 쓴다.
        public static Color SuccessText   = Color.FromArgb(4,   120, 87);   // #047857 에메랄드-700 (흰색 대비 5.48:1)
        public static Color WarningText   = Color.FromArgb(180, 83,  9);    // #B45309 앰버-700   (흰색 대비 5.02:1)

        // ── 텍스트 (슬레이트 스케일) ───────────────────────────
        public static Color TextPrimary   = Color.FromArgb(15,  23,  42);   // #0F172A 슬레이트-900
        public static Color TextSecondary = Color.FromArgb(71,  85,  105);  // #475569 슬레이트-600
        // TextMuted: 흰 카드 대비 2.56:1 — 장식·비활성 전용. 읽어야 하는 텍스트에는 TextSecondary.
        public static Color TextMuted     = Color.FromArgb(148, 163, 184);  // #94A3B8 슬레이트-400
        public static Color TextOnAccent  = Color.FromArgb(255, 255, 255);  // 강조색 위 텍스트(흰색)

        // ── 네비 활성 배경 ─────────────────────────────────────
        public static Color NavActive     = Color.FromArgb(255, 255, 255);

        // ── 사용률 색상 ────────────────────────────────────────
        public static Color GetUsageColor(double percent)
        {
            if (percent < 60) return Success;
            if (percent < 80) return Warning;
            return Danger;
        }

        // 색 유틸 (컨트롤들이 강조색에서 파생색을 만들 때 사용)
        public static Color Darken(Color c, int amount) => Color.FromArgb(c.A,
            Math.Max(0, c.R - amount), Math.Max(0, c.G - amount), Math.Max(0, c.B - amount));
        public static Color WithAlpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);
        // a를 b 쪽으로 t(0~1)만큼 섞은 불투명색
        public static Color Blend(Color a, Color b, float t) => Color.FromArgb(255,
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));

        public static void ApplyToForm(Form form)
        {
            form.BackColor = Background;
            form.ForeColor = TextPrimary;
        }
    }
}
