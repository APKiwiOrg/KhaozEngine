using System;
using System.Numerics;
using KhaozEngine.Effects;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for the trauma-based ScreenShake offset generator.</summary>
public class ScreenShakeTests
{
    private const float Tol = 1e-4f;

    [Fact]
    public void Add_RaisesTraumaClampedToOne()
    {
        var s = new ScreenShake();
        s.Add(0.5f);
        Assert.Equal(0.5f, s.Trauma, Tol);
        s.Add(0.7f);
        Assert.Equal(1f, s.Trauma, Tol);   // 1.2 clamped to 1
    }

    [Fact]
    public void Add_AtOrAboveOneClampsToOne()
    {
        var s = new ScreenShake();
        s.Add(2f);
        Assert.Equal(1f, s.Trauma, Tol);
    }

    [Fact]
    public void Add_IgnoresNegativeAmount()
    {
        var s = new ScreenShake();
        s.Add(0.4f);
        s.Add(-0.2f);
        Assert.Equal(0.4f, s.Trauma, Tol);
    }

    [Fact]
    public void Update_DrainsTraumaFlooredAtZero()
    {
        var s = new ScreenShake { DecayPerSecond = 1f };
        s.Add(0.3f);
        s.Update(1f);   // 0.3 - 1 < 0 -> floored to 0
        Assert.Equal(0f, s.Trauma, Tol);
    }

    [Fact]
    public void ZeroTrauma_ProducesNoOffsetOrAngle()
    {
        var s = new ScreenShake();
        s.Update(0.1f);   // trauma still 0
        Assert.Equal(Vector2.Zero, s.Offset);
        Assert.Equal(0f, s.Angle, Tol);
    }

    [Fact]
    public void Offset_BoundedByTraumaSquaredTimesMaxOffset()
    {
        var s = new ScreenShake { DecayPerSecond = 0f, MaxOffset = 30f };
        s.Add(1f);
        for (int i = 0; i < 50; i++)
        {
            s.Update(0.01f);
            Assert.True(MathF.Abs(s.Offset.X) <= 30f + Tol, $"|offX| {s.Offset.X} exceeds 30");
            Assert.True(MathF.Abs(s.Offset.Y) <= 30f + Tol, $"|offY| {s.Offset.Y} exceeds 30");
        }
    }

    [Fact]
    public void Offset_ScalesWithTraumaSquared()
    {
        var full = new ScreenShake(seed: 7) { DecayPerSecond = 0f };
        var half = new ScreenShake(seed: 7) { DecayPerSecond = 0f };
        full.Add(1f);
        half.Add(0.5f);
        full.Update(0.1f);
        half.Update(0.1f);   // same seed + same elapsed -> same noise

        float ratio = half.Offset.Length() / full.Offset.Length();
        Assert.Equal(0.25f, ratio, 1e-3f);   // (0.5^2) / (1^2)
    }

    [Fact]
    public void Offset_IsDeterministicForSameSeedAndSequence()
    {
        var a = new ScreenShake(seed: 42) { DecayPerSecond = 0f };
        var b = new ScreenShake(seed: 42) { DecayPerSecond = 0f };
        a.Add(0.8f); b.Add(0.8f);
        for (int i = 0; i < 10; i++) { a.Update(0.02f); b.Update(0.02f); }
        Assert.Equal(a.Offset.X, b.Offset.X, Tol);
        Assert.Equal(a.Offset.Y, b.Offset.Y, Tol);
    }

    [Fact]
    public void Offset_OscillatesSignOverTime()
    {
        var s = new ScreenShake { DecayPerSecond = 0f, Frequency = 25f, MaxOffset = 30f };
        s.Add(1f);
        bool sawPositive = false, sawNegative = false;
        for (int i = 0; i < 200; i++)
        {
            s.Update(0.005f);
            if (s.Offset.X > 1f) sawPositive = true;
            if (s.Offset.X < -1f) sawNegative = true;
        }
        Assert.True(sawPositive && sawNegative, "offset X should swing both signs (it shakes, not pushes)");
    }

    [Fact]
    public void MaxAngleZero_ProducesNoAngle()
    {
        var s = new ScreenShake { DecayPerSecond = 0f, MaxAngle = 0f };
        s.Add(1f);
        for (int i = 0; i < 20; i++)
        {
            s.Update(0.02f);
            Assert.Equal(0f, s.Angle, Tol);
        }
    }

    [Fact]
    public void Angle_NonZeroAndBoundedWhenActive()
    {
        var s = new ScreenShake(seed: 3) { DecayPerSecond = 0f, MaxAngle = 0.2f };
        s.Add(1f);
        bool sawNonZero = false;
        for (int i = 0; i < 100; i++)
        {
            s.Update(0.01f);
            Assert.True(MathF.Abs(s.Angle) <= 0.2f + Tol, $"|angle| {s.Angle} exceeds trauma^2*MaxAngle");
            if (MathF.Abs(s.Angle) > 1e-3f) sawNonZero = true;
        }
        Assert.True(sawNonZero, "angle should be non-zero at some point when MaxAngle>0 and trauma>0");
    }
}
