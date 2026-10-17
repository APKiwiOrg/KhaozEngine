using KhaozEngine.BugReports.Client.Internal;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

public sealed class CaptureSizeTests
{
    [Theory]
    [InlineData(2560, 1440, 960, 960, 540)]
    [InlineData(1440, 2560, 960, 540, 960)]
    [InlineData(800, 600, 960, 800, 600)]
    [InlineData(3001, 7, 960, 960, 2)]
    [InlineData(1, 5000, 960, 1, 960)]
    public void Fit_scales_the_long_edge_down_and_never_up(int w, int h, int edge, int ew, int eh)
        => Assert.Equal((ew, eh), CaptureSize.Fit(w, h, edge));
}
