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

    /// <summary>
    /// THE EXCLUSION BOX IS PART OF THE REQUEST'S IDENTITY TOO, and for the same reason the near radius is: it
    /// decides what a map CONTAINS. It rides as two <c>init</c> properties beside the near radius, so the released
    /// two-component deconstruction still compiles, and the record's own equality picks the corners up off the
    /// backing fields.
    /// </summary>
    [Fact]
    public void ExclusionBox_IsPartOfTheValueAndDefaultsToNothing()
    {
        var min = new Vector3(-0.2f, 1.8f, -0.2f);
        var max = new Vector3(0.2f, 2.2f, 0.2f);

        Assert.False(LightShadow.None.HasExclusionBox);
        Assert.False(LightShadow.Static(7).HasExclusionBox);
        Assert.False(LightShadow.Dynamic.HasExclusionBox);
        Assert.Equal(Vector3.Zero, LightShadow.Static(7).ExclusionMin);
        Assert.Equal(Vector3.Zero, LightShadow.Static(7).ExclusionMax);

        LightShadow boxed = LightShadow.Static(7, min, max);
        Assert.True(boxed.HasExclusionBox);
        Assert.Equal(min, boxed.ExclusionMin);
        Assert.Equal(max, boxed.ExclusionMax);
        Assert.Equal(LightShadowMode.Static, boxed.Mode);
        Assert.Equal(7L, boxed.Key);
        Assert.Equal(0f, boxed.NearRadius);

        Assert.Equal(boxed, LightShadow.Static(7).WithExclusionBox(min, max));
        Assert.Equal(boxed.GetHashCode(), LightShadow.Static(7).WithExclusionBox(min, max).GetHashCode());
        Assert.NotEqual(LightShadow.Static(7), boxed);
        Assert.NotEqual(LightShadow.Static(7, min, max * 1.1f), boxed);

        // The two clearances are independent and either one excludes, so a caller may set both.
        LightShadow both = LightShadow.Static(7, 0.25f).WithExclusionBox(min, max);
        Assert.Equal(0.25f, both.NearRadius);
        Assert.True(both.HasExclusionBox);
    }

    /// <summary>The corners are ORDERED per axis, so a caller handing over the two corners of its own bounds in
    /// whichever order it holds them gets the same box either way.</summary>
    [Fact]
    public void ExclusionBox_OrdersTheCornersOnEveryAxis()
    {
        var min = new Vector3(-1f, 2f, -3f);
        var max = new Vector3(4f, 5f, 6f);
        LightShadow ordered = LightShadow.Static(1, min, max);

        Assert.Equal(ordered, LightShadow.Static(1, max, min));
        Assert.Equal(ordered, LightShadow.Static(1,
            new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, max.Y, min.Z)));
        Assert.Equal(ordered, LightShadow.Static(1).WithExclusionBox(max, min));
        Assert.Equal(min, LightShadow.Static(1, max, min).ExclusionMin);
        Assert.Equal(max, LightShadow.Static(1, max, min).ExclusionMax);
    }

    /// <summary>A request is presentation, so nonsense corners leave the request EXACTLY as it was rather than
    /// throwing: a box that is not a box excludes nothing, and a light is never worth refusing to draw.</summary>
    [Fact]
    public void ExclusionBox_RefusesAnythingThatIsNotABox()
    {
        var min = new Vector3(-1f, -1f, -1f);
        var max = new Vector3(1f, 1f, 1f);

        Assert.False(LightShadow.Static(1, new Vector3(float.NaN, 0f, 0f), max).HasExclusionBox);
        Assert.False(LightShadow.Static(1, min, new Vector3(0f, float.NaN, 0f)).HasExclusionBox);
        Assert.False(LightShadow.Static(1, new Vector3(float.NegativeInfinity), new Vector3(float.PositiveInfinity))
            .HasExclusionBox);
        Assert.Equal(LightShadow.Static(1), LightShadow.Static(1, new Vector3(float.NaN), max));

        // Flat on one axis is not a box either: nothing is inside it, so it is the same as asking for none.
        Assert.False(LightShadow.Static(1, min, new Vector3(1f, -1f, 1f)).HasExclusionBox);
        Assert.False(LightShadow.Static(1, min, min).HasExclusionBox);

        // And a bad box does not take a good one away from a request that already carried it.
        LightShadow boxed = LightShadow.Static(1, min, max);
        Assert.Equal(boxed, boxed.WithExclusionBox(new Vector3(float.NaN), max));
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
