using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MemoryCleaner.Core
{
    // GitHub Releases 최신 릴리즈 정보
    public sealed class ReleaseInfo
    {
        public Version Version { get; set; }
        public string Tag { get; set; }
        public string Notes { get; set; }
        public string HtmlUrl { get; set; }
        public string InstallerName { get; set; }
        public string InstallerUrl { get; set; }
        public string ChecksumUrl { get; set; }
    }

    // 사용자에게 보여줄 수 있는 업데이트 실패(원인 + 다음 행동이 담긴 메시지)
    public sealed class UpdateException : Exception
    {
        public UpdateException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// GitHub Releases 기반 자체 업데이트.
    ///  1) api.github.com/repos/{Owner}/{Repo}/releases/latest 조회
    ///  2) tag(vX.Y.Z)가 현재 버전보다 높으면 설치 파일 + .sha256 체크섬 다운로드
    ///  3) SHA-256 일치 확인 후 Inno Setup 설치 파일을 무인 모드로 실행(/UPDATE=1 → 설치 후 앱 재시작)
    /// 릴리즈 자산 이름 규칙: DHMemCleaner_Setup_X.Y.Z.exe, DHMemCleaner_Setup_X.Y.Z.exe.sha256
    /// </summary>
    public static class Updater
    {
        public const string Owner = "MrDH1004";
        public const string Repo  = "DHMemCleaner";
        public const string InstallerPrefix = "DHMemCleaner_Setup_";

        public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases";

        // 설치 파일 무인 실행 인자. /UPDATE=1은 installer.iss의 [Run]에서 설치 후 앱을 다시 띄우는 표시.
        public const string SilentInstallArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE=1";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            // GitHub API는 User-Agent 필수. 개인 정보가 없는 일반 클라이언트 이름만 보낸다.
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DHMemCleaner-Updater", CurrentVersionString));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return http;
        }

        // 실행 중인 앱 버전(csproj <Version> → AssemblyVersion). 비교는 Major.Minor.Build 3자리.
        public static Version CurrentVersion
        {
            get
            {
                Version v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
                return Normalize(v);
            }
        }

        public static string CurrentVersionString => CurrentVersion.ToString(3);

        private static Version Normalize(Version v)
            => new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

        // ── 순수 함수 (단위 테스트 대상) ──────────────────────────

        // "v1.2.3" / "1.2.3" → 1.2.3
        internal static bool TryParseTag(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            if (!Version.TryParse(s, out Version parsed)) return false;
            version = Normalize(parsed);
            return true;
        }

        internal static bool IsNewer(Version latest, Version current)
            => latest != null && current != null && latest.CompareTo(current) > 0;

        // GitHub releases/latest JSON → ReleaseInfo. 규칙에 맞는 설치 파일·체크섬이 없으면 UpdateException.
        internal static ReleaseInfo ParseRelease(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    string tag = GetString(root, "tag_name");
                    if (!TryParseTag(tag, out Version version))
                        throw new UpdateException($"릴리즈 태그 '{tag}'에서 버전을 읽지 못했습니다.");

                    string expectedName = InstallerPrefix + version.ToString(3) + ".exe";
                    string installerUrl = null, checksumUrl = null;

                    if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement a in assets.EnumerateArray())
                        {
                            string name = GetString(a, "name");
                            string url = GetString(a, "browser_download_url");
                            if (string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
                                installerUrl = url;
                            else if (string.Equals(name, expectedName + ".sha256", StringComparison.OrdinalIgnoreCase))
                                checksumUrl = url;
                        }
                    }

                    if (installerUrl == null || checksumUrl == null)
                        throw new UpdateException($"릴리즈 {tag}에 설치 파일({expectedName}) 또는 체크섬 파일이 없습니다.");

                    return new ReleaseInfo
                    {
                        Version = version,
                        Tag = tag,
                        Notes = GetString(root, "body") ?? "",
                        HtmlUrl = GetString(root, "html_url") ?? ReleasesPageUrl,
                        InstallerName = expectedName,
                        InstallerUrl = installerUrl,
                        ChecksumUrl = checksumUrl
                    };
                }
            }
            catch (JsonException ex)
            {
                throw new UpdateException("릴리즈 정보를 해석하지 못했습니다.", ex);
            }
        }

        private static string GetString(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // sha256sum 형식("<hex>  <파일명>" 또는 "<hex> *<파일명>") 또는 해시만 있는 파일에서 해시를 읽는다.
        internal static string ParseChecksum(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            string first = content.Trim().TrimStart('﻿').Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries)[0];
            if (first.Length != 64) return null;
            foreach (char c in first)
                if (!Uri.IsHexDigit(c)) return null;
            return first.ToLowerInvariant();
        }

        // 다운로드 URL은 https + github.com 만 허용(리다이렉트로 이어지는 CDN은 HttpClient가 따라간다).
        internal static bool IsTrustedDownloadUrl(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri u)
               && u.Scheme == Uri.UriSchemeHttps
               && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase);

        // ── 네트워크 ───────────────────────────────────────────────

        public static async Task<ReleaseInfo> GetLatestAsync(CancellationToken ct = default)
        {
            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            try
            {
                using (HttpResponseMessage resp = await Http.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        throw new UpdateException("아직 게시된 릴리즈가 없습니다.");
                    if (!resp.IsSuccessStatusCode)
                        throw new UpdateException($"업데이트 서버 응답 오류({(int)resp.StatusCode}). 잠시 후 다시 시도하세요.");
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return ParseRelease(json);
                }
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException("업데이트 서버에 연결하지 못했습니다. 인터넷 연결을 확인하고 다시 시도하세요.", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new UpdateException("업데이트 확인 시간이 초과됐습니다. 인터넷 연결을 확인하고 다시 시도하세요.", ex);
            }
        }

        // 설치 파일을 받아 SHA-256을 검증하고 로컬 경로를 돌려준다. 불일치면 파일을 지우고 UpdateException.
        public static async Task<string> DownloadAndVerifyAsync(ReleaseInfo release, CancellationToken ct = default)
        {
            if (!IsTrustedDownloadUrl(release.InstallerUrl) || !IsTrustedDownloadUrl(release.ChecksumUrl))
                throw new UpdateException("신뢰할 수 없는 다운로드 주소입니다. 업데이트를 중단했습니다.");

            string dir = Path.Combine(Path.GetTempPath(), "DHMemCleaner_update");
            string path = Path.Combine(dir, release.InstallerName);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                Directory.CreateDirectory(dir);

                string checksumText = await Http.GetStringAsync(release.ChecksumUrl, ct).ConfigureAwait(false);
                string expected = ParseChecksum(checksumText)
                    ?? throw new UpdateException("체크섬 파일 형식이 올바르지 않습니다. 업데이트를 중단했습니다.");

                using (HttpResponseMessage resp = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new UpdateException($"설치 파일 다운로드 실패({(int)resp.StatusCode}). 잠시 후 다시 시도하세요.");
                    using (Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    using (FileStream dst = File.Create(path))
                        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }

                string actual = ComputeSha256(path);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    Trace.WriteLine($"[Updater] SHA-256 불일치: expected={expected} actual={actual}");
                    throw new UpdateException("다운로드한 파일이 손상됐습니다(체크섬 불일치). 다시 시도하세요.");
                }

                Trace.WriteLine($"[Updater] 다운로드·검증 완료: {path} sha256={actual}");
                return path;
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException("설치 파일을 받지 못했습니다. 인터넷 연결을 확인하고 다시 시도하세요.", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new UpdateException("다운로드 시간이 초과됐습니다. 다시 시도하세요.", ex);
            }
            catch (IOException ex)
            {
                throw new UpdateException($"임시 폴더에 설치 파일을 저장하지 못했습니다: {ex.Message}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UpdateException($"임시 폴더 접근이 거부됐습니다: {ex.Message}", ex);
            }
        }

        internal static string ComputeSha256(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }

        // 검증된 설치 파일을 무인 모드로 실행한다. 호출자는 직후 앱을 종료해야 파일 교체가 깔끔하다
        // (설치 파일도 /CLOSEAPPLICATIONS + PrepareToInstall에서 남은 인스턴스를 닫는다).
        public static void LaunchInstaller(string installerPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = SilentInstallArgs,
                    UseShellExecute = true   // 앱이 관리자 권한이므로 설치 파일도 상승 상태로 실행(UAC 없음)
                };
                Process.Start(psi);
                Trace.WriteLine($"[Updater] 설치 실행: {installerPath} {SilentInstallArgs}");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new UpdateException($"설치 파일을 실행하지 못했습니다({ex.NativeErrorCode}). {ReleasesPageUrl} 에서 직접 받아 설치하세요.", ex);
            }
        }

        // 무인 업데이트(명령줄 /update): 확인 → 다운로드·검증 → 설치 실행. 반환: 0=최신/설치 시작, 1=실패
        public static int RunHeadless()
        {
            try
            {
                Trace.WriteLine($"[Updater] /update 시작 — 현재 v{CurrentVersionString}");
                ReleaseInfo latest = GetLatestAsync().GetAwaiter().GetResult();
                Trace.WriteLine($"[Updater] 최신 릴리즈 {latest.Tag}");
                if (!IsNewer(latest.Version, CurrentVersion))
                {
                    Trace.WriteLine("[Updater] 이미 최신 버전");
                    return 0;
                }
                string path = DownloadAndVerifyAsync(latest).GetAwaiter().GetResult();
                LaunchInstaller(path);
                return 0;
            }
            catch (UpdateException ex)
            {
                Trace.WriteLine($"[Updater] 실패: {ex.Message} {ex.InnerException?.Message}");
                return 1;
            }
        }
    }
}
