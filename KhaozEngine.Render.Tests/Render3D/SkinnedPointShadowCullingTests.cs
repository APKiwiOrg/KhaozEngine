using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedPointShadowCullingTests
{
    [Theory]
    [InlineData(0f, 0f, 0f, true)]
    [InlineData(20f, 0f, 0f, false)]
    public void OuterSphereControlsRetention(float x, float y, float z, bool expected)
    {
        var caster = new PointShadowCasterSphere(new Vector3(x, y, z), 1.5f);

        Assert.Equal(expected,
            caster.TouchesShadowingShell(Vector3.Zero, 8f, 0f, default, default));
    }

    [Fact]
    public void WhollyInsideNearRadiusOrExclusionBoxIsRejectedButPartialOverlapIsKept()
    {
        var inner = new PointShadowCasterSphere(new Vector3(0.5f, 0f, 0f), 0.25f);
        var partial = new PointShadowCasterSphere(new Vector3(1.9f, 0f, 0f), 0.25f);

        Assert.False(inner.TouchesShadowingShell(Vector3.Zero, 8f, 1f, default, default));
        Assert.True(partial.TouchesShadowingShell(Vector3.Zero, 8f, 2f, default, default));
        Assert.False(inner.TouchesShadowingShell(
            Vector3.Zero, 8f, 0f, new Vector3(-1f), new Vector3(1f)));
        Assert.True(partial.TouchesShadowingShell(
            Vector3.Zero, 8f, 0f, new Vector3(-2f), new Vector3(2f)));
    }

    [Fact]
    public void RestBoundsProduceTheInflatedAbsoluteSphereAtLargeOrigins()
    {
        var bounds = new MeshBounds(new Vector3(-1f), new Vector3(1f));
        Matrix4x4 world = Matrix4x4.CreateTranslation(100_000f, 2f, -100_000f);

        PointShadowCasterSphere sphere = PointShadowCasterSphere.FromRestBounds(
            bounds, world, Scene3D.SkinnedCullSafetyFactor);

        Assert.Equal(new Vector3(100_000f, 2f, -100_000f), sphere.Center);
        Assert.Equal(2.598076f, sphere.Radius, precision: 5);
    }
}
