using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class MotionMathTests
{
    [Fact]
    public void TheSentinelIsHalfMaxAndSitsAboveTheBackgroundThreshold()
    {
        Assert.Equal(MotionMath.Sentinel, (float)Half.MaxValue);
        Assert.True(MotionMath.BackgroundThreshold < MotionMath.Sentinel);
        Assert.Equal(new Color(65504f, 65504f, 0f, 0f), MotionMath.SentinelColor);
        Assert.True(MotionMath.IsBackground(new Vector2(MotionMath.Sentinel, MotionMath.Sentinel)));
        // Two whole screens of motion in either direction is still motion.
        Assert.False(MotionMath.IsBackground(new Vector2(-2f, 2f)));
        Assert.Equal(GpuPixelFormat.R16G16Float, MotionMath.Format);
    }

    [Fact]
    public void AnOutputDescriptionIsTemporalWhenItCarriesAFourthColourAttachment()
    {
        Assert.False(MotionMath.IsTemporal(new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt,
            GpuPixelFormat.R16G16B16A16Float, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float)));
        Assert.True(MotionMath.IsTemporal(new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt,
            GpuPixelFormat.R16G16B16A16Float, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float, MotionMath.Format)));
    }

    [Fact]
    public void UvMotionRunsRightwardAndDownTheImage()
    {
        // Right in NDC is right in UV. Up in NDC is up the image, which is negative V. The w divide happens first.
        Assert.Equal(new Vector2(.25f, 0f), MotionMath.UvMotion(new Vector4(.5f, 0f, 0f, 1f), new Vector4(0f, 0f, 0f, 1f)));
        Assert.Equal(new Vector2(0f, -.25f), MotionMath.UvMotion(new Vector4(0f, 1f, 0f, 2f), new Vector4(0f, 0f, 0f, 1f)));
        Assert.Equal(Vector2.Zero, MotionMath.UvMotion(new Vector4(.3f, -.2f, .5f, 3f), new Vector4(.3f, -.2f, .5f, 3f)));
    }

    [Fact]
    public void AReadbackIsRowMajorFromTheTopAndScalesToInternalPixels()
    {
        var motion = new Vector2[6];
        motion[1 * 3 + 2] = new Vector2(.5f, -.25f);
        motion[0] = new Vector2(MotionMath.Sentinel, MotionMath.Sentinel);
        var readback = new MotionTargetReadback(motion, 3, 2);

        Assert.Equal(new Vector2(1.5f, -.5f), readback.PixelsAt(2, 1));
        Assert.True(readback.IsBackground(0, 0));
        Assert.False(readback.IsBackground(2, 1));
        (Vector2[] m, int w, int h) = readback;   // the shape group F deconstructs
        Assert.Same(motion, m);
        Assert.Equal((3, 2), (w, h));
    }
}
