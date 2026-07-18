using KhaozEngine.Platform;
using Xunit;

namespace KhaozEngine.Tests.Platform
{
    /// <summary>
    /// Headless coverage for <see cref="ApplicationIcon.TrySetMacDockIcon"/>'s guard behaviour. The success path
    /// actually mutates the running app's macOS Dock icon (a side effect that needs a real windowed run to see),
    /// so it stays out; these assert only the cases that return <c>false</c> cleanly on every platform:
    /// null/empty input (rejected before any Cocoa call) and undecodable bytes (NSImage cannot build an image,
    /// so it returns false without ever reaching setApplicationIconImage). None throw.
    /// </summary>
    public sealed class ApplicationIconTests
    {
        [Fact]
        public void NullBytes_ReturnsFalse() =>
            Assert.False(ApplicationIcon.TrySetMacDockIcon(null!));

        [Fact]
        public void EmptyBytes_ReturnsFalse() =>
            Assert.False(ApplicationIcon.TrySetMacDockIcon(System.Array.Empty<byte>()));

        [Fact]
        public void UndecodableBytes_ReturnsFalse_AndDoesNotThrow() =>
            Assert.False(ApplicationIcon.TrySetMacDockIcon(new byte[] { 1, 2, 3, 4, 5 }));
    }
}
