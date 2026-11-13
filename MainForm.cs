using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MemoryCleaner.Controls;
using MemoryCleaner.Core;
using MemoryCleaner.UI;

namespace MemoryCleaner
{
    public class MainForm : Form
    {
        private AppSettings _settings;

        // 레이아웃 패널
        private Panel _navPanel;
        private Panel _contentPanel;

        // 네비게이션 버튼
        private NavButton _btnDashboard;
        private NavButton _btnLeakDetect;
        private NavButton _btnHistory;
        private NavButton _btnProcess;
        private NavButton _btnSettings;

        // 현재 화면
        private UserControl _currentControl;

        // 시스템 트레이
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;

        // 실시간 트레이 타이머
        private System.Timers.Timer _trayTimer;

        private bool _startMinimized;
        private bool _initialVisibilityHandled;

        public MainForm(bool startMinimized = false)
        {
            _startMinimized = startMinimized;
            _settings = AppSettings.Load();
            // 예약 작업 경로 동기화(exe 이동 대비). schtasks.exe 대기로 창 표시가
            // 늦어지지 않도록 백그라운드에서 실행한다.
            // 설정 파일을 읽지 못해 기본값으로 시작했으면 사용자의 실제 선택을 모르므로 건드리지 않는다.
            if (!_settings.LoadedFromFallback)
                _ = AppSettings.ApplyStartupRegistryAsync(_settings.RunOnStartup);
            InitForm();
            BuildLayout();
            BuildTray();
            AutoCleanManager.Start(_settings);
            AutoCleanManager.CleanPerformed += OnAutoClean;
            ProcessLeakTracker.LeakDetected += OnLeakDetected;
            Navigate("dashboard");

            // /minimized 시작(로그온 자동 실행)은 SetVisibleCore(false) 때문에 창을 열기 전까지
            // 핸들이 생기지 않아, 트레이 툴팁 갱신 등 BeginInvoke 경로가 전부 건너뛰어진다.
            // 보이지 않는 상태로 핸들만 미리 만든다.
            _ = Handle;

            StartUpdateChecks();
            if (!startMinimized)
                this.Load += (s, e) => { Show(); Activate(); BringToFront(); };
        }

        // Application.Run이 내부에서 Show()를 호출할 때 가로채서 startMinimized 시 숨김
        protected override void SetVisibleCore(bool value)
        {
            if (!_initialVisibilityHandled)
            {
                _initialVisibilityHandled = true;
                if (_startMinimized)
                {
                    base.SetVisibleCore(false);
                    return;
                }
            }
            base.SetVisibleCore(value);
        }

        // ─────────────────── 폼 초기화 ───────────────────
        private void InitForm()
        {
            Text = $"메모리클리너  v{Updater.CurrentVersionString}";
            Size = new Size(720, 560);
            MinimumSize = new Size(580, 460);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = DarkTheme.Background;
            ForeColor = DarkTheme.TextPrimary;
            FormBorderStyle = FormBorderStyle.None; // 커스텀 타이틀바

            // 커스텀 타이틀바 드래그
            this.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };

