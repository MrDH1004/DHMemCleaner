using System;
using System.Drawing;
using System.Windows.Forms;
using MemoryCleaner.Core;
using MemoryCleaner.UI;

namespace MemoryCleaner.Controls
{
    public class SettingsControl : UserControl
    {
        private AppSettings _settings;
        private bool _loading = false;
        public event EventHandler SettingsChanged;
        // "지금 확인" 버튼 — 실제 확인·설치 흐름은 MainForm이 소유한다
        public event EventHandler CheckUpdateRequested;

        // 디스크 저장 디바운스용 타이머 (마지막 변경 후 일정 시간 뒤 1회 저장)
        private readonly Timer _saveDebounce;
        private bool _savePending = false;

        // 잘린 라벨의 전체 문구를 보여주는 툴팁(마우스 호버·키보드 포커스)
        private readonly ToolTip _toolTip = new ToolTip { AutoPopDelay = 15000 };

        private CheckBox _chkPhysical, _chkVirtual, _chkWS, _chkTime, _chkStartup, _chkTray;
        private ComboBox _cmbPhysical, _cmbVirtual, _cmbWS, _cmbTime;

        // 행 레이아웃 기준 폭. 행은 리사이즈 시 실제 폭으로 늘어나고, 우측 요소는 Anchor로 따라간다.
        private const int RowBaseWidth = 500;
        private const int ComboWidth   = 76;
        private const int UnitWidth    = 24;
        private const int RowPadding   = 14;

        public SettingsControl(AppSettings settings)
        {
            _settings = settings;
            BackColor = DarkTheme.Background;
            Dock = DockStyle.Fill;

            _saveDebounce = new Timer { Interval = 700 };
            _saveDebounce.Tick += (s, e) => { _saveDebounce.Stop(); FlushSave(); };

            _loading = true;
            BuildUI();
            LoadSettings();
            _loading = false;
        }

        private void BuildUI()
        {
            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = DarkTheme.Background
            };
            Controls.Add(scroll);

            var lblTitle = new Label
            {
                Text = "환경설정",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = DarkTheme.TextPrimary,
                Dock = DockStyle.Top,
                Height = 52,
                Padding = new Padding(24, 16, 0, 0)
            };
            scroll.Controls.Add(lblTitle);

            var inner = new Panel
            {
                Left = 0, Top = 52,
                BackColor = DarkTheme.Background,
                Padding = new Padding(24, 0, 24, 24)
            };
            scroll.Controls.Add(inner);

            int y = 0;

            AddSection(inner, "자동 정리 조건", ref y);
            _chkPhysical = AddRow(inner, ref y, "실제 메모리(RAM) 사용률이 이상일 때",
                out _cmbPhysical, new[] { "40", "50", "60", "70", "80", "90" }, "60", "%");
            _chkVirtual = AddRow(inner, ref y, "가상 메모리 사용률이 이상일 때",
                out _cmbVirtual, new[] { "70", "80", "90", "95" }, "90", "%");
            _chkWS = AddRow(inner, ref y, "워킹셋 사용률이 이상일 때",
                out _cmbWS, new[] { "60", "70", "80", "90", "95" }, "90", "%");
            _chkTime = AddRow(inner, ref y, "마다 자동 정리 (분 간격)",
                out _cmbTime, new[] { "15", "20", "30", "45", "60", "120" }, "30", "분");

            y += 8;
            AddSection(inner, "스마트 기능", ref y);
            // 각 토글은 자기 필드만 갱신한 뒤 한 번만 저장한다(값 반영 전 저장 금지).
            var chkGame = AddToggle(inner, ref y, "게임 모드 (풀스크린 앱 실행 중 자동정리 일시중지)");
            chkGame.Checked = _settings.PauseOnFullscreen;
            chkGame.CheckedChanged += (s, e) => { _settings.PauseOnFullscreen = chkGame.Checked; Save(); };

            var chkLeak = AddToggle(inner, ref y, "메모리 누수 감지 (백그라운드 모니터링)");
            chkLeak.Checked = _settings.EnableLeakDetector;
            chkLeak.CheckedChanged += (s, e) => { _settings.EnableLeakDetector = chkLeak.Checked; Save(); };

