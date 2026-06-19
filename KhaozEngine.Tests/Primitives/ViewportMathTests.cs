using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class ViewportMathTests
{
    [Fact]
    public void Fit_WiderSource_LimitedByWidth()
        => Assert.Equal(0.5f, ViewportMath.Fit(200, 100, 100, 100));

    [Fact]
    public void Fit_TallerSource_LimitedByHeight()
        => Assert.Equal(0.5f, ViewportMath.Fit(100, 200, 100, 100));

    [Fact]
    public void Cover_UsesMaxRatio()
        => Assert.Equal(1f, ViewportMath.Cover(200, 100, 100, 100));
}
