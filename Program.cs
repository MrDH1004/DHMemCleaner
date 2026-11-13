using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace MemoryCleaner
{
    // 모든 Trace.WriteLine 줄 앞에 로컬 시각(ms)을 붙인다.
    // 시각이 있어야 Windows 이벤트 로그(비정상 재부팅 등)와 대조할 수 있다.
    internal sealed class TimestampTraceListener : TextWriterTraceListener
    {
        public TimestampTraceListener(TextWriter writer) : base(writer) { }

        public override void WriteLine(string message)
            => base.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    }

    static class Program
    {
        private static readonly string MutexName = "MemoryCleaner_KILHO_SingleInstance";

        // AC1.4: debug.log 경로 (%AppData%\KILHO.net\MemoryCleaner\debug.log)
        private static readonly string DebugLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KILHO.net", "MemoryCleaner", "debug.log");

        private const long DebugLogMaxBytes = 10L * 1024 * 1024; // 10MB

        // AC1.4: Debug.WriteLine 출력을 debug.log로 라우팅하는 리스너를 설정하고,
        // 세션 헤더(PID, 권한 상승 여부)를 기록한다.
        private static void InitDebugLog()
        {
            try
            {
                string dir = Path.GetDirectoryName(DebugLogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 10MB 초과 시 .old로 롤오버
                if (File.Exists(DebugLogPath) &&
                    new FileInfo(DebugLogPath).Length >= DebugLogMaxBytes)
                {
                    string old = DebugLogPath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(DebugLogPath, old);
                }

                // 추가 모드 TextWriterTraceListener + 자동 플러시
                var listener = new TimestampTraceListener(
                    new StreamWriter(DebugLogPath, append: true) { AutoFlush = true });
                Trace.Listeners.Add(listener);
                Trace.AutoFlush = true;

                int pid = Environment.ProcessId;
                Trace.WriteLine($"=== Session Start [PID:{pid}] Elevated:{IsElevated()} ===");
            }
            // 로깅 초기화 실패는 앱 동작에 영향을 주지 않는다. 로그 파일이 없으니 디버거 출력으로만 남긴다.
            catch (IOException ex)
            {
                Debug.WriteLine($"[Program] debug.log 초기화 실패 (IO): {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[Program] debug.log 초기화 실패 (access): {ex.Message}");
            }
        }

        private static bool IsElevated()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (System.Security.SecurityException ex)
            {
                Debug.WriteLine($"[Program] 권한 상승 여부 확인 실패: {ex.Message}");
                return false;
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            InitDebugLog();
            Trace.WriteLine($"[Program] v{MemoryCleaner.Core.Updater.CurrentVersionString} args=[{string.Join(" ", args)}]");

            // 무인 업데이트: 창·트레이 없이 확인→다운로드·검증→설치 실행 후 종료.
            // 상주 인스턴스와 별개로 돌 수 있도록 단일 인스턴스 뮤텍스보다 먼저 처리한다
            // (설치 파일이 상주 인스턴스를 닫고, 설치 후 앱을 다시 띄운다).
            if (Array.Exists(args, a => a.Equals("/update", StringComparison.OrdinalIgnoreCase)))
                return MemoryCleaner.Core.Updater.RunHeadless();

            Application.ThreadException += (s, e) =>
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now}] ThreadException: {e.Exception}\r\n");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now}] UnhandledException: {e.ExceptionObject}\r\n");

            // 중복 실행 방지
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("메모리클리너가 이미 실행 중입니다.\n시스템 트레이를 확인하세요.",
                        "메모리클리너", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                        $"[{DateTime.Now}] 중복 실행 감지 - 종료\r\n");
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool startMinimized = Array.Exists(args, a =>
                    a.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

                try
                {
                    var form = new MainForm(startMinimized);
                    Application.Run(form);
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                        $"[{DateTime.Now}] Startup crash: {ex}\r\n");
                    MessageBox.Show("실행 오류:\n" + ex.Message, "오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
            return 0;
        }
    }
}