            var chkLeakNotify = AddToggle(inner, ref y, "누수 의심 발견 시 알림");
            chkLeakNotify.Checked = _settings.NotifyOnLeakDetected;
            chkLeakNotify.CheckedChanged += (s, e) => { _settings.NotifyOnLeakDetected = chkLeakNotify.Checked; Save(); };

            var chkFull = AddToggle(inner, ref y, "강력 정리 (불안정한 PC에서 재부팅 위험)",
                "전체 워킹셋·Standby·파일 캐시까지 비웁니다. 정리 직후 부하가 급증해, " +
                "하드웨어가 불안정한 PC에서는 시스템이 재시작될 수 있습니다. 끄면 저우선순위 Standby만 정리합니다.");
            chkFull.Checked = _settings.FullClean;
            chkFull.CheckedChanged += (s, e) => { _settings.FullClean = chkFull.Checked; Save(); };

            y += 8;
            AddSection(inner, "기본 설정", ref y);
            _chkStartup = AddToggle(inner, ref y, "부팅 시 자동 실행");
            _chkStartup.CheckedChanged += (s, e) => Save();
            _chkTray    = AddToggle(inner, ref y, "트레이 클릭 시 즉시 정리");
            _chkTray.CheckedChanged += (s, e) => Save();

            y += 8;
            AddSection(inner, "알림", ref y);
            var chkWarn = AddToggle(inner, ref y, $"워킹셋 {_settings.WorkingSetWarningThreshold}% 초과 시 경고 표시");
            chkWarn.Checked = _settings.ShowWorkingSetWarning;
            chkWarn.CheckedChanged += (s, e) => { _settings.ShowWorkingSetWarning = chkWarn.Checked; Save(); };

            var chkResult = AddToggle(inner, ref y, "정리 후 결과 알림 표시");
            chkResult.Checked = _settings.ShowCleanResultNotification;
            chkResult.CheckedChanged += (s, e) => { _settings.ShowCleanResultNotification = chkResult.Checked; Save(); };

            y += 8;
            AddSection(inner, "업데이트", ref y);
            var chkUpdate = AddToggle(inner, ref y, "자동으로 업데이트 확인 (새 버전은 알림 후 설치)");
            chkUpdate.Checked = _settings.AutoCheckUpdates;
            chkUpdate.CheckedChanged += (s, e) => { _settings.AutoCheckUpdates = chkUpdate.Checked; Save(); };
            AddUpdateRow(inner, ref y);

            int contentHeight = y + inner.Padding.Bottom;
            inner.Height = contentHeight;

            // 절대 배치 자식에는 Padding이 적용되지 않으므로 좌측 여백/폭을 직접 맞춘다
            // (제목의 24px 들여쓰기와 같은 정렬선).
            scroll.Resize += (s, e) =>
            {
                int w = scroll.ClientSize.Width;
                inner.Width = w;
                inner.Height = Math.Max(contentHeight, scroll.ClientSize.Height - 52);
                foreach (Control c in inner.Controls)
                {
                    c.Left = inner.Padding.Left;
                    c.Width = Math.Max(0, w - inner.Padding.Horizontal);
                }
            };
        }

        private void AddSection(Panel parent, string title, ref int y)
        {
            var lbl = new Label
            {
                Text = title.ToUpper(),
                Left = 0, Top = y,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = DarkTheme.TextSecondary,
                Height = 22,
                AutoSize = false
            };
            parent.Controls.Add(lbl);
            y += 26;

            var line = new Panel { Left = 0, Top = y, Height = 1, BackColor = DarkTheme.Border };
            parent.Controls.Add(line);
            y += 10;
        }

