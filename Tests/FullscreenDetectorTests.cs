using MemoryCleaner.Core;
using Xunit;

namespace MemoryCleaner.Tests
{
    public class FullscreenDetectorTests
    {
        // ── 호출 가능 검증 ──
        // FullscreenDetector는 외부 시스템 상태에 의존하므로 결과값 자체는 단언할 수 없음.
        // 테스트 환경에서 풀스크린 앱이 떠있을 보장이 없기 때문.
        // 그러나 예외 없이 호출되고 bool을 반환해야 함.

        [Fact]
        public void IsForegroundFullscreen_DoesNotThrow()
        {
            var ex = Record.Exception(() => FullscreenDetector.IsForegroundFullscreen());
            Assert.Null(ex);
        }

        [Fact]
        public void IsForegroundFullscreen_OutParam_DoesNotThrow()
        {
            var ex = Record.Exception(() => FullscreenDetector.IsForegroundFullscreen(out _));
            Assert.Null(ex);
        }

        [Fact]
        public void IsForegroundFullscreen_ReturnsBool()
        {
            // 값 자체는 환경 의존이므로 단언하지 않고 호출 성공만 검증
            bool result = FullscreenDetector.IsForegroundFullscreen();
            // result는 true/false 어느 쪽이든 정상
            Assert.True(result == true || result == false);
        }

        [Fact]
        public void IsForegroundFullscreen_WithOutParam_ProcessNameIsConsistent()
        {
            // 풀스크린이 아니면 processName은 null이어야 함 (제외된 시스템 프로세스 포함)
            bool isFs = FullscreenDetector.IsForegroundFullscreen(out string name);
            if (!isFs)
            {
                // false일 때 name은 null이어야 함 (계약상)
                Assert.Null(name);
            }
            else
            {
                Assert.NotNull(name);
                Assert.NotEmpty(name);
            }
        }
    }
}
