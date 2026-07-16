using System;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class DelegateSurfaceProviderTests
{
    [Fact]
    public void Ctor_NullDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DelegateSurfaceProvider(null!));
    }

    [Fact]
    public void TrySample_ForwardsHeightAndHeadroom()
    {
        bool SampleDelegate(float x, float z, out float height, out float headroom)
        {
            height = 3f;
            headroom = 5f;
            return true;
        }

        var provider = new DelegateSurfaceProvider(SampleDelegate);
        bool result = provider.TrySample(1f, 2f, out float h, out float hr);

        Assert.True(result);
        Assert.Equal(3f, h);
        Assert.Equal(5f, hr);
    }

    [Fact]
    public void TrySample_FalseWhenDelegateFalse()
    {
        bool SampleDelegate(float x, float z, out float height, out float headroom)
        {
            height = 0f;
            headroom = 0f;
            return false;
        }

        var provider = new DelegateSurfaceProvider(SampleDelegate);
        bool result = provider.TrySample(1f, 2f, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TrySample_PassesCoordinatesThrough()
    {
        float capturedX = float.NaN;
        float capturedZ = float.NaN;

        bool SampleDelegate(float x, float z, out float height, out float headroom)
        {
            capturedX = x;
            capturedZ = z;
            height = x;
            headroom = z;
            return true;
        }

        var provider = new DelegateSurfaceProvider(SampleDelegate);
        provider.TrySample(1.5f, 2.5f, out float h, out float hr);

        Assert.Equal(1.5f, capturedX);
        Assert.Equal(2.5f, capturedZ);
        Assert.Equal(1.5f, h);
        Assert.Equal(2.5f, hr);
    }
}