        private CheckBox AddRow(Panel parent, ref int y, string label,
            out ComboBox cmb, string[] options, string defaultVal, string unit)
        {
            var row = new Panel { Left = 0, Top = y, Width = RowBaseWidth, Height = 46, BackColor = DarkTheme.Card };

            int unitLeft  = RowBaseWidth - RowPadding - UnitWidth;
            int comboLeft = unitLeft - 4 - ComboWidth;

            var chk = new CheckBox
            {
                Text = label,
                Left = RowPadding, Top = 0,
                Font = new Font("Segoe UI", 9f),
                ForeColor = DarkTheme.TextPrimary,
                AutoSize = false, Width = comboLeft - RowPadding - 8, Height = 46,
                AutoEllipsis = true,
                BackColor = DarkTheme.Card,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _toolTip.SetToolTip(chk, label);

            var combo = new ComboBox
            {
                Left = comboLeft, Top = 11,
                Width = ComboWidth, Height = 24,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9f),
                BackColor = DarkTheme.Surface,
                ForeColor = DarkTheme.TextPrimary,
                FlatStyle = FlatStyle.Flat,
                Enabled = false,
                AccessibleName = $"{label} — 기준값({unit})",
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            combo.Items.AddRange(options);
            int defIdx = Array.IndexOf(options, defaultVal);
            combo.SelectedIndex = defIdx >= 0 ? defIdx : 0;

            var lblUnit = new Label
            {
                Text = unit,
                Left = unitLeft, Top = 15, Width = UnitWidth,
                Font = new Font("Segoe UI", 9f),
                ForeColor = DarkTheme.TextSecondary,
                BackColor = DarkTheme.Card,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            chk.CheckedChanged += (s, e) => { combo.Enabled = chk.Checked; Save(); };
            combo.SelectedIndexChanged += (s, e) => Save();

            row.Controls.Add(chk);
            row.Controls.Add(combo);
            row.Controls.Add(lblUnit);
            parent.Controls.Add(row);
            y += 50;
            cmb = combo;
            return chk;
        }

        // 저장 처리는 호출부가 붙인다(이 메서드는 Save를 연결하지 않는다).
        private CheckBox AddToggle(Panel parent, ref int y, string label, string detail = null)
        {
            var row = new Panel { Left = 0, Top = y, Width = RowBaseWidth, Height = 46, BackColor = DarkTheme.Card };

            var chk = new CheckBox
            {
                Text = label,
                Left = RowPadding, Top = 0,
                Font = new Font("Segoe UI", 9f),
                ForeColor = DarkTheme.TextPrimary,
                AutoSize = false, Width = RowBaseWidth - RowPadding * 2, Height = 46,
                AutoEllipsis = true,
                BackColor = DarkTheme.Card,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            string tip = detail == null ? label : label + "\n" + detail;
            _toolTip.SetToolTip(chk, tip);
            if (detail != null) chk.AccessibleDescription = detail;

            row.Controls.Add(chk);
            parent.Controls.Add(row);
            y += 50;
            return chk;
        }

        // 저장된 값이 미리 정한 목록에 없으면(구버전·수동 편집, 예: 85) 목록의 오름차순 위치에
        // 그 값을 끼워 넣어 선택한다. 첫 항목(예: 40)으로 바꿔 보여주면 다른 설정을 저장할 때
        // 사용자가 모르게 임계치가 덮어써진다.
        // 현재 버전 + "지금 확인" 버튼 행
        private void AddUpdateRow(Panel parent, ref int y)
        {
            var row = new Panel { Left = 0, Top = y, Width = RowBaseWidth, Height = 46, BackColor = DarkTheme.Card };
            const int btnWidth = 96;

            var lbl = new Label
            {
                Text = $"현재 버전 v{Updater.CurrentVersionString}",
                Left = RowPadding, Top = 0, Height = 46,
                Width = RowBaseWidth - RowPadding * 2 - btnWidth - 8,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9f),
                ForeColor = DarkTheme.TextPrimary,
                BackColor = DarkTheme.Card,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var btn = new RoundButton("지금 확인")
            {
                Left = RowBaseWidth - RowPadding - btnWidth, Top = 8, Width = btnWidth, Height = 30,
                Font = new Font("Segoe UI", 8.5f),
                BaseColor = DarkTheme.Card,
                HoverColor = DarkTheme.CardHover,
                PressColor = DarkTheme.Border,
                AccessibleName = "업데이트 지금 확인",
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btn.Click += (s, e) => CheckUpdateRequested?.Invoke(this, EventArgs.Empty);

            row.Controls.Add(lbl);
            row.Controls.Add(btn);
            parent.Controls.Add(row);
            y += 50;
        }

        internal static void SetComboValue(ComboBox cmb, int value)
        {
            string val = value.ToString();
            int idx = cmb.Items.IndexOf(val);
            if (idx < 0)
            {
                idx = cmb.Items.Count;
                for (int i = 0; i < cmb.Items.Count; i++)
                {
                    if (int.TryParse(cmb.Items[i]?.ToString(), out int item) && item > value)
                    {
                        idx = i;
                        break;
                    }
                }
                cmb.Items.Insert(idx, val);
            }
            cmb.SelectedIndex = idx;
        }

        private void LoadSettings()
        {
            _chkPhysical.Checked = _settings.AutoCleanPhysical;
            SetComboValue(_cmbPhysical, _settings.AutoCleanPhysicalThreshold);
            _cmbPhysical.Enabled = _settings.AutoCleanPhysical;

            _chkVirtual.Checked = _settings.AutoCleanVirtual;
            SetComboValue(_cmbVirtual, _settings.AutoCleanVirtualThreshold);
            _cmbVirtual.Enabled = _settings.AutoCleanVirtual;

            _chkWS.Checked = _settings.AutoCleanWorkingSet;
            SetComboValue(_cmbWS, _settings.AutoCleanWorkingSetThreshold);
            _cmbWS.Enabled = _settings.AutoCleanWorkingSet;

            _chkTime.Checked = _settings.AutoCleanByTime;
            SetComboValue(_cmbTime, _settings.AutoCleanIntervalMinutes);
            _cmbTime.Enabled = _settings.AutoCleanByTime;

            _chkStartup.Checked = _settings.RunOnStartup;
            _chkTray.Checked = _settings.TrayClickClean;
        }

        private void Save()
        {
            if (_loading) return;
            _settings.AutoCleanPhysical = _chkPhysical.Checked;
            if (int.TryParse(_cmbPhysical.SelectedItem?.ToString(), out int p)) _settings.AutoCleanPhysicalThreshold = p;

            _settings.AutoCleanVirtual = _chkVirtual.Checked;
            if (int.TryParse(_cmbVirtual.SelectedItem?.ToString(), out int v)) _settings.AutoCleanVirtualThreshold = v;

            _settings.AutoCleanWorkingSet = _chkWS.Checked;
            if (int.TryParse(_cmbWS.SelectedItem?.ToString(), out int w)) _settings.AutoCleanWorkingSetThreshold = w;

            _settings.AutoCleanByTime = _chkTime.Checked;
            if (int.TryParse(_cmbTime.SelectedItem?.ToString(), out int t)) _settings.AutoCleanIntervalMinutes = t;

            // 부팅 시 자동 실행은 값이 바뀐 경우에만 반영하고, schtasks.exe 대기가
            // UI 스레드를 막지 않도록 백그라운드에서 실행한다.
            bool startup = _chkStartup.Checked;
            if (startup != _settings.RunOnStartup)
            {
                _settings.RunOnStartup = startup;
                _ = AppSettings.ApplyStartupRegistryAsync(startup);
            }
            _settings.TrayClickClean = _chkTray.Checked;

            // AutoClean 반영은 SettingsChanged 이벤트를 통해 MainForm이 UpdateSettings로 단일 경로 처리
            SettingsChanged?.Invoke(this, EventArgs.Empty);

            // 디스크 저장은 디바운스 (마지막 변경 후 700ms 뒤 1회)
            _savePending = true;
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        // 디바운스된 실제 디스크 저장
        private void FlushSave()
        {
            if (!_savePending) return;
            _savePending = false;
            _settings.Save();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _saveDebounce?.Stop();
                // 컨트롤 파괴 시 대기 중인 저장을 flush
                FlushSave();
                _saveDebounce?.Dispose();
                _toolTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
