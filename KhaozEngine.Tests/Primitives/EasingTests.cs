using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class EasingTests
{
    [Fact]
    public void Endpoints_AreFixed()
    {
        Assert.Equal(0f, Easing.SmoothStep(0f));
        Assert.Equal(1f, Easing.SmoothStep(1f));
        Assert.Equal(0f, Easing.EaseInOut(0f));
        Assert.Equal(1f, Easing.EaseInOut(1f));
    }

    [Fact]
    public void Clamps_OutOfRangeInput()
    {
        Assert.Equal(0f, Easing.SmoothStep(-1f));
        Assert.Equal(1f, Easing.EaseIn(2f));
    }

    [Fact]
    public void SmoothStep_MidpointIsHalf() => Assert.Equal(0.5f, Easing.SmoothStep(0.5f), 5);
}
