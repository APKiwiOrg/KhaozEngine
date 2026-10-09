using System;
using KhaozEngine.Platform;
using Xunit;

namespace KhaozEngine.Tests.Platform;

public sealed class WindowsConsoleHandleTests
{
    // Missing handles are not intentional redirection. Valid non-console devices are.
    [Theory]
    [InlineData(0, 0, 0, false, false)]
    [InlineData(-1, 0, 6, false, false)]
    [InlineData(123, 0, 6, false, false)]
    [InlineData(123, 1, 0, false, true)]
    [InlineData(123, 3, 0, false, true)]
    [InlineData(123, 2, 0, true, false)]
    [InlineData(123, 2, 0, false, true)]
    [InlineData(123, 0, 0, false, true)]
    [InlineData(123, 0, 5, false, true)]
    public void IsRedirected_PreservesValidDestinationsButAllowsMissingHandles(
        int handle, uint fileType, int error, bool consoleMode, bool expected)
    {
        Assert.Equal(expected, WindowsConsoleHandle.IsRedirected(new IntPtr(handle), fileType, error, consoleMode));
    }
}
