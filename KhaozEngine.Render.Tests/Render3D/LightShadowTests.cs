using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Device-free coverage of the point-light shadow REQUEST: the value a caller attaches to a light, and what the
/// scene's per-frame light queue does with it. The atlas, the pass and the receiver sampling are elsewhere. What
/// matters here is that the four-argument <see cref="Scene3D.AddLight(Vector3,Color,float,float)"/> stays the
/// unshadowed path it always was, so every existing scene queues <see cref="LightShadow.None"/> without saying so.
/// </summary>
public sealed class LightShadowTests
{
    [Fact]
    public void None_IsTheDefaultValueAndRequestsNothing()
    {
        Assert.Equal(default, LightShadow.None);
        Assert.Equal(LightShadowMode.None, LightShadow.None.Mode);
        Assert.Equal(0L, LightShadow.None.Key);
        Assert.False(LightShadow.None.Requested);
        Assert.False(default(LightShadow).Requested);
    }

    [Fact]
    public void Static_CarriesItsCallerKey()
    {
        LightShadow shadow = LightShadow.Static(7);
        Assert.Equal(LightShadowMode.Static, shadow.Mode);
        Assert.Equal(7L, shadow.Key);
        Assert.True(shadow.Requested);
        Assert.Equal(LightShadow.Static(7), shadow);
        Assert.NotEqual(LightShadow.Static(8), shadow);
    }

    [Fact]
    public void Dynamic_IsRequestedAndCarriesNoKey()
    {
        Assert.Equal(LightShadowMode.Dynamic, LightShadow.Dynamic.Mode);
        Assert.Equal(0L, LightShadow.Dynamic.Key);
        Assert.True(LightShadow.Dynamic.Requested);
        Assert.NotEqual(LightShadow.Static(0), LightShadow.Dynamic);
    }

    /// <summary>
    /// THE NEAR RADIUS IS PART OF THE REQUEST'S IDENTITY. It rides as an <c>init</c> property rather than a third
    /// positional component, so the two-component deconstruction released in 19.2.0 still compiles, and the
    /// record's own equality picks it up off the backing field. A light whose fixture clearance changed is a
    /// different request, and the static cache leans on exactly that.
    /// </summary>
    [Fact]
    public void NearRadius_IsPartOfTheValueAndDefaultsToZero()
    {
        Assert.Equal(0f, LightShadow.None.NearRadius);
        Assert.Equal(0f, LightShadow.Static(7).NearRadius);
        Assert.Equal(0f, LightShadow.Dynamic.NearRadius);

        Assert.Equal(0.25f, LightShadow.Static(7, 0.25f).NearRadius);
        Assert.Equal(LightShadowMode.Static, LightShadow.Static(7, 0.25f).Mode);
        Assert.Equal(7L, LightShadow.Static(7, 0.25f).Key);
        Assert.Equal(0.25f, LightShadow.DynamicWithNearRadius(0.25f).NearRadius);
        Assert.Equal(LightShadowMode.Dynamic, LightShadow.DynamicWithNearRadius(0.25f).Mode);
        Assert.Equal(0.25f, LightShadow.Static(7).WithNearRadius(0.25f).NearRadius);

        Assert.Equal(LightShadow.Static(7, 0.25f), LightShadow.Static(7).WithNearRadius(0.25f));
        Assert.NotEqual(LightShadow.Static(7), LightShadow.Static(7, 0.25f));
        Assert.NotEqual(LightShadow.Static(7, 0.2f), LightShadow.Static(7, 0.25f));
        Assert.Equal(LightShadow.Static(7, 0.25f).GetHashCode(),
            LightShadow.Static(7).WithNearRadius(0.25f).GetHashCode());
    }

