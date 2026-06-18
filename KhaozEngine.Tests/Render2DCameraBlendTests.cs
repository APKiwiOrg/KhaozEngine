using System;
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for eased camera blends (CameraState, Easing, CameraBlend).</summary>
public class Render2DCameraBlendTests
{
    private const float Tol = 1e-4f;

    private static void AssertState(Camera2D cam, Vector2 pos, float zoom, float rot)
    {
        Assert.True(Vector2.Distance(pos, cam.Position) <= 1e-3f, $"pos expected {pos}, got {cam.Position}");
        Assert.Equal(zoom, cam.Zoom, 1e-3f);
        Assert.Equal(rot, cam.Rotation, 1e-3f);
    }

    // ---- CameraState ----

    [Fact]
    public void State_FromCapturesCameraFields()
    {
        var cam = new Camera2D { Position = new Vector2(3f, 4f), Zoom = 2f, Rotation = 0.5f };
        var s = CameraState.From(cam);
        Assert.Equal(new Vector2(3f, 4f), s.Position);
        Assert.Equal(2f, s.Zoom, Tol);
        Assert.Equal(0.5f, s.Rotation, Tol);
    }

    [Fact]
    public void State_ApplyToWritesCameraFields()
    {
        var cam = new Camera2D();
        new CameraState(new Vector2(7f, -2f), 3f, 1.25f).ApplyTo(cam);
        AssertState(cam, new Vector2(7f, -2f), 3f, 1.25f);
    }

    [Fact]
    public void State_LerpInterpolatesPerField()
    {
        var a = new CameraState(new Vector2(0f, 0f), 1f, 0f);
        var b = new CameraState(new Vector2(100f, 50f), 3f, 1f);

        var at0 = CameraState.Lerp(a, b, 0f);   // t=0 returns a exactly
        Assert.Equal(new Vector2(0f, 0f), at0.Position);
        Assert.Equal(1f, at0.Zoom, Tol);
        Assert.Equal(0f, at0.Rotation, Tol);

        var at1 = CameraState.Lerp(a, b, 1f);
        Assert.Equal(new Vector2(100f, 50f), at1.Position);
        Assert.Equal(3f, at1.Zoom, Tol);
        Assert.Equal(1f, at1.Rotation, Tol);

        var mid = CameraState.Lerp(a, b, 0.5f);
        Assert.Equal(new Vector2(50f, 25f), mid.Position);
        Assert.Equal(2f, mid.Zoom, Tol);
        Assert.Equal(0.5f, mid.Rotation, Tol);
    }

    // ---- Easing ----

    [Fact]
    public void Easing_EndpointsAreZeroAndOne()
    {
        Func<float, float>[] curves = { Easing.Linear, Easing.SmoothStep, Easing.EaseIn, Easing.EaseOut, Easing.EaseInOut };
        foreach (var f in curves)
        {
            Assert.Equal(0f, f(0f), Tol);
            Assert.Equal(1f, f(1f), Tol);
        }
    }

    [Fact]
    public void Easing_ClampsInputOutsideUnitInterval()
    {
        Func<float, float>[] curves = { Easing.Linear, Easing.SmoothStep, Easing.EaseIn, Easing.EaseOut, Easing.EaseInOut };
        foreach (var f in curves)
        {
            Assert.Equal(0f, f(-1f), Tol);
            Assert.Equal(1f, f(2f), Tol);
        }
    }

    [Fact]
    public void Easing_HasExpectedMidpointShapes()
    {
        Assert.Equal(0.3f, Easing.Linear(0.3f), Tol);
        Assert.Equal(0.5f, Easing.SmoothStep(0.5f), Tol);
        Assert.Equal(0.25f, Easing.EaseIn(0.5f), Tol);
        Assert.Equal(0.75f, Easing.EaseOut(0.5f), Tol);
        Assert.Equal(0.5f, Easing.EaseInOut(0.5f), Tol);
    }

    [Fact]
    public void Easing_IsMonotonicNonDecreasing()
    {
        Func<float, float>[] curves = { Easing.Linear, Easing.SmoothStep, Easing.EaseIn, Easing.EaseOut, Easing.EaseInOut };
        foreach (var f in curves)
        {
            float prev = f(0f);
            for (float t = 0.05f; t <= 1f; t += 0.05f)
            {
                float cur = f(t);
                Assert.True(cur >= prev - 1e-5f, $"curve not monotonic at t={t}: {cur} < {prev}");
                prev = cur;
            }
        }
    }
}