            this.FormClosing += OnFormClosing;
            this.Resize += (s, e) => this.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(DarkTheme.Border, 1))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            // Accent 상단 라인
            using (var pen = new Pen(DarkTheme.Accent, 2))
                e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }

        // ─────────────────── 레이아웃 ───────────────────
        private void BuildLayout()
        {
            // ── 커스텀 타이틀바
            var titleBar = new TitleBarPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = DarkTheme.NavBackground
            };
            titleBar.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };

            var lblAppTitle = new Label
            {
                Text = "Memory Cleaner",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = DarkTheme.TextSecondary,
                BackColor = Color.Transparent,
                Left = 16, Top = 0, Width = 200, Height = 40,
                TextAlign = ContentAlignment.MiddleLeft
            };

            // 최소화
            var btnMin = MakeTitleBarBtn("─", "최소화", DarkTheme.TextSecondary, DarkTheme.CardHover);
            btnMin.Left = titleBar.Width - 120;
            btnMin.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;

            // 트레이로 숨기기
            var btnClose = MakeTitleBarBtn("✕", "트레이로 숨기기", DarkTheme.TextSecondary, DarkTheme.CardHover);
            btnClose.Left = titleBar.Width - 80;
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.Click += (s, e) => Hide();

            // 완전 종료
            var btnExit = MakeTitleBarBtn("⏻", "앱 종료", DarkTheme.Danger, DarkTheme.WithAlpha(DarkTheme.Danger, 36));
            btnExit.Left = titleBar.Width - 40;
            btnExit.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnExit.Click += (s, e) => ExitApp();

            titleBar.Controls.Add(lblAppTitle);
            titleBar.Controls.Add(btnMin);
            titleBar.Controls.Add(btnClose);
            titleBar.Controls.Add(btnExit);

            // ── 좌측 네비게이션
            _navPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 152,
                BackColor = DarkTheme.NavBackground
            };

            var navTop = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = DarkTheme.NavBackground };

            _btnDashboard  = new NavButton("대시보드",   "○") { Dock = DockStyle.Top };
            _btnLeakDetect = new NavButton("누수 감지",  "○") { Dock = DockStyle.Top };
            _btnHistory    = new NavButton("정리 이력", "○") { Dock = DockStyle.Top };
            _btnProcess    = new NavButton("프로세스",  "○") { Dock = DockStyle.Top };
            _btnSettings   = new NavButton("환경설정",  "○") { Dock = DockStyle.Top };
            // 탭 순서 = 화면 위→아래 순서 (Dock 때문에 컬렉션 순서는 역순이라 명시한다)
            _btnDashboard.TabIndex  = 0;
            _btnLeakDetect.TabIndex = 1;
            _btnHistory.TabIndex    = 2;
            _btnProcess.TabIndex    = 3;
            _btnSettings.TabIndex   = 4;
            _btnDashboard.Click  += (s, e) => Navigate("dashboard");
            _btnLeakDetect.Click += (s, e) => Navigate("leak");
            _btnHistory.Click    += (s, e) => Navigate("history");
            _btnProcess.Click    += (s, e) => Navigate("process");
            _btnSettings.Click   += (s, e) => Navigate("settings");

            var lblVer = new Label
            {
                Text = $"v{Updater.CurrentVersionString}  ·  Freeware",
                Font = new Font("Segoe UI", 7f),
                ForeColor = DarkTheme.TextSecondary,
                Dock = DockStyle.Bottom,
                Height = 24,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = DarkTheme.NavBackground
            };

            var navBorder = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = DarkTheme.Border };

            _navPanel.Controls.Add(navBorder);
            _navPanel.Controls.Add(lblVer);
            _navPanel.Controls.Add(_btnSettings);
            _navPanel.Controls.Add(_btnProcess);
            _navPanel.Controls.Add(_btnHistory);
            _navPanel.Controls.Add(_btnLeakDetect);
            _navPanel.Controls.Add(_btnDashboard);
            _navPanel.Controls.Add(navTop);

            // ── 오른쪽 콘텐츠 영역
            _contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = DarkTheme.Background,
                Padding = new Padding(0)
            };

            Controls.Add(_contentPanel);
            Controls.Add(_navPanel);
            Controls.Add(titleBar);

            // 탭 순서: 타이틀바 버튼 → 내비 → 콘텐츠
            titleBar.TabIndex      = 0;
            _navPanel.TabIndex     = 1;
            _contentPanel.TabIndex = 2;
        }

        // 네이티브 Button(Flat): 탭 이동, Enter/Space, 포커스 표시, 접근성 이름을 플랫폼이 제공한다.
        private static Button MakeTitleBarBtn(string glyph, string accessibleName, Color iconColor, Color hoverBg)
        {
            var btn = new Button
            {
                Text = glyph,
                AccessibleName = accessibleName,
                Width = 40, Height = 40,
                Top = 0,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10f),
                ForeColor = iconColor,
                BackColor = DarkTheme.NavBackground,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = hoverBg;
            btn.FlatAppearance.MouseDownBackColor = hoverBg;
            return btn;
        }

        // ─────────────────── 네비게이션 ───────────────────
        private void Navigate(string page)
        {
            _btnDashboard.IsActive  = page == "dashboard";
            _btnLeakDetect.IsActive = page == "leak";
            _btnHistory.IsActive    = page == "history";
            _btnProcess.IsActive    = page == "process";
            _btnSettings.IsActive   = page == "settings";

            _currentControl?.Dispose();
            _contentPanel.Controls.Clear();

            UserControl ctrl;
            switch (page)
            {
                case "leak":
                    ctrl = new LeakDetectorControl();
                    break;
                case "history":
                    ctrl = new HistoryControl();
                    break;
                case "process":
                    ctrl = new ProcessControl();
                    break;
                case "settings":
                    var sc = new SettingsControl(_settings);
                    sc.SettingsChanged += (s, e) => AutoCleanManager.UpdateSettings(_settings);
                    sc.CheckUpdateRequested += (s, e) => _ = CheckForUpdatesAsync(userInitiated: true);
                    ctrl = sc;
                    break;
                default:
                    ctrl = new DashboardControl(_settings);
                    break;
            }

            ctrl.Dock = DockStyle.Fill;
            _contentPanel.Controls.Add(ctrl);
            _currentControl = ctrl;
        }

        // ─────────────────── 시스템 트레이 ───────────────────
        private void BuildTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.RenderMode = ToolStripRenderMode.System;
            _trayMenu.Items.Add("열기",  null, (s, e) => ShowWindow());
            // 정리는 수 초 걸릴 수 있으므로 UI 스레드를 막지 않도록 백그라운드에서 실행
            _trayMenu.Items.Add("정리하기", null, (s, e) => CleanFromTray());
            _trayMenu.Items.Add("업데이트 확인", null, (s, e) => _ = CheckForUpdatesAsync(userInitiated: true));
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("앱 종료", null, (s, e) => { _trayIcon.Visible = false; Application.Exit(); });

            _trayIcon = new NotifyIcon
            {
                Text = "메모리클리너",
                Visible = true,
                ContextMenuStrip = _trayMenu,
                Icon = CreateTrayIcon()
            };

            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (_settings.TrayClickClean)
                        CleanFromTray();
                    else
                        ShowWindow();
                }
            };

            // 트레이 아이콘 실시간 갱신 (5초마다)
            _trayTimer = new System.Timers.Timer(5000) { AutoReset = true };
            _trayTimer.Elapsed += (s, e) =>
            {
                // Elapsed는 스레드풀 스레드에서 실행되므로 UI 스레드로 마샬링
                PostToUi(UpdateTrayText);
            };
            _trayTimer.Start();

            AutoCleanManager.CleanPerformed += (s, e) =>
            {
                if (!e.ShowNotification) return;
                var entry = e.Entry;
                PostToUi(() =>
                {
                    if (entry.PartiallyFailed)
                    {
                        ShowBalloon(5000, "메모리클리너 — 일부 정리 실패",
                            $"일부 단계가 적용되지 않았습니다. 실제 메모리 {entry.AfterPhysical:F1}%. " +
                            "관리자 권한으로 실행했는지 확인하고, 계속되면 debug.log를 확인하세요.",
                            ToolTipIcon.Warning);
                    }
                    else
                    {
                        ShowBalloon(3000, "메모리클리너",
                            $"정리 완료 — 실제 메모리 {entry.AfterPhysical:F1}% ({entry.PhysicalSaved:+0.0;-0.0;0.0}% 절약)",
                            ToolTipIcon.Info);
                    }
                });
            };
        }

        // 다른 스레드에서 UI 작업을 안전하게 예약한다. 핸들이 없거나 이미 파괴 중이면 건너뛴다.
        private void PostToUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException ex)
            {
                // 확인 직후 핸들이 파괴된 경합(종료 중) — ObjectDisposedException 포함
                System.Diagnostics.Trace.WriteLine($"[MainForm] PostToUi 건너뜀: {ex.GetType().Name}");
            }
        }

        // ─────────────────── 트레이 풍선 ───────────────────
        // 마지막 풍선이 업데이트 알림이었는지 — 풍선 클릭 시 업데이트 승인 창을 띄울지 판단
        private bool _lastBalloonIsUpdate;

        private void ShowBalloon(int timeoutMs, string title, string text, ToolTipIcon icon, bool isUpdate = false)
        {
            if (_trayIcon == null) return;
            _lastBalloonIsUpdate = isUpdate;
            _trayIcon.ShowBalloonTip(timeoutMs, title, text, icon);
        }

        // ─────────────────── 자체 업데이트 ───────────────────
        private System.Threading.Timer _updateTimer;
        private ReleaseInfo _pendingUpdate;
        private int _updateBusy; // 0=유휴, 1=확인/설치 진행 중

        // 시작 30초 후 1회 + 24시간마다 확인(설정에서 끌 수 있음). 콜백은 UI 스레드로 넘긴다.
        private void StartUpdateChecks()
        {
            _trayIcon.BalloonTipClicked += (s, e) =>
            {
                if (_lastBalloonIsUpdate && _pendingUpdate != null)
                    _ = PromptAndInstallAsync(_pendingUpdate);
            };
            _updateTimer = new System.Threading.Timer(
                _ => PostToUi(() => _ = CheckForUpdatesAsync(userInitiated: false)),
                null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(24));
        }

        // userInitiated=true(설정/트레이 메뉴): 결과를 항상 창으로 알린다.
        // false(자동 확인): 새 버전이 있을 때만 풍선으로 알리고, 실패는 로그만 남긴다.
        private async System.Threading.Tasks.Task CheckForUpdatesAsync(bool userInitiated)
        {
            if (!userInitiated && !_settings.AutoCheckUpdates) return;
            if (System.Threading.Interlocked.CompareExchange(ref _updateBusy, 1, 0) != 0)
            {
                if (userInitiated)
                    ShowUpdateInfo("업데이트 확인 중", "이미 업데이트를 확인하거나 설치하는 중입니다. 잠시 후 다시 시도하세요.");
                return;
            }

            try
            {
                ReleaseInfo latest = await Updater.GetLatestAsync();
                System.Diagnostics.Trace.WriteLine($"[Update] 최신 {latest.Tag}, 현재 v{Updater.CurrentVersionString}");

                if (!Updater.IsNewer(latest.Version, Updater.CurrentVersion))
                {
                    if (userInitiated)
                        ShowUpdateInfo("최신 버전입니다", $"현재 v{Updater.CurrentVersionString}이(가) 최신 버전입니다.");
                    return;
                }

                _pendingUpdate = latest;
                if (userInitiated)
                {
                    System.Threading.Interlocked.Exchange(ref _updateBusy, 0);
                    await PromptAndInstallAsync(latest);
                }
                else
                {
                    ShowBalloon(10000, $"메모리클리너 v{latest.Version.ToString(3)} 업데이트",
                        "새 버전이 있습니다. 이 알림을 클릭하면 설치합니다.", ToolTipIcon.Info, isUpdate: true);
                }
            }
            catch (UpdateException ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Update] 확인 실패: {ex.Message} {ex.InnerException?.Message}");
                if (userInitiated)
                    ShowUpdateError("업데이트를 확인하지 못했습니다", ex.Message);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _updateBusy, 0);
            }
        }

        private async System.Threading.Tasks.Task PromptAndInstallAsync(ReleaseInfo latest)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _updateBusy, 1, 0) != 0) return;
            try
            {
                string notes = (latest.Notes ?? "").Trim();
                if (notes.Length > 600) notes = notes.Substring(0, 600) + "…";

                var install = new TaskDialogButton("지금 업데이트");
                var later = new TaskDialogButton("나중에");
                var page = new TaskDialogPage
                {
                    Caption = "메모리클리너 업데이트",
                    Heading = $"새 버전 v{latest.Version.ToString(3)}이(가) 있습니다",
                    Text = $"현재 버전 v{Updater.CurrentVersionString}. 설치하는 동안 앱이 잠시 종료됐다가 다시 시작됩니다." +
                           (notes.Length > 0 ? "\n\n" + notes : ""),
                    Icon = TaskDialogIcon.Information,
                    Buttons = { install, later },
                    DefaultButton = install
                };
                if (ShowTaskDialog(page) != install) return;

                ShowBalloon(5000, "메모리클리너", "업데이트를 받는 중입니다. 끝나면 자동으로 설치하고 다시 시작합니다.", ToolTipIcon.Info);
                string path = await Updater.DownloadAndVerifyAsync(latest);
                Updater.LaunchInstaller(path);
                ExitApp(); // 설치 파일이 파일을 교체하고 앱을 다시 띄운다
            }
            catch (UpdateException ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Update] 설치 실패: {ex.Message} {ex.InnerException?.Message}");
                ShowUpdateError("업데이트하지 못했습니다",
                    ex.Message + $"\n계속되면 {Updater.ReleasesPageUrl} 에서 설치 파일을 직접 받아 실행하세요.");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _updateBusy, 0);
            }
        }

        // 창이 숨겨져 있으면(트레이 상주) 소유자 없이 띄운다
        private TaskDialogButton ShowTaskDialog(TaskDialogPage page)
            => Visible ? TaskDialog.ShowDialog(this, page) : TaskDialog.ShowDialog(page);

        private void ShowUpdateInfo(string heading, string text)
            => ShowTaskDialog(new TaskDialogPage
            {
                Caption = "메모리클리너 업데이트", Heading = heading, Text = text,
                Icon = TaskDialogIcon.Information, Buttons = { TaskDialogButton.OK }
            });

        private void ShowUpdateError(string heading, string text)
            => ShowTaskDialog(new TaskDialogPage
            {
                Caption = "메모리클리너 업데이트", Heading = heading, Text = text,
                Icon = TaskDialogIcon.Error, Buttons = { TaskDialogButton.OK }
            });

        private void CleanFromTray()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                // 사용자가 직접 누른 정리 → 쿨다운 우회 요청(강력 정리 모드에서는 MemoryHelper가 무시)
                if (AutoCleanManager.PerformClean("트레이", bypassCooldown: true)) return;

                // 건너뛴 이유를 알려준다(조용히 무시하면 클릭이 먹통처럼 보인다)
                TimeSpan rem = MemoryHelper.GetCooldownRemaining();
                string msg = rem > TimeSpan.Zero
                    ? $"강력 정리는 60초에 한 번만 실행됩니다. {(int)Math.Ceiling(rem.TotalSeconds)}초 후 다시 시도하세요."
                    : "이미 정리 중입니다. 끝나면 결과를 알려드립니다.";
                PostToUi(() => ShowBalloon(3000, "메모리클리너", msg, ToolTipIcon.Info));
            });
        }

        private void UpdateTrayText()
        {
            if (_trayIcon == null || IsDisposed) return;
            var info = MemoryHelper.GetMemoryInfo();
            // NotifyIcon.Text 길이 제한(Shell_NotifyIcon szTip) 안쪽으로 자른다
            string text = $"메모리클리너\nRAM: {info.PhysicalPercent:F0}%  VM: {info.VirtualPercent:F0}%";
            if (text.Length > 63) text = text.Substring(0, 63);
            _trayIcon.Text = text;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static Icon CreateTrayIcon()
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(DarkTheme.Accent))
                        g.FillRectangle(brush, 0, 0, 16, 16);
                    using (var font = new Font("Arial", 9f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.White))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("M", font, brush, new RectangleF(0, 0, 16, 16), sf);
                    }
                }

                // GetHicon()이 만든 HICON은 관리되지 않으므로, 실제 아이콘으로 복제 후 원본 HICON 파괴
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(hIcon))
                        return (Icon)tmp.Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        // ─────────────────── 닫기 처리 ───────────────────
        private bool _forceExit = false;

        private void ExitApp()
        {
            _forceExit = true;
            _trayIcon.Visible = false;
            Application.Exit();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_forceExit) return;

            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide(); // 트레이로 최소화
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _trayTimer?.Stop();
                _trayTimer?.Dispose();
                _updateTimer?.Dispose();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    var icon = _trayIcon.Icon; // 복제 아이콘 GDI 정리
                    _trayIcon.Dispose();
                    icon?.Dispose();
                }
                AutoCleanManager.Stop();
            }
            base.Dispose(disposing);
        }

        private void OnAutoClean(object sender, CleanEventArgs e) { /* 트레이 알림은 BuildTray에서 처리 */ }

        // 누수 의심 발생 시 트레이 알림 (중복 방지를 위해 PID+1분 단위로 디바운싱)
        private readonly Dictionary<int, DateTime> _lastLeakNotify = new Dictionary<int, DateTime>();
        private readonly object _leakNotifyLock = new object();
        private void OnLeakDetected(object sender, ProcessLeakInfo info)
        {
            if (!_settings.NotifyOnLeakDetected) return;

            // 디바운스 판정은 경과 시간만 쓰므로 단조 시계 기준
            DateTime now = MonotonicClock.Now;

            // 타이머/이벤트 스레드에서 접근하므로 락으로 보호
            lock (_leakNotifyLock)
            {
                // 죽은 PID 엔트리가 무한 증가하지 않도록 1시간 이상 지난 항목 정리
                if (_lastLeakNotify.Count > 0)
                {
                    var stale = new List<int>();
                    foreach (var kv in _lastLeakNotify)
                        if ((now - kv.Value).TotalHours >= 1) stale.Add(kv.Key);
                    foreach (int pid in stale) _lastLeakNotify.Remove(pid);
                }

                if (_lastLeakNotify.TryGetValue(info.PID, out DateTime last)
                    && (now - last).TotalMinutes < 10)
                    return; // 같은 PID 10분 내 중복 알림 차단
                _lastLeakNotify[info.PID] = now;
            }

            string deltaStr = MemoryHelper.FormatBytes(info.DeltaBytes);
            string body = $"{info.Name} (PID {info.PID})\n{info.MinutesObserved:F0}분간 +{deltaStr} 증가";
            // 누수 감지 타이머 스레드에서 호출되므로 UI 스레드로 넘긴다
            PostToUi(() => ShowBalloon(5000, "메모리 누수 의심", body, ToolTipIcon.Warning));
        }
    }

    internal class TitleBarPanel : Panel
    {
        public TitleBarPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(DarkTheme.NavBackground);
            // 하단 구분선
            using (var pen = new Pen(DarkTheme.Border, 1))
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    }
}
