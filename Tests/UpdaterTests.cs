using System;
using MemoryCleaner.Core;

namespace MemoryCleaner.Tests
{
    // Updater의 네트워크 없는 판정 로직 검증 (실제 다운로드·설치는 릴리즈 E2E로 검증)
    public class UpdaterTests
    {
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static string ReleaseJson(string tag, params string[] assetNames)
        {
            var assets = new System.Text.StringBuilder();
            for (int i = 0; i < assetNames.Length; i++)
            {
                if (i > 0) assets.Append(',');
                assets.Append($"{{\"name\":\"{assetNames[i]}\",\"browser_download_url\":\"https://github.com/MrDH1004/DHMemCleaner/releases/download/{tag}/{assetNames[i]}\"}}");
            }
            return $"{{\"tag_name\":\"{tag}\",\"html_url\":\"https://github.com/MrDH1004/DHMemCleaner/releases/tag/{tag}\",\"body\":\"notes\",\"assets\":[{assets}]}}";
        }

        [Theory]
        [InlineData("v1.2.3", 1, 2, 3)]
        [InlineData("1.2.3", 1, 2, 3)]
        [InlineData("V10.0.1", 10, 0, 1)]
        [InlineData("v1.2", 1, 2, 0)]
        public void TryParseTag_Valid(string tag, int ma, int mi, int b)
        {
            Assert.True(Updater.TryParseTag(tag, out Version v));
            Assert.Equal(new Version(ma, mi, b), v);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("latest")]
        [InlineData("v1.x")]
        public void TryParseTag_Invalid(string tag)
        {
            Assert.False(Updater.TryParseTag(tag, out _));
        }

        [Fact]
        public void IsNewer_ComparesSemantically()
        {
            Assert.True(Updater.IsNewer(new Version(1, 1, 1), new Version(1, 1, 0)));
            Assert.True(Updater.IsNewer(new Version(1, 10, 0), new Version(1, 9, 9)));  // 문자열 비교 함정
            Assert.False(Updater.IsNewer(new Version(1, 1, 0), new Version(1, 1, 0)));
            Assert.False(Updater.IsNewer(new Version(1, 0, 9), new Version(1, 1, 0)));
            Assert.False(Updater.IsNewer(null, new Version(1, 0, 0)));
        }

        [Fact]
        public void ParseRelease_PicksMatchingInstallerAndChecksum()
        {
            string json = ReleaseJson("v1.2.3",
                "Source.zip", "DHMemCleaner_Setup_1.2.3.exe", "DHMemCleaner_Setup_1.2.3.exe.sha256",
                "DHMemCleaner_Setup_1.2.2.exe");
            ReleaseInfo r = Updater.ParseRelease(json);

            Assert.Equal(new Version(1, 2, 3), r.Version);
            Assert.Equal("DHMemCleaner_Setup_1.2.3.exe", r.InstallerName);
            Assert.EndsWith("/DHMemCleaner_Setup_1.2.3.exe", r.InstallerUrl);
            Assert.EndsWith("/DHMemCleaner_Setup_1.2.3.exe.sha256", r.ChecksumUrl);
            Assert.Equal("notes", r.Notes);
        }

        [Fact]
        public void ParseRelease_MissingChecksum_Throws()
        {
            string json = ReleaseJson("v1.2.3", "DHMemCleaner_Setup_1.2.3.exe");
            Assert.Throws<UpdateException>(() => Updater.ParseRelease(json));
        }

        [Fact]
        public void ParseRelease_InstallerVersionMustMatchTag()
        {
            // 태그는 1.2.3인데 자산은 1.2.2 → 엉뚱한 설치 파일을 받지 않도록 거부
            string json = ReleaseJson("v1.2.3", "DHMemCleaner_Setup_1.2.2.exe", "DHMemCleaner_Setup_1.2.2.exe.sha256");
            Assert.Throws<UpdateException>(() => Updater.ParseRelease(json));
        }

        [Fact]
        public void ParseRelease_InvalidJson_ThrowsUpdateException()
        {
            Assert.Throws<UpdateException>(() => Updater.ParseRelease("{not json"));
        }

        [Theory]
        [InlineData(Sha + "  DHMemCleaner_Setup_1.2.3.exe\n")]
        [InlineData(Sha + " *DHMemCleaner_Setup_1.2.3.exe")]
        [InlineData(Sha)]
        [InlineData("﻿" + Sha + "  x.exe")]
        public void ParseChecksum_AcceptsSha256sumFormats(string content)
        {
            Assert.Equal(Sha, Updater.ParseChecksum(content));
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc  file.exe")]
        [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
        public void ParseChecksum_RejectsInvalid(string content)
        {
            Assert.Null(Updater.ParseChecksum(content));
        }

        [Theory]
        [InlineData("https://github.com/MrDH1004/DHMemCleaner/releases/download/v1/x.exe", true)]
        [InlineData("http://github.com/MrDH1004/DHMemCleaner/releases/download/v1/x.exe", false)]
        [InlineData("https://evil.example.com/x.exe", false)]
        [InlineData("https://github.com.evil.example/x.exe", false)]
        [InlineData("not a url", false)]
        public void IsTrustedDownloadUrl(string url, bool expected)
        {
            Assert.Equal(expected, Updater.IsTrustedDownloadUrl(url));
        }
    }
}