    /// <summary>A request is presentation, so a nonsense clearance is CLAMPED rather than thrown on: the light
    /// still renders, with no fixture cut out of its map.</summary>
    [Fact]
    public void NearRadius_ClampsAnythingThatIsNotAPositiveDistanceToZero()
    {
        Assert.Equal(0f, LightShadow.Static(1, -0.5f).NearRadius);
        Assert.Equal(0f, LightShadow.Static(1, float.NaN).NearRadius);
        Assert.Equal(0f, LightShadow.Static(1, float.NegativeInfinity).NearRadius);
        Assert.Equal(0f, LightShadow.Static(1, float.PositiveInfinity).NearRadius);
        Assert.Equal(0f, LightShadow.DynamicWithNearRadius(-1f).NearRadius);
        Assert.Equal(LightShadow.Static(1), LightShadow.Static(1, -0.5f));
    }

    [Fact]
    public void TheFourArgumentOverload_QueuesAnUnshadowedLight()
    {
        using var rig = new LightQueueRig();

        rig.Scene.AddLight(Vector3.Zero, new Color(1f, 1f, 1f, 1f), 4f, 2f);

        Assert.Equal(1, rig.Scene.LightCount);
        Assert.Equal(LightShadow.None, rig.Scene.LightShadowAt(0));
    }

    [Fact]
    public void TheFiveArgumentOverload_KeepsTheRequestedShadow()
    {
        using var rig = new LightQueueRig();

        rig.Scene.AddLight(Vector3.Zero, new Color(1f, 1f, 1f, 1f), 4f, 2f, LightShadow.Static(11));
        rig.Scene.AddLight(Vector3.One, new Color(1f, 1f, 1f, 1f), 4f, 2f, LightShadow.Dynamic);
        rig.Scene.AddLight(-Vector3.One, new Color(1f, 1f, 1f, 1f), 4f, 2f, LightShadow.None);
        rig.Scene.AddLight(Vector3.UnitY, new Color(1f, 1f, 1f, 1f), 4f, 2f, LightShadow.Static(12, 0.3f));

        Assert.Equal(4, rig.Scene.LightCount);
        Assert.Equal(LightShadow.Static(11), rig.Scene.LightShadowAt(0));
        Assert.Equal(LightShadow.Dynamic, rig.Scene.LightShadowAt(1));
        Assert.Equal(LightShadow.None, rig.Scene.LightShadowAt(2));
        Assert.Equal(0.3f, rig.Scene.LightShadowAt(3).NearRadius);
    }

    [Fact]
    public void LightShadowAt_OutOfRangeReadsNone()
    {
        using var rig = new LightQueueRig();

        rig.Scene.AddLight(Vector3.Zero, new Color(1f, 1f, 1f, 1f), 4f, 2f, LightShadow.Dynamic);

        Assert.Equal(LightShadow.None, rig.Scene.LightShadowAt(-1));
        Assert.Equal(LightShadow.None, rig.Scene.LightShadowAt(1));
    }

    [Fact]
    public void Begin_ClearsTheQueuedShadowRequests()
    {
        using var rig = new LightQueueRig();
        rig.Scene.AddLight(Vector3.Zero, new Color(1f, 1f, 1f, 1f), 4f, 2f, LightShadow.Static(3));

        rig.Scene.Begin();

        Assert.Equal(0, rig.Scene.LightCount);
        Assert.Equal(LightShadow.None, rig.Scene.LightShadowAt(0));
    }

    /// <summary>A scene on the fake device, which is all the light queue needs: nothing here renders.</summary>
    sealed class LightQueueRig : System.IDisposable
    {
        readonly FakeGpuDevice _device = new();
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;

        internal LightQueueRig()
        {
            _targetTexture = _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = _device.Factory.CreateFramebuffer(null, _targetTexture);
            Scene = new Scene3D(_device, _target.Outputs);
        }

        internal Scene3D Scene { get; }

        public void Dispose()
        {
            Scene.Dispose();
            _target.Dispose();
            _targetTexture.Dispose();
            _device.Dispose();
        }
    }
}
